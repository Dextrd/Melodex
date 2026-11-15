using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Tasks;
using ImportOutcome = Melodex.Core.Data.ImportOutcome;

namespace Melodex.Core.Tools;

/// <summary>
/// Matches an import's songs, one by one, showing each in Activity as it goes. Paused and resumed, it carries on with
/// the songs not matched yet. Once done, the import waits to be checked and applied.
/// </summary>
public sealed class ImportTask(ImportMatcher matcher, MelodexDbContext db, TaskFeed feed) : ITaskHandler
{
    public const string TaskKind = "tools-import";
    private const int BatchSize = 25;

    public string Kind => TaskKind;

    public static string Title(string? name) => name is null ? "Import" : $"Import · {name}";

    public async Task RunAsync(BackgroundTask task, IProgress<string> progress, CancellationToken ct)
    {
        var importId = JsonSerializer.Deserialize<Guid>(task.PayloadJson!);
        if (await db.Imports.AsNoTracking().FirstOrDefaultAsync(i => i.Id == importId, ct) is not { } import)
        {
            progress.Report("The import was deleted");
            return;
        }
        var rows = feed.Start(task);
        var all = await db.ImportLines.Where(l => l.ImportId == importId).OrderBy(l => l.Position)
            .Select(l => new { l.Id, l.Outcome, l.Title, l.Artist }).ToListAsync(ct);
        foreach (var line in all.Where(l => l.Outcome == ImportOutcome.Pending))
            rows.Put(line.Id.ToString(), TaskRowState.Waiting, line.Title, line.Artist);

        var todo = all.Where(l => l.Outcome == ImportOutcome.Pending).Select(l => l.Id).ToList();
        var done = all.Count - todo.Count;
        Report();
        foreach (var batch in todo.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            db.ChangeTracker.Clear();
            var lines = await db.ImportLines.Where(l => batch.Contains(l.Id)).OrderBy(l => l.Position).ToListAsync(ct);
            foreach (var line in lines)
            {
                rows.Put(line.Id.ToString(), TaskRowState.Working, line.Title, line.Artist);
                if (import.Albums)
                    await matcher.MatchAlbumAsync(line, ct);
                else
                    await matcher.MatchAsync(line, ct);
                await db.SaveChangesAsync(ct);
                await RowAsync(rows, line, ct);
                done++;
                Report();
            }
        }

        db.ChangeTracker.Clear();
        await db.Imports.Where(i => i.Id == importId && i.Status == ImportStatus.Matching)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, ImportStatus.Ready), ct);
        var counts = await db.ImportLines.Where(l => l.ImportId == importId).GroupBy(l => l.Outcome)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var parts = new List<string>();
        if (counts.GetValueOrDefault(ImportOutcome.Owned) is > 0 and var owned)
            parts.Add($"{owned} in your library");
        if (counts.GetValueOrDefault(ImportOutcome.Found) is > 0 and var found)
            parts.Add($"{found} to get");
        if (counts.GetValueOrDefault(ImportOutcome.Unsure) is > 0 and var unsure)
            parts.Add($"{unsure} to check");
        if (counts.GetValueOrDefault(ImportOutcome.NotFound) is > 0 and var none)
            parts.Add($"{none} not found");
        progress.Report($"Matched {Things(all.Count)}: {string.Join(", ", parts)}. Check and apply it from Tools.");

        void Report() => progress.Report($"Matching {import.Name}: {done} of {Things(all.Count)}");

        string Things(int count) => import.Albums ? LibraryMatcher.Plural(count, "album", "albums") : LibraryMatcher.Plural(count, "song", "songs");
    }

    // The song as it came out: what it was matched to, and how sure.
    private async Task RowAsync(TaskRows rows, ImportLine line, CancellationToken ct)
    {
        var track = line.TrackId is { } id
            ? await db.Tracks.AsNoTracking().Where(t => t.Id == id).Select(t => new { t.Title, t.ArtistCredit }).FirstOrDefaultAsync(ct)
            : line.AlbumId is { } album
                ? await db.Albums.AsNoTracking().Where(a => a.Id == album).Select(a => new { a.Title, a.ArtistCredit }).FirstOrDefaultAsync(ct)
                : null;
        var what = track is null ? null : $"{track.Title} · {track.ArtistCredit}";
        var (state, detail) = line.Outcome switch
        {
            ImportOutcome.Owned => (TaskRowState.Done, what is null ? "In your library" : $"In your library: {what}"),
            ImportOutcome.Found => (TaskRowState.Done, $"Found: {what}"),
            ImportOutcome.Unsure => (TaskRowState.Skipped, $"Not sure: {what}, {line.Confidence:P0} alike"),
            _ => (TaskRowState.Failed, "Nothing like it in the library or the catalogue"),
        };
        rows.Put(line.Id.ToString(), state, line.Title, line.Artist, detail, line.TrackId is { } trackId ? $"tracks/{trackId}" : line.AlbumId is { } albumId ? $"albums/{albumId}" : null);
    }
}
