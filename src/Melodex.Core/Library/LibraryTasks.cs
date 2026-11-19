using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Melodex.Client;
using Melodex.Core.Catalogue;
using Melodex.Core.Data;
using Melodex.Core.Plugins;
using Melodex.Core.Tasks;

namespace Melodex.Core.Library;

/// <summary>
/// Scans the music folder and matches new and changed files. Then, if the settings say so, it queues a catalogue update,
/// finding lyrics for songs without them, and making the visualiser's bars for them.
/// </summary>
public sealed class LibraryScanTask(
    LibraryScanner scanner,
    LibraryMatcher matcher,
    LibraryReading reading,
    ScanFeed feed,
    LyricsFinder lyrics,
    SettingsService settings,
    TaskQueue queue,
    MelodexDbContext db) : ITaskHandler
{
    public const string TaskKind = "library-scan";

    public string Kind => TaskKind;

    /// <summary>The task's payload: null for a plain scan, so asking twice queues one.</summary>
    public static string? Payload(Rematch rematch) => rematch == Rematch.None ? null : JsonSerializer.Serialize(rematch, MelodexJson.Options);

    public static Rematch RematchOf(string? payload) => payload is null ? Rematch.None : JsonSerializer.Deserialize<Rematch>(payload, MelodexJson.Options);

    public static string Title(string? payload) => RematchOf(payload) switch
    {
        Rematch.Unmatched => "Library scan · matching unmatched files again",
        Rematch.All => "Library scan · matching everything again",
        _ => "Library scan",
    };

    public async Task RunAsync(BackgroundTask task, IProgress<string> progress, CancellationToken ct)
    {
        LibraryScanner.Result scan;
        LibraryMatcher.Result match;
        using (reading.Begin())
        {
            progress.Report("Reading files");
            feed.Clear(task.Id);
            scan = await scanner.ScanAsync(progress, ct);
            var again = await RematchAsync(RematchOf(task.PayloadJson), task.CreatedAt, ct);
            progress.Report($"Found {scan.Files} files ({scan.Added} new, {scan.Changed} changed, {scan.Removed} removed"
                            + (again > 0 ? $", {again} to match again" : "") + "); matching");
            match = await matcher.MatchPendingAsync(progress, ct);
        }

        // A scan can add a lot to the library, which changes how best to read it.
        await db.OptimiseAsync(ct);
        var notes = new List<string>();
        if (scan.SkippedFolders > 0)
            notes.Add(Count(scan.SkippedFolders, "folder", "folders") + " couldn't be read");
        if (scan.Unreadable > 0)
            notes.Add(Count(scan.Unreadable, "file", "files") + " couldn't be read");
        if (match.Waiting > 0)
            notes.Add(Count(match.Waiting, "file", "files") + " left to match next scan (the catalogue source was busy)");
        if (match.NoCatalogue > 0)
            notes.Add(Count(match.NoCatalogue, "file", "files") + " shown as their tags say, as no catalogue is set up");
        if (match.StillDeferred > 0)
            notes.Add(Count(match.StillDeferred, "deferred file", "deferred files") + " still not a confident match");
        progress.Report($"{scan.Files} files: {match.Matched} newly matched, {match.Review} to review" + string.Concat(notes.Select(n => "; " + n)));

        var library = await settings.GetLibraryAsync(ct);
        if (library.UpdateCatalogueAfterScan)
            await queue.EnqueueAsync(CatalogueUpdateTask.TaskKind, null, ct);
        if (library.FetchLyrics && await lyrics.HaveSourcesAsync(ct) && await lyrics.Wanting().AnyAsync(ct))
            await queue.EnqueueAsync(LyricsTask.TaskKind, null, ct);
        // While they're made when matching, new songs get the visualiser's bars.
        if ((await settings.GetPlaybackAsync(ct)).Visualiser == VisualiserBars.WhenMatching)
            await queue.EnqueueAsync(BarsTask.TaskKind, null, ct);
        // New and changed lossless files are checked for lossy music passed off as lossless.
        if (await SpectrumTask.Unchecked(db).AnyAsync(ct))
            await queue.EnqueueAsync(SpectrumTask.TaskKind, null, ct);
    }

    private static string Count(int n, string one, string many) => LibraryMatcher.Plural(n, one, many);

    /// <summary>
    /// Puts files back to be matched: deferred ones and those put back on the review list on every scan, and those the
    /// scan was asked to match again. Only
    /// files last matched before the scan was asked for, so a scan picked up again after a pause doesn't redo any.
    /// Files you matched, excluded or hid yourself are left alone.
    /// </summary>
    private async Task<int> RematchAsync(Rematch rematch, DateTime since, CancellationToken ct)
    {
        var files = db.LibraryFiles.Where(f => f.MatchedAt != null && f.MatchedAt < since && f.ExcludedAt == null && f.HiddenAt == null);
        var again = await files.Where(f => (f.DeferredAt != null || f.PutBackAt != null) && f.TrackId == null)
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.MatchedAt, (DateTime?)null).SetProperty(f => f.PutBackAt, (DateTime?)null), ct);
        if (rematch == Rematch.Unmatched)
            again += await files.Where(f => f.TrackId == null)
                .ExecuteUpdateAsync(u => u.SetProperty(f => f.MatchedAt, (DateTime?)null), ct);
        else if (rematch == Rematch.All)
            again += await files.Where(f => f.MatchMethod != MatchMethod.Manual)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(f => f.MatchedAt, (DateTime?)null)
                    .SetProperty(f => f.TrackId, (Guid?)null)
                    .SetProperty(f => f.EditionId, (Guid?)null)
                    .SetProperty(f => f.SuggestedTrackId, (Guid?)null)
                    .SetProperty(f => f.SuggestedEditionId, (Guid?)null)
                    .SetProperty(f => f.MatchMethod, MatchMethod.Unmatched), ct);
        return again;
    }
}

/// <summary>
/// Fills in the catalogue for every library artist: their discography and the editions of each album the missing view
/// might show, which reveals bootleg-only albums and albums covered by tracks owned elsewhere. Queued after each scan if
/// the settings say so, or from the Scan menu.
/// </summary>
public sealed class CatalogueUpdateTask(CatalogueRefresher refresher, TaskFeed feed, MelodexDbContext db) : ITaskHandler
{
    public const string TaskKind = "catalogue-update";
    public const string Title = "Catalogue update";

    public string Kind => TaskKind;

    public async Task RunAsync(BackgroundTask task, IProgress<string> progress, CancellationToken ct)
    {
        var artists = await db.Artists.Where(a => a.InLibrary).OrderBy(a => a.SortName ?? a.Name).Select(a => a.Id).ToListAsync(ct);
        var failures = await refresher.RefreshArtistsAsync(artists, progress, ct, feed.Start(task));
        // An update can add a lot to the catalogue, which changes how best to read it.
        await db.OptimiseAsync(ct);
        progress.Report(LibraryMatcher.Plural(artists.Count, "artist", "artists") + " updated"
                        + (failures > 0 ? $"; {LibraryMatcher.Plural(failures, "lookup", "lookups")} failed and will be tried again next update" : ""));
    }
}

/// <summary>Fills in one artist's catalogue after their page is opened, so non-library artists get full statuses.</summary>
public sealed class ArtistRefreshTask(CatalogueRefresher refresher, TaskFeed feed) : ITaskHandler
{
    public const string TaskKind = "artist-refresh";

    public string Kind => TaskKind;

    public static string Payload(Guid artistId) => JsonSerializer.Serialize(artistId);

    public Task RunAsync(BackgroundTask task, IProgress<string> progress, CancellationToken ct) =>
        refresher.RefreshArtistsAsync([JsonSerializer.Deserialize<Guid>(task.PayloadJson!)], progress, ct, feed.Start(task));
}

/// <summary>
/// Fills in the catalogue for library artists: discographies, then editions of the albums the missing view shows (editions
/// say whether an album has an official release). An artist or album the source is too busy for goes to the back of its
/// queue, up to the attempts the settings allow; after that it stays stale for the next update. Nothing fails the update.
/// </summary>
public sealed class CatalogueRefresher(
    MelodexDbContext db,
    CatalogueService catalogue,
    SettingsService settings,
    IOptions<PluginHttpOptions> httpOptions,
    ILogger<CatalogueRefresher> logger)
{
    private enum Outcome { Done, Busy, Failed }

    /// <param name="rows">Where a task shows what happened to each artist, if it does.</param>
    public async Task<int> RefreshArtistsAsync(IReadOnlyList<Guid> artistIds, IProgress<string> progress, CancellationToken ct, TaskRows? rows = null)
    {
        var library = await settings.GetLibraryAsync(ct);
        var backoff = new SourceBackoff(httpOptions.Value, logger);
        var artists = new Queue<(Guid Id, int Attempt)>(artistIds.Select(id => (id, 1)));
        int done = 0, failures = 0;
        while (artists.TryDequeue(out var next))
        {
            if (backoff.GaveUp)
                return failures + artists.Count + 1;
            db.ChangeTracker.Clear();
            var artist = await db.Artists.FindAsync([next.Id], ct);
            if (artist is null)
                continue;
            var status = $"Updating catalogue: {artist.Name} ({done + 1} of {artistIds.Count})";
            progress.Report(status);
            var name = artist.Name;
            var href = $"artists/{next.Id}";
            rows?.Put(next.Id.ToString(), TaskRowState.Working, name, null, "Looking up the discography", href, LibraryView.ArtistImageUrl(next.Id));
            var before = rows is null ? [] : await db.Albums.Where(a => a.ArtistId == next.Id).Select(a => a.Id).ToListAsync(ct);
            switch (await TryAsync(() => catalogue.RefreshDiscographyAsync(artist, false, ct), $"the discography of {name}", next.Attempt, library.AttemptsWhenBusy, backoff, progress, status, ct))
            {
                case Outcome.Busy when next.Attempt < library.AttemptsWhenBusy:
                    artists.Enqueue((next.Id, next.Attempt + 1));
                    rows?.Put(next.Id.ToString(), TaskRowState.Waiting, name, null, "The catalogue source is busy; tried again shortly", href, LibraryView.ArtistImageUrl(next.Id));
                    continue;
                case Outcome.Busy or Outcome.Failed:
                    done++;
                    failures++;
                    rows?.Put(next.Id.ToString(), TaskRowState.Failed, name, null, "Couldn't be looked up; tried again next update", href, LibraryView.ArtistImageUrl(next.Id));
                    continue;
            }

            var stale = DateTime.UtcNow - CatalogueService.EditionsTtl;
            var various = await VariousArtists.IsAsync(db, next.Id, ct);
            var albums = await db.Albums.Where(a => a.ArtistId == next.Id && (a.EditionsRefreshedAt == null || a.EditionsRefreshedAt < stale)
                                                    && (!various || db.Editions.Any(e => e.AlbumId == a.Id && db.LibraryFiles.Any(f => f.EditionId == e.Id))))
                .ToListAsync(ct);
            var wanted = new Queue<(Guid Id, int Attempt)>(albums.Where(a => library.ShowingOf(a) != ReleaseShowing.Hide).Select(a => (a.Id, 1)));
            int checkedAlbums = 0, albumFailures = 0, total = wanted.Count;
            var after = rows is null ? [] : await db.Albums.Where(a => a.ArtistId == next.Id).Select(a => a.Id).ToListAsync(ct);
            var changes = Changes(after.Except(before).Count(), before.Except(after).Count());
            while (wanted.TryDequeue(out var albumItem))
            {
                if (backoff.GaveUp)
                    return failures + albumFailures + wanted.Count + 1 + artists.Count;
                var albumStatus = $"Updating catalogue: {name}, album {checkedAlbums + 1} of {total} ({done + 1} of {artistIds.Count} artists)";
                progress.Report(albumStatus);
                rows?.Put(next.Id.ToString(), TaskRowState.Working, name, changes, $"Checking album {checkedAlbums + 1} of {total}", href, LibraryView.ArtistImageUrl(next.Id));
                // Loaded one at a time with a clear tracker: a big discography tracks thousands of entities otherwise,
                // and every query and save would scan them all.
                db.ChangeTracker.Clear();
                if (await db.Albums.FindAsync([albumItem.Id], ct) is not { } album)
                    continue;
                switch (await TryAsync(() => catalogue.RefreshEditionsAsync(album, false, ct), $"the editions of {album.Title}", albumItem.Attempt, library.AttemptsWhenBusy, backoff, progress, albumStatus, ct))
                {
                    case Outcome.Busy when albumItem.Attempt < library.AttemptsWhenBusy:
                        wanted.Enqueue((albumItem.Id, albumItem.Attempt + 1));
                        continue;
                    case Outcome.Busy or Outcome.Failed:
                        albumFailures++;
                        break;
                }
                checkedAlbums++;
            }
            db.ChangeTracker.Clear();
            done++;
            failures += albumFailures;
            rows?.Put(next.Id.ToString(), albumFailures > 0 ? TaskRowState.Failed : changes is null && total == 0 ? TaskRowState.Skipped : TaskRowState.Done,
                name, changes,
                (total == 0 ? "Up to date" : $"{LibraryMatcher.Plural(total, "album", "albums")} checked")
                + (albumFailures > 0 ? $"; {albumFailures} couldn't be looked up, tried again next update" : ""), href, LibraryView.ArtistImageUrl(next.Id));
            if (total == 0)
                logger.LogInformation("[{Done}/{Total} artists] The catalogue for {Artist} is up to date", done, artistIds.Count, name);
            else
                logger.LogInformation("[{Done}/{Total} artists] Updated the catalogue for {Artist}: {Count} albums checked{Failed}",
                    done, artistIds.Count, name, total, albumFailures > 0 ? $", {albumFailures} couldn't be updated" : "");
        }
        return failures;
    }

    // Albums the discography gained and lost, or null for neither.
    private static string? Changes(int added, int gone) =>
        (added, gone) switch
        {
            (0, 0) => null,
            (_, 0) => $"{LibraryMatcher.Plural(added, "album", "albums")} added",
            (0, _) => $"{LibraryMatcher.Plural(gone, "album", "albums")} gone",
            _ => $"{LibraryMatcher.Plural(added, "album", "albums")} added, {gone} gone",
        };

    private async Task<Outcome> TryAsync(Func<Task> refresh, string what, int attempt, int attempts, SourceBackoff backoff,
        IProgress<string> progress, string status, CancellationToken ct)
    {
        try
        {
            await refresh();
            backoff.Answered();
            return Outcome.Done;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // Drop whatever the failed refresh left half-done, so the next save doesn't write it.
            db.ChangeTracker.Clear();
            if (!LibraryMatcher.IsUnavailable(e, ct))
            {
                logger.LogWarning(e, "Couldn't update {What}; it will be tried again on the next update", what);
                return Outcome.Failed;
            }
            if (attempt < attempts)
                logger.LogWarning("The catalogue source is busy; {What} goes to the back of the queue (attempt {Attempt} of {Attempts}): {Reason}",
                    what, attempt, attempts, e.Message);
            else
                logger.LogWarning("Gave up on {What} for this update after {Attempts}; the next update tries again: {Reason}",
                    what, LibraryMatcher.Plural(attempts, "attempt", "attempts"), e.Message);
            await backoff.UnavailableAsync(progress, status, ct);
            return Outcome.Busy;
        }
    }
}
