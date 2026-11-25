using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Melodex.Client;
using Melodex.Core.Data;
using Melodex.Core.Import;
using Melodex.Core.Plugins;
using Melodex.Core.Tasks;

namespace Melodex.Core.Library;

/// <summary>
/// Works out songs' and albums' loudness and writes it into their files as ReplayGain (#23), so they play at an even
/// volume in Melodex's player, Subsonic players and the media server. Each album is done whole, as its gain is its songs'
/// loudness together; a song on no album gets its own gain alone. Only matched files are touched, as formatting does.
/// </summary>
public sealed class ReplayGainTask(
    MelodexDbContext db,
    IImportWorker worker,
    LoudnessMeter meter,
    SettingsService settings,
    ConnectorPlugins connectors,
    TaskFeed feed,
    IOptions<MelodexOptions> options,
    ILogger<ReplayGainTask> logger) : ITaskHandler
{
    public const string TaskKind = "library-replaygain";

    public string Kind => TaskKind;

    /// <summary>What it covers, and its title for the Activity page.</summary>
    public sealed record Job(FormatScope Scope, string Title);

    public static string Title(string? payload) =>
        payload is not null && JsonSerializer.Deserialize<Job>(payload, MelodexJson.Options) is { } job ? $"Even volume · {job.Title}" : "Even volume";

    /// <summary>Queues it, or returns the same one already queued or running.</summary>
    public static async Task<BackgroundTask> StartAsync(MelodexDbContext db, TaskQueue queue, FormatScope scope, CancellationToken ct)
    {
        var title = await LibraryFormatter.TitleAsync(db, scope, ct) ?? throw new KeyNotFoundException();
        return await queue.EnqueueAsync(TaskKind, JsonSerializer.Serialize(new Job(scope, title), MelodexJson.Options), ct);
    }

    /// <summary>The files it works on: matched ones still in the library.</summary>
    public static IQueryable<LibraryFile> Files(MelodexDbContext db, FormatScope scope) =>
        FormatTask.Formattable(FormatTask.Files(db, scope)).Where(f => f.HiddenAt == null);

    public async Task RunAsync(BackgroundTask task, IProgress<string> progress, CancellationToken ct)
    {
        var job = JsonSerializer.Deserialize<Job>(task.PayloadJson!, MelodexJson.Options)!;
        var playback = await settings.GetPlaybackAsync(ct);
        var files = await Files(db, job.Scope)
            .OrderBy(f => f.EditionId).ThenBy(f => f.Path)
            .Select(f => new { f.Id, f.Path, f.EditionId, AlbumId = f.Edition != null ? f.Edition.AlbumId : (Guid?)null, Has = f.TrackGain != null && (f.EditionId == null || f.AlbumGain != null) })
            .ToListAsync(ct);
        // An album whose songs all have it already is left as it is, unless the settings say to work it out again. One
        // that's gained a song is done again whole, as its album gain changes.
        var albums = files.GroupBy(f => f.EditionId is { } edition ? edition.ToString() : f.Id.ToString()).ToList();
        var todo = albums.Where(a => playback.RedoGains || a.Any(f => !f.Has)).ToList();
        var total = todo.Sum(a => a.Count());
        var root = Path.GetFullPath(options.Value.LibraryPath);
        var rows = feed.Start(task);
        var covers = files.ToDictionary(f => f.Id, f => f.AlbumId is { } album ? (Cover: LibraryView.CoverUrl(album, f.EditionId), Href: $"albums/{album}") : (null, null));
        void Show(Guid id, string path, TaskRowState state, string? detail = null) =>
            rows.Put(path, state, Path.GetFileName(path), Path.GetDirectoryName(path), detail, covers.GetValueOrDefault(id).Href, covers.GetValueOrDefault(id).Cover);
        foreach (var file in todo.SelectMany(a => a))
            Show(file.Id, file.Path, TaskRowState.Waiting);

        int done = 0, written = 0;
        var failures = new List<string>();
        var untold = new List<string>();
        var told = DateTime.UtcNow;
        progress.Report(Progress(done, total));
        foreach (var album in todo)
        {
            ct.ThrowIfCancellationRequested();
            var measured = new List<(Guid Id, string Path, Loudness Loudness)>();
            foreach (var file in album)
            {
                Show(file.Id, file.Path, TaskRowState.Working, "Working out its loudness");
                try
                {
                    var loudness = await meter.MeasureAsync(Path.Combine(root, file.Path), ct);
                    if (loudness.Integrated is null)
                        throw new LoudnessException("It's silent");
                    measured.Add((file.Id, file.Path, loudness));
                }
                catch (LoudnessException e)
                {
                    Show(file.Id, file.Path, TaskRowState.Failed, e.Message);
                    failures.Add($"{file.Path}: {e.Message}");
                    logger.LogWarning("Couldn't work out the loudness of {Path}: {Reason}", file.Path, e.Message);
                }
            }
            done += album.Count();
            if (measured.Count == 0)
            {
                progress.Report(Progress(done, total));
                continue;
            }

            // An album's songs together, as one; a song on no album has no album gain.
            var onAlbum = album.First().EditionId is not null;
            var albumLoudness = onAlbum ? Loudness.Of(measured.SelectMany(m => m.Loudness.Blocks)) : null;
            var albumPeak = measured.Max(m => m.Loudness.Peak);
            var request = new FormatRequest(measured.Select(m => new FormatFile(m.Path, new Dictionary<string, string>
            {
                ["replaygain_track_gain"] = Number(playback.LoudnessTarget - m.Loudness.Integrated!.Value),
                ["replaygain_track_peak"] = Peak(m.Loudness.Peak),
                ["replaygain_album_gain"] = albumLoudness is { } l ? Number(playback.LoudnessTarget - l) : "",
                ["replaygain_album_peak"] = albumLoudness is null ? "" : Peak(albumPeak),
            }, null, false)).ToList(), new Dictionary<string, FormatArtwork>(), null);
            foreach (var m in measured)
                Show(m.Id, m.Path, TaskRowState.Working, "Writing");
            FormatResult result;
            try
            {
                result = await worker.FormatAsync(request, ct);
            }
            catch (Exception e) when (e is HttpRequestException or ImportException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                foreach (var m in measured)
                    Show(m.Id, m.Path, TaskRowState.Failed, $"Couldn't be written: {e.Message}");
                failures.Add($"{measured[0].Path}: {e.Message}");
                logger.LogWarning("Couldn't write ReplayGain to {Path} and the rest of its album: {Reason}", measured[0].Path, e.Message);
                progress.Report(Progress(done, total));
                continue;
            }

            db.ChangeTracker.Clear();
            var ids = measured.Select(m => m.Id).ToList();
            var saved = await db.LibraryFiles.Where(f => ids.Contains(f.Id)).ToListAsync(ct);
            foreach (var answer in result.Files)
            {
                if (saved.FirstOrDefault(f => f.Path == answer.Path) is not { } file)
                    continue;
                if (answer.NewPath is null)
                {
                    Show(file.Id, file.Path, TaskRowState.Failed, answer.Error ?? "It couldn't be written");
                    failures.Add($"{answer.Path}: {answer.Error}");
                    logger.LogWarning("Couldn't write ReplayGain to {Path}: {Reason}", answer.Path, answer.Error);
                    continue;
                }
                var loudness = measured.First(m => m.Id == file.Id).Loudness;
                Show(file.Id, file.Path, TaskRowState.Done,
                    $"{loudness.Integrated!.Value.ToString("0.0", CultureInfo.InvariantCulture)} LUFS, {Number(playback.LoudnessTarget - loudness.Integrated.Value)} dB");
                // Read back as the file has them now, so the next scan sees it unchanged.
                FormatTask.Reread(file, root, logger);
                untold.Add(file.Path);
                written++;
            }
            // Saved even if it's cancelled now: the files have been written.
            await db.SaveChangesAsync(CancellationToken.None);
            progress.Report(Progress(done, total));
            if (DateTime.UtcNow - told >= TimeSpan.FromSeconds(20))
            {
                await FormatTask.TellMediaServerAsync(connectors, untold, logger, ct);
                untold.Clear();
                told = DateTime.UtcNow;
            }
        }
        await FormatTask.TellMediaServerAsync(connectors, untold, logger, ct);

        var skipped = files.Count - total;
        var summary = $"Wrote ReplayGain to {LibraryMatcher.Plural(written, "file", "files")}"
                      + (skipped == 0 ? "" : $"; {LibraryMatcher.Plural(skipped, "file", "files")} had it already");
        progress.Report(failures.Count == 0 ? summary : $"{summary}; {failures.Count} couldn't be done: {failures[0]}");
        logger.LogInformation("{Summary} for {Title}", summary, job.Title);
    }

    private static string Progress(int done, int total) => $"Working out loudness: {done} of {total} files";

    private static string Number(double db) => Math.Round(db, 2).ToString("0.00", CultureInfo.InvariantCulture);

    private static string Peak(double peak) => Math.Round(peak, 6).ToString("0.000000", CultureInfo.InvariantCulture);
}
