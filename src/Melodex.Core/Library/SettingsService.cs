using System.Text.Json;
using Melodex.Client;
using Melodex.Core.Data;

namespace Melodex.Core.Library;

public static class LibrarySettingsExtensions
{
    /// <summary>How the missing view treats this album when it isn't owned, by the strictest of its kinds.</summary>
    public static ReleaseShowing ShowingOf(this LibrarySettings settings, Album album) =>
        ReleaseKinds.Of(album.Type, album.SecondaryTypes, album.HasOfficialRelease).Select(k => settings.ShowingOf(album.ArtistId, k)).DefaultIfEmpty().Max();
}

public sealed class SettingsService(MelodexDbContext db)
{
    private const string LibraryKey = "library";
    private const string FetchKey = "fetch";
    private const string LogKey = "log";
    private const string ArtistShowingKey = "artistShowing";
    private const string QualityKey = "quality";
    private const string UpgradesKey = "upgrades";
    private const string UpgradeRunKey = "upgrades.lastRun";
    private const string PlaybackKey = "playback";

    /// <summary>The library's settings, with the artists that have their own choices for the missing view.</summary>
    public async Task<LibrarySettings> GetLibraryAsync(CancellationToken ct)
    {
        var artists = await GetAsync<Dictionary<Guid, Dictionary<ReleaseKind, ReleaseShowing>>>(ArtistShowingKey, ct);
        return await GetAsync<LibrarySettings>(LibraryKey, ct) with
        {
            ArtistShowing = artists.ToDictionary(a => a.Key, a => (IReadOnlyDictionary<ReleaseKind, ReleaseShowing>)a.Value),
        };
    }

    /// <summary>An artist's own choices for the missing view, every kind written out; null to follow the library's again.</summary>
    public async Task SaveArtistShowingAsync(Guid artistId, IReadOnlyDictionary<ReleaseKind, ReleaseShowing>? showing, CancellationToken ct)
    {
        var artists = await GetAsync<Dictionary<Guid, Dictionary<ReleaseKind, ReleaseShowing>>>(ArtistShowingKey, ct);
        if (showing is null)
            artists.Remove(artistId);
        else
            artists[artistId] = Enum.GetValues<ReleaseKind>().ToDictionary(k => k, k => showing.TryGetValue(k, out var s) ? s : ReleaseKinds.Default(k));
        await SaveAsync(ArtistShowingKey, artists, ct);
    }

    public Task SaveLibraryAsync(LibrarySettings value, CancellationToken ct) => SaveAsync(LibraryKey, value, ct);

    public Task<FetchSettings> GetFetchAsync(CancellationToken ct) => GetAsync<FetchSettings>(FetchKey, ct);

    public Task SaveFetchAsync(FetchSettings value, CancellationToken ct) => SaveAsync(FetchKey, value, ct);

    public Task<QualityProfile> GetQualityAsync(CancellationToken ct) => GetAsync<QualityProfile>(QualityKey, ct);

    /// <summary>Saves the profile with every step in it, in order.</summary>
    public Task SaveQualityAsync(QualityProfile value, CancellationToken ct) =>
        SaveAsync(QualityKey, value with { Steps = value.Ordered().Select(s => new ProfileStep(s.Step.Key, s.Allowed)).ToList() }, ct);

    public Task<UpgradeSettings> GetUpgradesAsync(CancellationToken ct) => GetAsync<UpgradeSettings>(UpgradesKey, ct);

    public Task SaveUpgradesAsync(UpgradeSettings value, CancellationToken ct) => SaveAsync(UpgradesKey, value, ct);

    /// <summary>Where the scheduled look for upgrades stands, in its latest window.</summary>
    public Task<UpgradeRun> GetUpgradeRunAsync(CancellationToken ct) => GetAsync<UpgradeRun>(UpgradeRunKey, ct);

    public Task SaveUpgradeRunAsync(UpgradeRun run, CancellationToken ct) => SaveAsync(UpgradeRunKey, run, ct);

    public Task<PlaybackSettings> GetPlaybackAsync(CancellationToken ct) => GetAsync<PlaybackSettings>(PlaybackKey, ct);

    public Task SavePlaybackAsync(PlaybackSettings value, CancellationToken ct) => SaveAsync(PlaybackKey, value, ct);

    public Task<LogSettings> GetLogAsync(CancellationToken ct) => GetAsync<LogSettings>(LogKey, ct);

    public Task SaveLogAsync(LogSettings value, CancellationToken ct) => SaveAsync(LogKey, value, ct);

    private async Task<T> GetAsync<T>(string key, CancellationToken ct) where T : new()
    {
        var row = await db.Settings.FindAsync([key], ct);
        return row is null ? new T() : JsonSerializer.Deserialize<T>(row.ValueJson, MelodexJson.Options) ?? new T();
    }

    private async Task SaveAsync<T>(string key, T value, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(value, MelodexJson.Options);
        var row = await db.Settings.FindAsync([key], ct);
        if (row is null)
            db.Settings.Add(new Setting { Key = key, ValueJson = json });
        else
            row.ValueJson = json;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// A look through the whole library for upgrades: in a scheduled window, or by hand with Upgrade now. It keeps a few
/// upgrades going until every upgradable song has been tried, it's stopped, or the window it started in closes.
/// </summary>
/// <remarks>
/// Window is when the scheduled window it started in opened, in UTC, or null for one by hand outside any; Requester, who
/// its requests are asked for by; StartedAt, when it began (songs looked for since are left alone); Asked, how many it's
/// asked for; Done, that there was nothing left to try; Closed, that it's over.
/// </remarks>
public sealed record UpgradeRun
{
    public DateTime? Window { get; init; }
    public bool ByHand { get; init; }
    public string Requester { get; init; } = Upgrades.ScheduledRequester;
    public DateTime StartedAt { get; init; }
    public int Asked { get; init; }
    public bool Done { get; init; }
    public bool Closed { get; init; }
    public bool Stopped { get; init; }

    /// <summary>Still going: asking for more as upgrades finish.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Going => !Done && !Closed && (Window is not null || ByHand);

    /// <summary>When it began; before it was kept, a scheduled run's window opening.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime Began => StartedAt != default ? StartedAt : Window ?? DateTime.MinValue;
}
