using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Scrobbling;

namespace Melodex.Plugins.LastFm;

/// <summary>Entry point for the Last.fm scrobbling plugin.</summary>
public sealed class LastFmScrobblingFactory : IPluginFactory
{
    public const string PluginId = "melodex.scrobbling.lastfm";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "Last.fm",
        Version = "0.1.0",
        Type = PluginType.Scrobbling,
        ContractVersion = ContractVersions.Scrobbling.ToString(),
        Description = "Sends what each person plays to their Last.fm account, once they've linked it on their account page. Needs an API account of your own, which is free.",
        Homepage = "https://www.last.fm",
        Capabilities = [ScrobblingCapabilities.LinkWeb, ScrobblingCapabilities.NowPlaying],
        Access = new PluginAccess { Network = ["ws.audioscrobbler.com", "www.last.fm"] },
        MaxRequestsPerSecond = 5,
        SettingsSchema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "apiKey": {
                  "type": "string",
                  "title": "API key",
                  "description": "Make an API account at last.fm/api/account/create and copy its API key here. It can be the same one as for the Last.fm discovery plugin."
                },
                "secret": {
                  "type": "string",
                  "title": "Shared secret",
                  "description": "The API account's shared secret, which signs what's sent.",
                  "writeOnly": true
                }
              },
              "required": ["apiKey", "secret"]
            }
            """).RootElement,
    };

    public IPlugin Create(PluginContext context) => new LastFmScrobbling(context);
}

/// <summary>
/// Sends plays to Last.fm. Someone links their account on Last.fm's own page, which sends them back with a token that's
/// turned into a session key; plays and what's playing now go with it, signed with the API account's shared secret.
/// </summary>
public sealed class LastFmScrobbling(PluginContext context) : IScrobblingPlugin
{
    public const string Api = "https://ws.audioscrobbler.com/2.0/";
    public const string Auth = "https://www.last.fm/api/auth/";

    private readonly HttpClient _http = context.HttpClient;
    private readonly string _apiKey = Setting(context, "apiKey");
    private readonly string _secret = Setting(context, "secret");

    private static string Setting(PluginContext context, string name) =>
        context.Settings.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";

    public Task<Uri> LinkPageAsync(Uri callback, CancellationToken cancellationToken) =>
        Task.FromResult(new Uri($"{Auth}?api_key={Uri.EscapeDataString(_apiKey)}&cb={Uri.EscapeDataString(callback.ToString())}"));

    public async Task<LinkedAccount> LinkAsync(string token, CancellationToken cancellationToken)
    {
        var body = await CallAsync(new() { ["method"] = "auth.getSession", ["token"] = token.Trim() }, HttpMethod.Get, cancellationToken);
        if ((string?)body["session"]?["name"] is { Length: > 0 } name && (string?)body["session"]?["key"] is { Length: > 0 } key)
            return new LinkedAccount(name, key);
        throw new ScrobbleLinkException("Last.fm didn't give a session.");
    }

    public async Task ScrobbleAsync(string secret, IReadOnlyList<Listen> listens, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string> { ["method"] = "track.scrobble", ["sk"] = secret };
        // Last.fm takes only songs longer than half a minute.
        var sent = listens.Where(l => l.Duration is null || l.Duration > TimeSpan.FromSeconds(30)).ToList();
        if (sent.Count == 0)
            return;
        for (var i = 0; i < sent.Count; i++)
        {
            var listen = sent[i];
            parameters[$"artist[{i}]"] = listen.Artist;
            parameters[$"track[{i}]"] = listen.Title;
            parameters[$"timestamp[{i}]"] = listen.PlayedAt.ToUnixTimeSeconds().ToString();
            if (listen.Album is { Length: > 0 } album)
                parameters[$"album[{i}]"] = album;
            if (listen.Duration is { } duration && duration > TimeSpan.Zero)
                parameters[$"duration[{i}]"] = ((int)Math.Round(duration.TotalSeconds)).ToString();
            if (listen.RecordingMbid is { } mbid)
                parameters[$"mbid[{i}]"] = mbid;
            parameters[$"chosenByUser[{i}]"] = "1";
        }
        await CallAsync(parameters, HttpMethod.Post, cancellationToken);
    }

    public async Task NowPlayingAsync(string secret, Listen listen, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>
        {
            ["method"] = "track.updateNowPlaying", ["sk"] = secret, ["artist"] = listen.Artist, ["track"] = listen.Title,
        };
        if (listen.Album is { Length: > 0 } album)
            parameters["album"] = album;
        if (listen.Duration is { } duration && duration > TimeSpan.Zero)
            parameters["duration"] = ((int)Math.Round(duration.TotalSeconds)).ToString();
        await CallAsync(parameters, HttpMethod.Post, cancellationToken);
    }

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (_apiKey.Length == 0 || _secret.Length == 0)
            return new HealthReport(HealthStatus.Unhealthy, "Add your Last.fm API key and shared secret.");
        try
        {
            // A token for linking needs a key Last.fm takes, and a right signature.
            await CallAsync(new() { ["method"] = "auth.getToken" }, HttpMethod.Get, cancellationToken);
            return HealthReport.Healthy();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ScrobbleLinkException)
        {
            return new HealthReport(HealthStatus.Unhealthy, e.Message);
        }
    }

    /// <summary>A signed Last.fm call, as JSON.</summary>
    private async Task<JsonNode> CallAsync(Dictionary<string, string> parameters, HttpMethod method, CancellationToken ct)
    {
        if (_apiKey.Length == 0 || _secret.Length == 0)
            throw new ScrobbleLinkException("The admin hasn't set the Last.fm plugin's API key and shared secret.");
        parameters["api_key"] = _apiKey;
        parameters["api_sig"] = Signature(parameters, _secret);
        parameters["format"] = "json";
        using var request = method == HttpMethod.Get
            ? new HttpRequestMessage(HttpMethod.Get, Api + "?" + string.Join('&', parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}")))
            : new HttpRequestMessage(HttpMethod.Post, Api) { Content = new FormUrlEncodedContent(parameters) };
        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? body;
        try
        {
            body = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            throw new HttpRequestException($"Last.fm answered {(int)response.StatusCode}.");
        }
        if (body?["error"] is { } code)
        {
            var message = (string?)body["message"] ?? "";
            var linking = parameters["method"] is "auth.getSession" or "auth.getToken";
            throw code.GetValue<int>() switch
            {
                // Invalid session or authentication: the person has to link again.
                4 or 9 or 14 or 15 => linking
                    ? new ScrobbleLinkException($"Last.fm turned it down: {message}. Try linking again.")
                    : new ScrobbleLinkRefusedException($"Last.fm no longer takes this link ({message}): link it again."),
                6 or 7 => new ScrobbleRejectedException($"Last.fm turned them down: {message}"),
                10 or 26 => linking
                    ? new ScrobbleLinkException($"Last.fm doesn't take the plugin's API key: {message}")
                    : new HttpRequestException($"Last.fm doesn't take the plugin's API key: {message}"),
                13 => linking
                    ? new ScrobbleLinkException("Last.fm says the signature is wrong: check the plugin's shared secret.")
                    : new HttpRequestException("Last.fm says the signature is wrong: check the plugin's shared secret."),
                _ => linking ? new ScrobbleLinkException($"Last.fm: {message}") : new HttpRequestException($"Last.fm: {message}"),
            };
        }
        if (!response.IsSuccessStatusCode || body is null)
            throw new HttpRequestException($"Last.fm answered {(int)response.StatusCode}.");
        return body;
    }

    /// <summary>Last.fm's signature: MD5 of every parameter's name and value, sorted by name, then the shared secret.</summary>
    public static string Signature(IReadOnlyDictionary<string, string> parameters, string secret)
    {
        var text = new StringBuilder();
        foreach (var (name, value) in parameters.Where(p => p.Key is not ("format" or "callback")).OrderBy(p => p.Key, StringComparer.Ordinal))
            text.Append(name).Append(value);
        text.Append(secret);
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
