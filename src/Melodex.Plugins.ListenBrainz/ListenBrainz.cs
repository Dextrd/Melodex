using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Discovery;

namespace Melodex.Plugins.ListenBrainz;

/// <summary>Entry point for the ListenBrainz discovery plugin.</summary>
public sealed class ListenBrainzFactory : IPluginFactory
{
    public const string PluginId = "melodex.discovery.listenbrainz";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "ListenBrainz",
        Version = "0.1.0",
        Type = PluginType.Discovery,
        ContractVersion = ContractVersions.Discovery.ToString(),
        Description = "Similar artists and songs from what ListenBrainz's listeners play together, and songs it recommends for you once you've linked your account. Free and open; no account needed for the rest.",
        Homepage = "https://listenbrainz.org",
        Capabilities = [DiscoveryCapabilities.SimilarArtists, DiscoveryCapabilities.SimilarRecordings, DiscoveryCapabilities.Recommendations],
        Access = new PluginAccess { Network = ["api.listenbrainz.org", "labs.api.listenbrainz.org"] },
        // ListenBrainz asks every app to keep to one call a second.
        MaxRequestsPerSecond = 1,
    };

    public IPlugin Create(PluginContext context) => new ListenBrainz(context);
}

/// <summary>
/// ListenBrainz, the MetaBrainz Foundation's open listening history. Similar artists and songs come from its labs API,
/// worked out from which ones listeners play in the same sessions; it knows them only by MusicBrainz ID. Recommendations
/// are its collaborative filtering's songs for a user, which it makes from the listens sent to it.
/// </summary>
public sealed class ListenBrainz(PluginContext context) : IDiscoveryPlugin
{
    public const string Api = "https://api.listenbrainz.org/1/";
    public const string Labs = "https://labs.api.listenbrainz.org/";

    // The ones ListenBrainz's own pages use: listening sessions over the last twenty years or so.
    public const string ArtistAlgorithm = "session_based_days_7500_session_300_contribution_5_threshold_10_limit_100_filter_True_skip_30";
    public const string RecordingAlgorithm = "session_based_days_9000_session_300_contribution_5_threshold_15_limit_50_skip_30";

    /// <summary>How many recordings to ask the metadata of at once.</summary>
    private const int MetadataBatch = 50;

    private readonly HttpClient _http = context.HttpClient;

    public async Task<IReadOnlyList<DiscoveredArtist>> SimilarArtistsAsync(DiscoveryArtist artist, int count, CancellationToken cancellationToken)
    {
        if (Mbid(artist.Ids) is not { } mbid)
            return [];
        var results = await GetAsync($"{Labs}similar-artists/json?artist_mbids={mbid}&algorithm={ArtistAlgorithm}", cancellationToken);
        return results is { ValueKind: JsonValueKind.Array } list ? ReadArtists(list, mbid, count) : [];
    }

    public async Task<IReadOnlyList<DiscoveredRecording>> SimilarRecordingsAsync(DiscoveryRecording recording, int count, CancellationToken cancellationToken)
    {
        if (Mbid(recording.Ids) is not { } mbid)
            return [];
        var results = await GetAsync($"{Labs}similar-recordings/json?recording_mbids={mbid}&algorithm={RecordingAlgorithm}", cancellationToken);
        if (results is not { ValueKind: JsonValueKind.Array } list)
            return [];
        var found = ReadRecordings(list, mbid, count);
        // The labs API leaves out who the artists are; the metadata has their IDs, which match them in the catalogue.
        var metadata = await MetadataAsync(found.Select(r => Mbid(r.Ids)!).ToList(), cancellationToken);
        return found.Select(r => metadata.TryGetValue(Mbid(r.Ids)!, out var known) ? known with { Score = r.Score, Album = r.Album ?? known.Album } : r).ToList();
    }

    public async Task<IReadOnlyList<DiscoveredRecording>> RecommendAsync(DiscoveryListener listener, int count, CancellationToken cancellationToken)
    {
        if (!listener.Accounts.TryGetValue(DiscoveryAccounts.ListenBrainz, out var user) || string.IsNullOrWhiteSpace(user))
            return [];
        // 204 until ListenBrainz has made them, 404 for a user it doesn't know: none either way.
        var answer = await GetAsync($"{Api}cf/recommendation/user/{Uri.EscapeDataString(user.Trim())}/recording?count={count}", cancellationToken);
        if (answer is not { } body || !body.TryGetProperty("payload", out var payload) || !payload.TryGetProperty("mbids", out var mbids)
            || mbids.ValueKind != JsonValueKind.Array)
            return [];
        var scored = mbids.EnumerateArray()
            .Select(m => (Mbid: Text(m, "recording_mbid"), Score: Number(m, "score")))
            .Where(m => m.Mbid is not null)
            .Take(count)
            .ToList();
        var metadata = await MetadataAsync(scored.Select(m => m.Mbid!).ToList(), cancellationToken);
        // Songs it has no metadata for can't be shown or matched, so they're left out.
        return scored.Where(m => metadata.ContainsKey(m.Mbid!)).Select(m => metadata[m.Mbid!] with { Score = m.Score }).ToList();
    }

    public async Task<HealthReport> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Portishead, whom it should always have others like.
            var similar = await SimilarArtistsAsync(new DiscoveryArtist("Portishead", [new ProviderId(Providers.MusicBrainz, "8f6bd1e4-fbe1-4f50-aa9b-94c450ec0f11")]), 1,
                cancellationToken);
            return similar.Count > 0
                ? HealthReport.Healthy()
                : new HealthReport(HealthStatus.Degraded, "ListenBrainz answered, but had no artists like one it should know.");
        }
        catch (HttpRequestException e)
        {
            return new HealthReport(HealthStatus.Unhealthy, $"Can't reach ListenBrainz: {e.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HealthReport(HealthStatus.Unhealthy, "ListenBrainz didn't answer in time.");
        }
    }

    /// <summary>The labs API's similar artists, leaving out the artist asked about.</summary>
    public static IReadOnlyList<DiscoveredArtist> ReadArtists(JsonElement list, string reference, int count) => list.EnumerateArray()
        .Select(a => (Mbid: Text(a, "artist_mbid"), Name: Text(a, "name"), Comment: Text(a, "comment"), Score: Number(a, "score")))
        .Where(a => a.Mbid is not null && a.Name is not null && !string.Equals(a.Mbid, reference, StringComparison.OrdinalIgnoreCase))
        .Take(count)
        .Select(a => new DiscoveredArtist(a.Name!, [new ProviderId(Providers.MusicBrainz, a.Mbid!)], a.Score, a.Comment))
        .ToList();

    /// <summary>The labs API's similar songs, leaving out the song asked about; their artists are only names.</summary>
    public static IReadOnlyList<DiscoveredRecording> ReadRecordings(JsonElement list, string reference, int count) => list.EnumerateArray()
        .Select(r => (Mbid: Text(r, "recording_mbid"), Title: Text(r, "recording_name"), Artist: Text(r, "artist_credit_name"), Album: Text(r, "release_name"),
            Score: Number(r, "score")))
        .Where(r => r.Mbid is not null && r.Title is not null && r.Artist is not null && !string.Equals(r.Mbid, reference, StringComparison.OrdinalIgnoreCase))
        .DistinctBy(r => r.Mbid)
        .Take(count)
        .Select(r => new DiscoveredRecording(r.Title!, r.Artist!, [new ProviderId(Providers.MusicBrainz, r.Mbid!)], [], r.Score, r.Album))
        .ToList();

    /// <summary>
    /// The metadata API's answer as songs, by recording MBID: title, credit, artists with their IDs, an album, ISRCs and
    /// length. Scores are left at zero for the caller.
    /// </summary>
    public static Dictionary<string, DiscoveredRecording> ReadMetadata(JsonElement body)
    {
        var found = new Dictionary<string, DiscoveredRecording>(StringComparer.OrdinalIgnoreCase);
        if (body.ValueKind != JsonValueKind.Object)
            return found;
        foreach (var entry in body.EnumerateObject())
        {
            var data = entry.Value;
            if (!data.TryGetProperty("recording", out var recording) || Text(recording, "name") is not { } title)
                continue;
            var credit = data.TryGetProperty("artist", out var artist) ? artist : default;
            var artists = credit.ValueKind == JsonValueKind.Object && credit.TryGetProperty("artists", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray()
                    .Where(a => Text(a, "name") is not null)
                    .Select(a => new DiscoveryArtist(Text(a, "name")!, Text(a, "artist_mbid") is { } id ? [new ProviderId(Providers.MusicBrainz, id)] : []))
                    .ToList()
                : [];
            var name = credit.ValueKind == JsonValueKind.Object ? Text(credit, "name") : null;
            if ((name ?? string.Join(", ", artists.Select(a => a.Name))) is not { Length: > 0 } artistCredit)
                continue;
            var ids = new List<ProviderId> { new(Providers.MusicBrainz, entry.Name) };
            if (recording.TryGetProperty("isrcs", out var isrcs) && isrcs.ValueKind == JsonValueKind.Array)
                ids.AddRange(isrcs.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => new ProviderId(Providers.Isrc, i.GetString()!)));
            var length = recording.TryGetProperty("length", out var ms) && ms.ValueKind == JsonValueKind.Number ? TimeSpan.FromMilliseconds(ms.GetDouble()) : (TimeSpan?)null;
            var album = data.TryGetProperty("release", out var release) && release.ValueKind == JsonValueKind.Object ? Text(release, "name") : null;
            found[entry.Name] = new DiscoveredRecording(title, artistCredit, ids, artists, 0, album, length);
        }
        return found;
    }

    private async Task<Dictionary<string, DiscoveredRecording>> MetadataAsync(IReadOnlyList<string> mbids, CancellationToken ct)
    {
        var found = new Dictionary<string, DiscoveredRecording>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in mbids.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(MetadataBatch))
            if (await GetAsync($"{Api}metadata/recording/?recording_mbids={string.Join(',', batch)}&inc=artist%20release", ct) is { } body)
                foreach (var (mbid, recording) in ReadMetadata(body))
                    found[mbid] = recording;
        return found;
    }

    private static string? Mbid(IReadOnlyList<ProviderId> ids) =>
        ids.FirstOrDefault(i => i.Provider == Providers.MusicBrainz && Guid.TryParse(i.Value, out _))?.Value;

    private static string? Text(JsonElement record, string name) =>
        record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static double Number(JsonElement record, string name) =>
        record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    // Null for a 204 or 404, which is how ListenBrainz says it has nothing.
    private async Task<JsonElement?> GetAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }
}
