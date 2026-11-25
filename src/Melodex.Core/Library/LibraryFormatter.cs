using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Melodex.Client;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins;
using Melodex.Core.Artwork;
using Melodex.Core.Data;
using Melodex.Core.Import;
using Melodex.Core.Playlists;
using Melodex.Core.Plugins;
using Melodex.Core.Tasks;

namespace Melodex.Core.Library;

/// <summary>
/// Formatting: rewriting matched files' tags and embedded covers from the catalogue and artwork plugins, and moving them
/// to where the path template says. The beets worker does the writing; this works out what to write. It runs as a
/// background task, as it can take a while over a whole library.
/// </summary>
public sealed class LibraryFormatter(MelodexDbContext db, IImportWorker worker, TaskQueue queue, SettingsService settings)
{
    public async Task<FormatPlan?> PlanAsync(FormatScope scope, CancellationToken ct)
    {
        if (await TitleAsync(db, scope, ct) is not { } title)
            return null;
        var files = FormatTask.Files(db, scope);
        var artwork = await db.PluginInstances.Where(p => p.Type == PluginType.Artwork && p.Enabled).OrderBy(p => p.Priority)
            .Select(p => new FormatSource(p.Id.ToString(), p.Name)).ToListAsync(ct);
        var source = await db.PluginInstances.Where(p => p.Type == PluginType.Index && p.Enabled).OrderBy(p => p.Priority)
            .Select(p => p.Name).FirstOrDefaultAsync(ct) ?? "the catalogue";
        var health = await worker.CheckHealthAsync(ct);
        return new FormatPlan(
            title,
            await files.CountAsync(ct),
            await FormatTask.Formattable(files).CountAsync(ct),
            [new FormatSource(FormatSources.Catalogue, source), new FormatSource(FormatSources.Keep, "Keep the file's own")],
            [
                new FormatSource(FormatSources.Keep, "Keep the file's own"),
                new FormatSource(FormatSources.Shown, "The cover Melodex shows"),
                .. artwork,
                new FormatSource(FormatSources.Remove, "Remove it"),
            ],
            (await settings.GetFetchAsync(ct)).PathTemplate,
            health.Status == HealthStatus.Healthy,
            health.Status == HealthStatus.Healthy ? null : health.Message,
            scope.Kind == FormatScopeKind.Library && health.Status == HealthStatus.Healthy ? await MusiclessAsync(ct) : null,
            [
                new FormatSource(FormatSources.Catalogue, $"{source}, where a file has none"),
                new FormatSource(FormatSources.Replace, $"{source}, over the file's own"),
                new FormatSource(FormatSources.Keep, "Keep the file's own"),
            ]);
    }

    // The folders a format of the whole library that moves files will remove, besides those its moves leave with no music.
    private async Task<IReadOnlyList<string>?> MusiclessAsync(CancellationToken ct)
    {
        try
        {
            return (await worker.MusiclessAsync(new MusiclessRequest(false), ct)).Folders;
        }
        catch (Exception e) when (e is HttpRequestException or ImportException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Queues the format, or returns the same one already queued or running.</summary>
    public async Task<BackgroundTask> StartAsync(StartFormat body, CancellationToken ct)
    {
        var title = await TitleAsync(db, body.Scope, ct) ?? throw new KeyNotFoundException();
        var job = new FormatTask.Job(body.Scope, body.Options, title);
        return await queue.EnqueueAsync(FormatTask.TaskKind, JsonSerializer.Serialize(job, MelodexJson.Options), ct);
    }

    /// <summary>Why these options can't be used, by option; empty when they can.</summary>
    public async Task<Dictionary<string, string[]>> ValidateAsync(FormatOptions options, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        foreach (var (name, value) in new[] { ("titles", options.Titles), ("numbers", options.Numbers), ("release", options.Release), ("identifiers", options.Identifiers) })
            if (value is not (FormatSources.Keep or FormatSources.Catalogue))
                errors[name] = [$"Choose {FormatSources.Catalogue} or {FormatSources.Keep}."];
        if (options.Genres is not (FormatSources.Keep or FormatSources.Catalogue or FormatSources.Replace))
            errors["genres"] = [$"Choose {FormatSources.Catalogue}, {FormatSources.Replace} or {FormatSources.Keep}."];
        if (options.Artwork is not (FormatSources.Keep or FormatSources.Shown or FormatSources.Remove)
            && !(Guid.TryParse(options.Artwork, out var plugin) && await db.PluginInstances.AnyAsync(p => p.Id == plugin && p.Type == PluginType.Artwork && p.Enabled, ct)))
            errors["artwork"] = [$"Choose {FormatSources.Keep}, {FormatSources.Shown}, {FormatSources.Remove} or the ID of an enabled artwork plugin."];
        return errors;
    }

    internal static async Task<string?> TitleAsync(MelodexDbContext db, FormatScope scope, CancellationToken ct) => scope.Kind switch
    {
        FormatScopeKind.Library => "the whole library",
        FormatScopeKind.Artist => await db.Artists.Where(a => a.Id == scope.Id).Select(a => a.Name).FirstOrDefaultAsync(ct),
        FormatScopeKind.Album => await db.Albums.Where(a => a.Id == scope.Id).Select(a => a.Title).FirstOrDefaultAsync(ct),
        FormatScopeKind.Track => await db.Tracks.Where(t => t.Id == scope.Id).Select(t => t.Title).FirstOrDefaultAsync(ct),
        _ => null,
    };
}

/// <summary>Runs a format, in batches of an album's files at a time. Paused, it carries on from the files it hasn't done.</summary>
public sealed class FormatTask(
    MelodexDbContext db,
    IImportWorker worker,
    ArtworkCache artwork,
    SettingsService settings,
    ConnectorPlugins connectors,
    PlaylistSyncer playlists,
    TaskFeed feed,
    IOptions<MelodexOptions> options,
    ILogger<FormatTask> logger) : ITaskHandler
{
    public const string TaskKind = "library-format";
    private const int BatchSize = 50;

    // How often the media server is told what's been written while a format runs, so a moved song is missing from it
    // for no longer than this and its scan, without asking it to scan after every batch.
    private static readonly TimeSpan TellEvery = TimeSpan.FromSeconds(20);

    public string Kind => TaskKind;

    /// <summary>A format's payload: what it covers and how, and its title for the Activity page.</summary>
    public sealed record Job(FormatScope Scope, FormatOptions Options, string Title);

    public static string Title(string? payload) =>
        payload is not null && JsonSerializer.Deserialize<Job>(payload, MelodexJson.Options) is { } job ? $"Formatting · {job.Title}" : "Formatting";

    public static IQueryable<LibraryFile> Files(MelodexDbContext db, FormatScope scope) => scope.Kind switch
    {
        FormatScopeKind.Artist => db.LibraryFiles.Where(f => f.ArtistId == scope.Id || f.EditionId != null && f.Edition!.Album.ArtistId == scope.Id
                                                             || f.TrackId != null && f.Track!.ArtistId == scope.Id),
        FormatScopeKind.Album => db.LibraryFiles.Where(f => f.EditionId != null && f.Edition!.AlbumId == scope.Id),
        FormatScopeKind.Track => db.LibraryFiles.Where(f => f.TrackId == scope.Id),
        _ => db.LibraryFiles,
    };

    /// <summary>Files a format rewrites: only matched ones, as only those have catalogue metadata to write.</summary>
    public static IQueryable<LibraryFile> Formattable(IQueryable<LibraryFile> files) => files.Where(f => f.TrackId != null && f.ExcludedAt == null);

    public async Task RunAsync(BackgroundTask task, IProgress<string> progress, CancellationToken ct)
    {
        var job = JsonSerializer.Deserialize<Job>(task.PayloadJson!, MelodexJson.Options)!;
        var formattable = Formattable(Files(db, job.Scope));
        var total = await formattable.CountAsync(ct);
        // Done before a pause: formatted since this format was asked for.
        var todo = await formattable.Where(f => f.FormattedAt == null || f.FormattedAt < task.CreatedAt)
            .OrderBy(f => f.EditionId).ThenBy(f => f.Path).Select(f => new { f.Id, f.EditionId, f.Path, AlbumId = f.Edition != null ? f.Edition.AlbumId : (Guid?)null }).ToListAsync(ct);
        var done = total - todo.Count;
        var template = job.Options.Move ? (await settings.GetFetchAsync(ct)).PathTemplate : null;
        var root = Path.GetFullPath(options.Value.LibraryPath);
        var touched = new List<string>();
        var moved = new List<Guid>();
        var failures = new List<string>();
        var removed = new List<string>();
        var untold = new List<string>();
        var told = DateTime.UtcNow;
        progress.Report(Progress(done, total));
        // Every file it means to write, listed as it starts, each filled in as it's done.
        var rows = feed.Start(task);
        // Each with its album's cover, and a link to the album.
        var covers = todo.ToDictionary(f => f.Id, f => f.AlbumId is { } album ? (Cover: LibraryView.CoverUrl(album, f.EditionId), Href: $"albums/{album}") : (null, null));
        void Show(Guid id, string key, TaskRowState state, string title, string? subtitle, string? detail = null) =>
            rows.Put(key, state, title, subtitle, detail, covers.GetValueOrDefault(id).Href, covers.GetValueOrDefault(id).Cover);
        foreach (var file in todo)
            Show(file.Id, file.Path, TaskRowState.Waiting, Path.GetFileName(file.Path), Path.GetDirectoryName(file.Path));

        foreach (var batch in todo.GroupBy(f => f.EditionId).SelectMany(g => g.Chunk(BatchSize)))
        {
            ct.ThrowIfCancellationRequested();
            db.ChangeTracker.Clear();
            var ids = batch.Select(f => f.Id).ToList();
            var files = await db.LibraryFiles.Where(f => ids.Contains(f.Id)).ToListAsync(ct);
            var tags = await CatalogueTags.ForAsync(db, files, job.Options, ct);
            var images = new Dictionary<string, FormatArtwork>();
            if (batch[0].EditionId is { } edition && await ArtworkAsync(job.Options.Artwork, edition, ct) is { } cover)
                images["cover"] = new FormatArtwork(Convert.ToBase64String(cover));
            var request = new FormatRequest(
                files.Select(f => new FormatFile(f.Path, tags.GetValueOrDefault(f.Id) ?? new Dictionary<string, string>(),
                    images.Count > 0 ? "cover" : null, job.Options.Artwork == FormatSources.Remove)).ToList(),
                images,
                template);

            foreach (var file in files)
                Show(file.Id, file.Path, TaskRowState.Working, Path.GetFileName(file.Path), Path.GetDirectoryName(file.Path), "Writing");
            var result = await worker.FormatAsync(request, ct);
            removed.AddRange(result.RemovedFolders ?? []);
            foreach (var folder in result.RemovedFolders ?? [])
                rows.Put("folder:" + folder, TaskRowState.Done, folder, null, "Removed, as it had no music left in it");
            var now = DateTime.UtcNow;
            foreach (var formatted in result.Files)
            {
                if (files.FirstOrDefault(f => f.Path == formatted.Path) is not { } file)
                    continue;
                var wrote = images.Count > 0 ? "Tags and cover written" : job.Options.Artwork == FormatSources.Remove ? "Tags written, cover removed" : "Tags written";
                if (formatted.NewPath is null)
                {
                    Show(file.Id, file.Path, TaskRowState.Failed, Path.GetFileName(file.Path), Path.GetDirectoryName(file.Path), formatted.Error ?? "It couldn't be written");
                    failures.Add($"{formatted.Path}: {formatted.Error}");
                    logger.LogWarning("Couldn't format {Path}: {Reason}", formatted.Path, formatted.Error);
                    continue;
                }
                untold.Add(file.Path);
                if (file.Path != formatted.NewPath)
                    moved.Add(file.Id);
                Show(file.Id, file.Path, TaskRowState.Done, Path.GetFileName(formatted.NewPath),
                    file.Path == formatted.NewPath ? Path.GetDirectoryName(file.Path) : $"{file.Path} → {formatted.NewPath}", wrote);
                file.Path = formatted.NewPath;
                untold.Add(file.Path);
                Reread(file, root, logger);
                file.FormattedAt = now;
            }
            // Saved even if it's cancelled now: the files have been written.
            await db.SaveChangesAsync(CancellationToken.None);
            done += batch.Length;
            progress.Report(Progress(done, total));
            touched.AddRange(untold);
            if (DateTime.UtcNow - told >= TellEvery)
            {
                await TellMediaServerAsync(connectors, untold.Distinct().ToList(), logger, ct);
                untold.Clear();
                told = DateTime.UtcNow;
            }
        }

        // Moving the whole library also tidies away the folders that already had no music in them.
        if (job.Scope.Kind == FormatScopeKind.Library && job.Options.Move)
        {
            var swept = await worker.MusiclessAsync(new MusiclessRequest(true), ct);
            removed.AddRange(swept.Folders);
            foreach (var folder in swept.Folders)
                rows.Put("folder:" + folder, TaskRowState.Done, folder, null, "Removed, as it had no music in it");
            foreach (var failed in swept.Failed)
                rows.Put("folder:" + failed.Path, TaskRowState.Failed, failed.Path, null, $"Couldn't be removed: {failed.Error}");
            foreach (var failed in swept.Failed)
                logger.LogWarning("Couldn't remove {Folder}, which has no music in it: {Reason}", failed.Path, failed.Error);
        }
        foreach (var folder in removed)
            logger.LogInformation("Removed {Folder}, as it had no music left in it", folder);

        // A scan asked for while one runs is ignored, so the last word waits for it, and covers everything written.
        await WaitForMediaServerAsync(connectors, ct);
        await TellMediaServerAsync(connectors, touched.Distinct().ToList(), logger, ct);
        // Moved files may have new IDs on the media server, so the playlists they're in find them there again.
        foreach (var user in await PlaylistSync.FilesMovedAsync(db, moved, CancellationToken.None))
            playlists.Changed(user);
        var summary = $"Formatted {LibraryMatcher.Plural(total - failures.Count, "file", "files")}"
            + (removed.Count == 0 ? "" : $" and removed {LibraryMatcher.Plural(removed.Count, "folder", "folders")} with no music left in them");
        progress.Report(failures.Count == 0 ? summary : $"{summary}; {failures.Count} couldn't be: {failures[0]}");
        logger.LogInformation("{Summary} for {Title}", summary, job.Title);
    }

    /// <summary>The file as it is now, so the next scan sees it unchanged and doesn't match it again.</summary>
    public static void Reread(LibraryFile file, string root, ILogger logger)
    {
        var full = Path.Combine(root, file.Path);
        if (!File.Exists(full))
            return;
        var info = new FileInfo(full);
        file.Size = info.Length;
        file.ModifiedAt = info.LastWriteTimeUtc;
        file.Format = Path.GetExtension(full).TrimStart('.').ToLowerInvariant();
        try
        {
            var (tags, duration, lyrics, quality, genres, gains) = LibraryScanner.ReadTags(full);
            file.TagsJson = tags.ToJson();
            file.Genres = genres;
            file.SetGains(gains);
            // Only its tags were rewritten, so its music's spectrum is as it was checked.
            var (cutoff, checkedAt) = (file.CutoffHz, file.SpectrumCheckedAt);
            file.SetQuality(quality);
            (file.CutoffHz, file.SpectrumCheckedAt) = (cutoff, checkedAt);
            file.Duration = duration ?? file.Duration;
            // Kept when there are none to read: a source may have said it's instrumental.
            if (lyrics != LyricsKind.None || file.Lyrics != LyricsKind.Instrumental)
                file.Lyrics = lyrics;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("Couldn't read the rewritten file {Path}: {Reason}", file.Path, e.Message);
        }
    }

    // The edition's cover, or its album's, from where the options say.
    private async Task<byte[]?> ArtworkAsync(string source, Guid editionId, CancellationToken ct)
    {
        if (source is FormatSources.Keep or FormatSources.Remove)
            return null;
        var albumId = await db.Editions.Where(e => e.Id == editionId).Select(e => e.AlbumId).FirstAsync(ct);
        foreach (var (kind, id) in new[] { (EntityKind.Edition, editionId), (EntityKind.Album, albumId) })
        {
            if (source == FormatSources.Shown)
            {
                if (await artwork.GetAsync(kind, id, ct) is { } shown)
                    return await File.ReadAllBytesAsync(shown.Path, ct);
            }
            else
            {
                try
                {
                    if (await artwork.FetchFromAsync(Guid.Parse(source), kind, id, ct) is { } image)
                        return image.Data;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning("Couldn't get a cover for formatting: {Reason}", e.Message);
                }
            }
        }
        return null;
    }

    // Activity says so while it runs: files being rewritten and moved don't always play.
    private static string Progress(int done, int total) =>
        $"Formatting: {done} of {total} files. Songs may not play reliably until it's done.";

    /// <summary>Waits, for a few minutes at most, while the media server scans.</summary>
    private static async Task WaitForMediaServerAsync(ConnectorPlugins connectors, CancellationToken ct)
    {
        if (await connectors.GetActiveAsync(ct) is not { } active)
            return;
        for (var tries = 0; tries < 90; tries++)
        {
            try
            {
                if (!(await active.Connector.GetScanStatusAsync(ct)).Scanning)
                    return;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    /// <summary>Asks the media server to pick up files that have been written, if there is one.</summary>
    public static async Task TellMediaServerAsync(ConnectorPlugins connectors, IReadOnlyCollection<string> paths, ILogger logger, CancellationToken ct)
    {
        if (paths.Count == 0 || await connectors.GetActiveAsync(ct) is not { } active)
            return;
        try
        {
            await active.Connector.TriggerScanAsync(paths, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("Couldn't ask {Server} to pick up the rewritten files: {Reason}", active.Instance.Name, e.Message);
        }
    }
}

/// <summary>The tags the catalogue gives files, by beets field name, for the groups the options take from it.</summary>
public static class CatalogueTags
{
    public static async Task<Dictionary<Guid, Dictionary<string, string>>> ForAsync(MelodexDbContext db, IReadOnlyList<LibraryFile> files, FormatOptions options,
        CancellationToken ct)
    {
        var result = new Dictionary<Guid, Dictionary<string, string>>();
        bool titles = options.Titles == FormatSources.Catalogue, numbers = options.Numbers == FormatSources.Catalogue,
            release = options.Release == FormatSources.Catalogue, identifiers = options.Identifiers == FormatSources.Catalogue,
            genres = options.Genres is FormatSources.Catalogue or FormatSources.Replace;
        if (!(titles || numbers || release || identifiers || genres))
            return result;

        var trackIds = files.Select(f => f.TrackId).OfType<Guid>().Distinct().ToList();
        var editionIds = files.Select(f => f.EditionId).OfType<Guid>().Distinct().ToList();
        var tracks = await db.Tracks.Where(t => trackIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var editions = await db.Editions.Include(e => e.Album).Where(e => editionIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var places = await db.EditionTracks.Where(et => editionIds.Contains(et.EditionId))
            .Select(et => new { et.EditionId, et.TrackId, et.Disc, et.Position, et.Title }).ToListAsync(ct);
        var entities = trackIds.Concat(editionIds).Concat(editions.Values.Select(e => e.AlbumId))
            .Concat(tracks.Values.Select(t => t.ArtistId)).Concat(editions.Values.Select(e => e.Album.ArtistId)).Distinct().ToList();
        var ids = identifiers
            ? (await db.Crosswalk.Where(m => entities.Contains(m.EntityId) && (m.Provider == Providers.MusicBrainz || m.Provider == Providers.Isrc))
                .OrderBy(m => m.Id).ToListAsync(ct))
              .GroupBy(m => (m.EntityKind, m.EntityId, m.Provider)).ToDictionary(g => g.Key, g => g.First().Value)
            : [];
        string? Id(EntityKind kind, Guid entity, string provider = Providers.MusicBrainz) => ids.GetValueOrDefault((kind, entity, provider));
        var albumIds = editions.Values.Select(e => e.AlbumId).Distinct().ToList();
        var artistIds = editions.Values.Select(e => e.Album.ArtistId).Concat(tracks.Values.Select(t => t.ArtistId)).Distinct().ToList();
        var albumGenres = genres ? await db.Albums.Where(a => albumIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Genres, ct) : [];
        var artistGenres = genres ? await db.Artists.Where(a => artistIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Genres, ct) : [];

        foreach (var file in files)
        {
            if (file.TrackId is not { } trackId || !tracks.TryGetValue(trackId, out var track))
                continue;
            var edition = file.EditionId is { } e ? editions.GetValueOrDefault(e) : null;
            var onEdition = edition is null ? [] : places.Where(p => p.EditionId == edition.Id).ToList();
            var place = onEdition.FirstOrDefault(p => p.TrackId == trackId);
            var tags = new Dictionary<string, string>();
            void Set(string name, string? value)
            {
                if (!string.IsNullOrEmpty(value))
                    tags[name] = value;
            }

            if (titles)
            {
                Set("title", place?.Title ?? track.Title);
                Set("artist", track.ArtistCredit);
                Set("album", edition?.Title);
                Set("albumartist", edition?.Album.ArtistCredit);
            }
            if (numbers && place is not null)
            {
                Set("track", place.Position.ToString());
                Set("tracktotal", onEdition.Count(p => p.Disc == place.Disc).ToString());
                Set("disc", place.Disc.ToString());
                Set("disctotal", onEdition.Max(p => p.Disc).ToString());
            }
            if (release && edition is not null)
            {
                Set("date", edition.ReleaseDate);
                Set("original_date", edition.Album.FirstReleaseDate);
                Set("country", edition.Country);
                Set("media", edition.Formats?.Split(", ")[0]);
                Set("albumtype", edition.Album.Type.ToString().ToLowerInvariant());
            }
            if (identifiers)
            {
                Set("mb_trackid", Id(EntityKind.Track, trackId));
                Set("isrc", Id(EntityKind.Track, trackId, Providers.Isrc));
                Set("mb_artistid", Id(EntityKind.Artist, track.ArtistId));
                if (edition is not null)
                {
                    Set("mb_albumid", Id(EntityKind.Edition, edition.Id));
                    Set("mb_releasegroupid", Id(EntityKind.Album, edition.AlbumId));
                    Set("mb_albumartistid", Id(EntityKind.Artist, edition.Album.ArtistId));
                }
            }
            if (genres)
            {
                // A file's own are left as they are, unless they're to be replaced and the catalogue has some.
                var own = SongGenres.Lines(file.Genres);
                var song = SongGenres.Of(options.Genres == FormatSources.Replace ? null : file.Genres,
                    edition is null ? null : albumGenres.GetValueOrDefault(edition.AlbumId), artistGenres.GetValueOrDefault(edition?.Album.ArtistId ?? track.ArtistId));
                if (song.Count == 0)
                    song = own;
                if (!song.SequenceEqual(own))
                    Set("genres", string.Join('\n', song));
            }
            result[file.Id] = tags;
        }
        return result;
    }
}
