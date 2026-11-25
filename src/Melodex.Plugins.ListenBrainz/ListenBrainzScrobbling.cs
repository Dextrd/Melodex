using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Scrobbling;

namespace Melodex.Plugins.ListenBrainz;

/// <summary>Entry point for the ListenBrainz scrobbling plugin.</summary>
public sealed class ListenBrainzScrobblingFactory : IPluginFactory
{
    public const string PluginId = "melodex.scrobbling.listenbrainz";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "ListenBrainz",
        Version = "0.1.0",
        Type = PluginType.Scrobbling,
        ContractVersion = ContractVersions.Scrobbling.ToString(),
        Description = "Sends what each person plays to their ListenBrainz account, once they've linked it on their account page with their user token. Free and open; nothing to set up here.",
        Homepage = "https://listenbrainz.org",
        Capabilities = [ScrobblingCapabilities.LinkToken, ScrobblingCapabilities.NowPlaying],
        Access = new PluginAccess { Network = ["api.listenbrainz.org"] },
    };

    public IPlugin Create(PluginContext context) => new ListenBrainzScrobbling(context);
}

/// <summary>
/// Sends plays to ListenBrainz, the MetaBrainz Foundation's open listening history, with each person's user token, which
/// they copy from their ListenBrainz settings.
/// </summary>
public sealed class ListenBrainzScrobbling(PluginContext context) : IScrobblingPlugin
{
    public const string Api = "https://api.listenbrainz.org/1/";

    private static readonly string Version = typeof(ListenBrainzScrobbling).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private readonly HttpClient _http = context.HttpClient;

    public async Task<LinkedAccount> LinkAsync(string token, CancellationToken cancellationToken)
    {
        token = token.Trim();
        using var request = new HttpRequestMessage(HttpMethod.Get, Api + "validate-token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (body?["valid"]?.GetValue<bool>() == true && (string?)body["user_name"] is { Length: > 0 } user)
                return new LinkedAccount(user, token);
            throw new ScrobbleLinkException("ListenBrainz doesn't know that token. Copy it from your ListenBrainz settings.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new ScrobbleLinkException($"ListenBrainz couldn't be reached: {e.Message}");
        }
    }

    public Task ScrobbleAsync(string secret, IReadOnlyList<Listen> listens, CancellationToken cancellationToken) =>
        SubmitAsync(secret, listens.Count == 1 ? "single" : "import", listens, cancellationToken);

    public Task NowPlayingAsync(string secret, Listen listen, CancellationToken cancellationToken) =>
        SubmitAsync(secret, "playing_now", [listen], cancellationToken);

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(Api + "validate-token", cancellationToken);
            // With no token it says so, which is enough to know it's there.
            return (int)response.StatusCode < 500 ? HealthReport.Healthy() : new HealthReport(HealthStatus.Unhealthy, $"ListenBrainz answered {(int)response.StatusCode}.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return new HealthReport(HealthStatus.Unhealthy, e.Message);
        }
    }

    private async Task SubmitAsync(string token, string type, IReadOnlyList<Listen> listens, CancellationToken ct)
    {
        var payload = new JsonArray();
        foreach (var listen in listens)
        {
            var info = new JsonObject
            {
                ["media_player"] = listen.Player,
                ["submission_client"] = "Melodex",
                ["submission_client_version"] = Version,
            };
            if (listen.RecordingMbid is { } recording)
                info["recording_mbid"] = recording;
            if (listen.ReleaseMbid is { } release)
                info["release_mbid"] = release;
            if (listen.Duration is { } duration && duration > TimeSpan.Zero)
                info["duration_ms"] = (long)Math.Round(duration.TotalMilliseconds);
            var metadata = new JsonObject { ["artist_name"] = listen.Artist, ["track_name"] = listen.Title, ["additional_info"] = info };
            if (listen.Album is { Length: > 0 } album)
                metadata["release_name"] = album;
            var entry = new JsonObject { ["track_metadata"] = metadata };
            if (type != "playing_now")
                entry["listened_at"] = listen.PlayedAt.ToUnixTimeSeconds();
            payload.Add(entry);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, Api + "submit-listens")
        {
            Content = new StringContent(new JsonObject { ["listen_type"] = type, ["payload"] = payload }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        using var response = await _http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
            return;
        var error = await ErrorOfAsync(response, ct);
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new ScrobbleLinkRefusedException("ListenBrainz no longer takes this token: link it again."),
            HttpStatusCode.BadRequest => new ScrobbleRejectedException($"ListenBrainz turned them down: {error}"),
            _ => new HttpRequestException($"ListenBrainz answered {(int)response.StatusCode}: {error}"),
        };
    }

    private static async Task<string> ErrorOfAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return (string?)JsonNode.Parse(text)?["error"] ?? response.ReasonPhrase ?? "";
        }
        catch (JsonException)
        {
            return response.ReasonPhrase ?? "";
        }
    }
}
