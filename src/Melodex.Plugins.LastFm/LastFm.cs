using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Discovery;

namespace Melodex.Plugins.LastFm;

/// <summary>Entry point for the Last.fm discovery plugin.</summary>
public sealed class LastFmFactory : IPluginFactory
{
    public const string PluginId = "melodex.discovery.lastfm";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "Last.fm",
        Version = "0.1.0",
        Type = PluginType.Discovery,
        ContractVersion = ContractVersions.Discovery.ToString(),
        Description = "Similar artists and songs from Last.fm, from what its listeners play. Needs an API key of your own, which is free.",
        Homepage = "https://www.last.fm",
        Capabilities = [DiscoveryCapabilities.SimilarArtists, DiscoveryCapabilities.SimilarRecordings],
        Access = new PluginAccess { Network = ["ws.audioscrobbler.com"] },
        MaxRequestsPerSecond = 5,
        SettingsSchema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "apiKey": {
                  "type": "string",
                  "title": "API key",
                  "description": "Make an API account at last.fm/api/account/create and copy its API key here. It can be the same one as for sending plays to Last.fm."
                }
              },
              "required": ["apiKey"]
            }
            """).RootElement,
    };

    public IPlugin Create(PluginContext context) => new LastFm(context);
}

/// <summary>
/// Last.fm's similar artists and tracks, from its listeners' scrobbles. It finds them by MusicBrainz ID where it has one,
/// else by name, and answers with names, and MusicBrainz IDs when it knows them.
/// </summary>
public sealed class LastFm(PluginContext context) : IDiscoveryPlugin
{
    public const string Api = "https://ws.audioscrobbler.com/2.0/";

    // Last.fm's error codes: no such artist or track, and a key it doesn't take.
    private const int InvalidParameters = 6;
    private const int InvalidApiKey = 10;
    private const int SuspendedApiKey = 26;

    private readonly HttpClient _http = context.HttpClient;
    private readonly string _apiKey = context.Settings.TryGetProperty("apiKey", out var key) && key.ValueKind == JsonValueKind.String ? key.GetString()!.Trim() : "";

    public async Task<IReadOnlyList<DiscoveredArtist>> SimilarArtistsAsync(DiscoveryArtist artist, int count, CancellationToken cancellationToken)
    {
        var body = await AskAsync("artist.getsimilar", Mbid(artist.Ids), new() { ["artist"] = artist.Name }, count, cancellationToken);
        return body is { } found && found.TryGetProperty("similarartists", out var similar) ? ReadArtists(similar, count) : [];
    }

    public async Task<IReadOnlyList<DiscoveredRecording>> SimilarRecordingsAsync(DiscoveryRecording recording, int count, CancellationToken cancellationToken)
    {
        var artist = recording.Artists.FirstOrDefault()?.Name ?? recording.Artist;
        var body = await AskAsync("track.getsimilar", Mbid(recording.Ids), new() { ["artist"] = artist, ["track"] = recording.Title }, count, cancellationToken);
        return body is { } found && found.TryGetProperty("similartracks", out var similar) ? ReadTracks(similar, count) : [];
    }

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        if (_apiKey.Length == 0)
            return new HealthReport(HealthStatus.Unhealthy, "Add your Last.fm API key.");
        try
        {
            var similar = await SimilarArtistsAsync(new DiscoveryArtist("Portishead", []), 1, cancellationToken);
            return similar.Count > 0
                ? HealthReport.Healthy()
                : new HealthReport(HealthStatus.Degraded, "Last.fm answered, but had no artists like one it should know.");
        }
        catch (LastFmException e)
        {
            return new HealthReport(HealthStatus.Unhealthy, e.Message);
        }
        catch (HttpRequestException e)
        {
            return new HealthReport(HealthStatus.Unhealthy, $"Can't reach Last.fm: {e.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HealthReport(HealthStatus.Unhealthy, "Last.fm didn't answer in time.");
        }
    }

    /// <summary><c>similarartists</c> from <c>artist.getSimilar</c>.</summary>
    public static IReadOnlyList<DiscoveredArtist> ReadArtists(JsonElement similar, int count) =>
        Items(similar, "artist")
            .Select(a => (Name: Text(a, "name"), Mbid: Text(a, "mbid"), Score: Number(a, "match")))
            .Where(a => a.Name is not null)
            .Take(count)
            .Select(a => new DiscoveredArtist(a.Name!, Ids(a.Mbid), a.Score))
            .ToList();

    /// <summary><c>similartracks</c> from <c>track.getSimilar</c>.</summary>
    public static IReadOnlyList<DiscoveredRecording> ReadTracks(JsonElement similar, int count) =>
        Items(similar, "track")
            .Select(t =>
            {
                var artist = t.TryGetProperty("artist", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
                var artistName = artist.ValueKind == JsonValueKind.Object ? Text(artist, "name") : null;
                var seconds = Number(t, "duration");
                return (Title: Text(t, "name"), Mbid: Text(t, "mbid"), Score: Number(t, "match"), Duration: seconds > 0 ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null,
                    Artist: artistName is null ? null : new DiscoveryArtist(artistName, Ids(Text(artist, "mbid"))));
            })
            .Where(t => t.Title is not null && t.Artist is not null)
            .Take(count)
            .Select(t => new DiscoveredRecording(t.Title!, t.Artist!.Name, Ids(t.Mbid), [t.Artist], t.Score, Duration: t.Duration))
            .ToList();

    /// <summary>
    /// Calls Last.fm by MusicBrainz ID when there is one, then by name if it doesn't know the ID. Null when it doesn't
    /// know what's asked about at all.
    /// </summary>
    private async Task<JsonElement?> AskAsync(string method, string? mbid, Dictionary<string, string> byName, int count, CancellationToken ct)
    {
        if (_apiKey.Length == 0)
            throw new LastFmException("Add your Last.fm API key in the plugin's settings.");
        if (mbid is not null && await CallAsync(method, new() { ["mbid"] = mbid }, count, ct) is { } byId)
            return byId;
        return await CallAsync(method, new(byName) { ["autocorrect"] = "1" }, count, ct);
    }

    private async Task<JsonElement?> CallAsync(string method, Dictionary<string, string> parameters, int count, CancellationToken ct)
    {
        parameters["method"] = method;
        parameters["limit"] = count.ToString(CultureInfo.InvariantCulture);
        parameters["api_key"] = _apiKey;
        parameters["format"] = "json";
        using var response = await _http.GetAsync(Api + "?" + string.Join('&', parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}")), ct);
        // Last.fm answers its errors with a code in the body, whatever the status.
        JsonElement body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        }
        catch (JsonException)
        {
            response.EnsureSuccessStatusCode();
            throw;
        }
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Number)
        {
            var message = Text(body, "message") ?? $"Last.fm error {error.GetInt32()}";
            return error.GetInt32() switch
            {
                InvalidParameters => null,
                InvalidApiKey or SuspendedApiKey => throw new LastFmException($"Last.fm doesn't take the API key: {message}"),
                _ => throw new HttpRequestException($"Last.fm: {message}"),
            };
        }
        response.EnsureSuccessStatusCode();
        return body;
    }

    // Last.fm sends a single item as an object rather than a list of one.
    private static IEnumerable<JsonElement> Items(JsonElement parent, string name) =>
        !parent.TryGetProperty(name, out var items) ? []
        : items.ValueKind == JsonValueKind.Array ? items.EnumerateArray()
        : items.ValueKind == JsonValueKind.Object ? [items]
        : [];

    private static IReadOnlyList<ProviderId> Ids(string? mbid) =>
        mbid is not null && Guid.TryParse(mbid, out _) ? [new ProviderId(Providers.MusicBrainz, mbid)] : [];

    private static string? Mbid(IReadOnlyList<ProviderId> ids) =>
        ids.FirstOrDefault(i => i.Provider == Providers.MusicBrainz && Guid.TryParse(i.Value, out _))?.Value;

    private static string? Text(JsonElement record, string name) =>
        record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    // Last.fm sends numbers as numbers or as strings, depending on the method.
    private static double Number(JsonElement record, string name) =>
        !record.TryGetProperty(name, out var value) ? 0
        : value.ValueKind == JsonValueKind.Number ? value.GetDouble()
        : value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed
        : 0;
}

/// <summary>Last.fm won't answer with these settings.</summary>
public sealed class LastFmException(string message) : Exception(message);
