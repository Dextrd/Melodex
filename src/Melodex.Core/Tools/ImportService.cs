using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Playlists;
using Melodex.Core.Requests;
using Melodex.Core.Tasks;
using ImportOutcome = Melodex.Core.Data.ImportOutcome;

namespace Melodex.Core.Tools;

/// <summary>An import that can't be made or applied as asked; the message says why.</summary>
public sealed class ImportRefusedException(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}

/// <summary>
/// Imports from lists: reads one, matches its songs in the background, lets each match be checked and changed, then
/// applies it, making or adding to a playlist, requesting what isn't owned, and sending what wasn't found to Review.
/// </summary>
public sealed class ImportService(MelodexDbContext db, TaskQueue queue, PlaylistService playlists, RequestService requests)
{
    /// <summary>The most a list's text can be: 10 MB.</summary>
    public const int MaxText = 10 * 1024 * 1024;

    public static ImportPeek Peek(PeekImport body) => ListParser.Peek(Check(body.Text), body.FileName);

    public async Task<ImportSummary> CreateAsync(Guid userId, CreateImport body, CancellationToken ct)
    {
        var text = Check(body.Text);
        var format = body.Format ?? ListParser.Detect(text, body.FileName);
        var songs = ListParser.Read(text, format, body.Columns, body.HasHeader);
        if (songs.Count == 0)
            throw new ImportRefusedException("text", format == ImportFormat.Csv
                ? "No songs were read: choose which column holds each song's title."
                : "No songs were read from it.");
        var name = body.Name?.Trim() is { Length: > 0 } given ? given
            : body.FileName is { Length: > 0 } file ? Path.GetFileNameWithoutExtension(file)
            : "Pasted list";
        if (name.Length > PlaylistService.MaxName)
            name = name[..PlaylistService.MaxName];

        var import = new ListImport { UserId = userId, Name = name, Source = (Data.ImportSource)(int)format };
        for (var i = 0; i < songs.Count; i++)
        {
            var song = songs[i];
            import.Lines.Add(new ImportLine
            {
                Position = i,
                Title = Clip(song.Title, 500)!,
                Artist = Clip(song.Artist, 500),
                Album = Clip(song.Album, 500),
                DurationSeconds = song.DurationSeconds,
                Isrc = Clip(song.Isrc, 20),
                MusicBrainzId = Clip(song.MusicBrainzId, 36),
                Path = Clip(song.Path, 2000),
            });
        }
        db.Imports.Add(import);
        await db.SaveChangesAsync(ct);
        var task = await queue.EnqueueAsync(ImportTask.TaskKind, JsonSerializer.Serialize(import.Id), ct);
        import.TaskId = task.Id;
        await db.SaveChangesAsync(ct);
        return (await SummariesAsync(db.Imports.Where(i => i.Id == import.Id), ct))[0];
    }

    public async Task<IReadOnlyList<ImportSummary>> ListAsync(Guid userId, CancellationToken ct) =>
        await SummariesAsync(db.Imports.Where(i => i.UserId == userId), ct);

    public async Task<ImportDetail?> GetAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var summaries = await SummariesAsync(db.Imports.Where(i => i.Id == id && i.UserId == userId), ct);
        if (summaries.Count == 0)
            return null;
        var lines = await db.ImportLines.AsNoTracking().Where(l => l.ImportId == id).OrderBy(l => l.Position).ToListAsync(ct);
        var matches = await MatchesAsync(lines, ct);
        return new ImportDetail(summaries[0], lines.Select(l => Info(l, matches)).ToList());
    }

    /// <summary>Chooses the song a line is, or leaves it out or puts it back, before the import is applied.</summary>
    public async Task<ImportLineInfo?> ChangeLineAsync(Guid userId, Guid id, Guid lineId, ChangeImportLine body, CancellationToken ct)
    {
        var line = await db.ImportLines.Include(l => l.Import).FirstOrDefaultAsync(l => l.Id == lineId && l.ImportId == id && l.Import.UserId == userId, ct);
        if (line is null)
            return null;
        if (line.Import.Status == ImportStatus.Applied)
            throw new ImportRefusedException("import", "It's been applied already.");
        if (body.TrackId is { } trackId)
            await ChooseAsync(line, trackId, ct);
        if (body.Dropped is true)
            line.Outcome = ImportOutcome.Dropped;
        else if (body.Dropped is false && line.Outcome == ImportOutcome.Dropped)
            line.Outcome = await OutcomeAsync(line, ct);
        await db.SaveChangesAsync(ct);
        return Info(line, await MatchesAsync([line], ct));
    }

    /// <summary>
    /// Makes the playlist (or adds to one) in the list's order, with songs not owned holding their places, requests what
    /// isn't owned if asked, and sends what wasn't found to Review or leaves it out.
    /// </summary>
    public async Task<ImportApplied?> ApplyAsync(Guid userId, string requester, bool mayPlaylist, Guid id, ApplyImport body, CancellationToken ct)
    {
        var import = await db.Imports.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id && i.UserId == userId, ct);
        if (import is null)
            return null;
        if (import.Status == ImportStatus.Matching)
            throw new ImportRefusedException("import", "Its songs are still being matched.");
        if (import.Status == ImportStatus.Applied)
            throw new ImportRefusedException("import", "It's been applied already.");
        if (body.Playlist != ImportPlaylistMode.None && !mayPlaylist)
            throw new ImportRefusedException("playlist", "You need the Listen permission to make playlists.");

        var lines = import.Lines.OrderBy(l => l.Position).ToList();
        var wanted = lines.Where(l => l.Outcome is ImportOutcome.Found or ImportOutcome.Unsure && l.TrackId is not null).ToList();
        var requested = 0;
        if (body.RequestMissing)
            foreach (var trackId in wanted.Select(l => l.TrackId!.Value).Distinct())
                if ((await requests.CreateAsync(new CreateRequest(RequestKind.Track, trackId), requester, ct)).Request is not null)
                    requested++;

        var placed = lines.Where(l => l.Outcome is ImportOutcome.Owned or ImportOutcome.Found or ImportOutcome.Unsure && (l.TrackId is not null || l.FileId is not null)).ToList();
        Guid? playlistId = null;
        if (body.Playlist != ImportPlaylistMode.None && placed.Count > 0)
        {
            var songs = await SongsAsync(placed, ct);
            var made = await playlists.ImportAsync(userId, body.Playlist == ImportPlaylistMode.Existing ? body.PlaylistId : null,
                body.Name?.Trim() is { Length: > 0 } name ? name : import.Name, songs, ct)
                ?? throw new ImportRefusedException("playlistId", "That playlist isn't one of yours.");
            playlistId = made.PlaylistId;
            for (var i = 0; i < placed.Count; i++)
                placed[i].EntryId = made.Entries[i];
        }
        else if (body.Playlist == ImportPlaylistMode.Existing && body.PlaylistId is { } existing)
            playlistId = existing;

        var notFound = lines.Where(l => l.Outcome == ImportOutcome.NotFound).ToList();
        foreach (var line in notFound)
            line.InReview = body.NotFoundToReview;
        var toReview = body.NotFoundToReview ? notFound.Count : 0;
        import.Status = ImportStatus.Applied;
        import.AppliedAt = DateTime.UtcNow;
        import.PlaylistId = playlistId;
        import.RequestMissing = body.RequestMissing;
        await db.SaveChangesAsync(ct);
        return new ImportApplied(playlistId, placed.Count, requested, toReview);
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var import = await db.Imports.FirstOrDefaultAsync(i => i.Id == id && i.UserId == userId, ct);
        if (import is null)
            return false;
        if (import.TaskId is { } taskId && await db.BackgroundTasks.FindAsync([taskId], ct) is { } task)
            await queue.CancelAsync(task, ct);
        db.Imports.Remove(import);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ---- Review ----

    /// <summary>Imported songs nothing was found for, waiting in Review.</summary>
    public async Task<IReadOnlyList<UnmatchedImportSong>> UnmatchedAsync(Guid userId, CancellationToken ct)
    {
        var lines = await db.ImportLines.AsNoTracking().Where(l => l.InReview && l.Import.UserId == userId)
            .OrderByDescending(l => l.Import.AppliedAt).ThenBy(l => l.Position)
            .Select(l => new { Line = l, l.Import.Name, l.Import.PlaylistId, l.Import.AppliedAt, l.Import.CreatedAt })
            .ToListAsync(ct);
        var playlistIds = lines.Select(l => l.PlaylistId).OfType<Guid>().Distinct().ToList();
        var names = await db.Playlists.Where(p => playlistIds.Contains(p.Id) && p.DeletedAt == null).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        return lines.Select(l => new UnmatchedImportSong(l.Line.Id, l.Line.ImportId, l.Name, l.PlaylistId is { } p && names.ContainsKey(p) ? p : null,
            l.PlaylistId is { } q ? names.GetValueOrDefault(q) : null, Song(l.Line), l.AppliedAt ?? l.CreatedAt)).ToList();
    }

    /// <summary>
    /// Matches a song waiting in Review: it goes into its import's playlist where it was in the list, and is requested if
    /// the import asked for that and it isn't owned.
    /// </summary>
    public async Task<bool> MatchUnmatchedAsync(Guid userId, string requester, Guid lineId, Guid trackId, CancellationToken ct)
    {
        var line = await db.ImportLines.Include(l => l.Import).FirstOrDefaultAsync(l => l.Id == lineId && l.InReview && l.Import.UserId == userId, ct);
        if (line is null)
            return false;
        await ChooseAsync(line, trackId, ct);
        line.InReview = false;
        if (line.Import.RequestMissing && line.Outcome != ImportOutcome.Owned)
            await requests.CreateAsync(new CreateRequest(RequestKind.Track, trackId), requester, ct);
        if (line.Import.PlaylistId is { } playlistId)
        {
            // Just after the nearest song before it that's in the playlist.
            var before = await db.ImportLines.Where(l => l.ImportId == line.ImportId && l.Position < line.Position && l.EntryId != null)
                .OrderByDescending(l => l.Position).Select(l => l.EntryId).FirstOrDefaultAsync(ct);
            line.EntryId = await playlists.InsertImportedAsync(userId, playlistId, (await SongsAsync([line], ct))[0], before, atStart: before is null, ct);
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Takes a song out of Review without matching it.</summary>
    public async Task<bool> DismissUnmatchedAsync(Guid userId, Guid lineId, CancellationToken ct)
    {
        var line = await db.ImportLines.FirstOrDefaultAsync(l => l.Id == lineId && l.InReview && l.Import.UserId == userId, ct);
        if (line is null)
            return false;
        line.InReview = false;
        line.Outcome = ImportOutcome.Dropped;
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ---- Lines ----

    private async Task ChooseAsync(ImportLine line, Guid trackId, CancellationToken ct)
    {
        if (!await db.Tracks.AnyAsync(t => t.Id == trackId, ct))
            throw new ImportRefusedException("trackId", "That song isn't in the catalogue.");
        (line.TrackId, line.FileId, line.Confidence, line.Chosen) = (trackId, null, 1, true);
        line.Outcome = await OutcomeAsync(line, ct);
    }

    // What a line is by its match: owned, found or not sure of, or not found.
    private async Task<ImportOutcome> OutcomeAsync(ImportLine line, CancellationToken ct) => line switch
    {
        { FileId: not null } => ImportOutcome.Owned,
        { TrackId: { } track } when await db.LibraryFiles.AnyAsync(f => f.TrackId == track && f.HiddenAt == null, ct) => ImportOutcome.Owned,
        { TrackId: not null, Confidence: >= ImportMatcher.Sure } => ImportOutcome.Found,
        { TrackId: not null } => ImportOutcome.Unsure,
        _ => ImportOutcome.NotFound,
    };

    // The songs as a playlist holds them, named as the catalogue or the file has them.
    private async Task<List<ImportedSong>> SongsAsync(IReadOnlyList<ImportLine> lines, CancellationToken ct)
    {
        var matches = await MatchesAsync(lines, ct);
        return lines.Select(l => matches.GetValueOrDefault(l.Id) is { } m
                ? new ImportedSong(m.TrackId, m.FileId, m.Title, m.Artist, m.Album, m.DurationSeconds ?? l.DurationSeconds)
                : new ImportedSong(l.TrackId, l.FileId, l.Title, l.Artist ?? "", l.Album, l.DurationSeconds))
            .ToList();
    }

    /// <summary>What each line was matched to, with its album as the library has it, or where the catalogue has it first.</summary>
    private async Task<Dictionary<Guid, ImportMatch>> MatchesAsync(IReadOnlyList<ImportLine> lines, CancellationToken ct)
    {
        var trackIds = lines.Select(l => l.TrackId).OfType<Guid>().Distinct().ToList();
        var fileIds = lines.Where(l => l.TrackId is null).Select(l => l.FileId).OfType<Guid>().Distinct().ToList();
        var tracks = trackIds.Count == 0 ? [] : await db.Tracks.AsNoTracking().Where(t => trackIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Title, t.ArtistCredit, t.ArtistId, t.Duration }).ToDictionaryAsync(t => t.Id, ct);
        var owned = trackIds.Count == 0 ? [] : (await db.LibraryFiles.AsNoTracking()
                .Where(f => f.TrackId != null && trackIds.Contains(f.TrackId.Value) && f.HiddenAt == null && f.EditionId != null)
                .Select(f => new { TrackId = f.TrackId!.Value, f.EditionId, f.Edition!.AlbumId, f.Edition.Album.Title }).ToListAsync(ct))
            .GroupBy(f => f.TrackId).ToDictionary(g => g.Key, g => g.First());
        var listed = trackIds.Count == 0 ? [] : (await db.EditionTracks.AsNoTracking().Where(et => trackIds.Contains(et.TrackId))
                .Select(et => new { et.TrackId, et.EditionId, et.Edition.AlbumId, et.Edition.Album.Title, et.Edition.Album.FirstReleaseDate }).ToListAsync(ct))
            .GroupBy(x => x.TrackId).ToDictionary(g => g.Key, g => g.OrderBy(x => x.FirstReleaseDate ?? "9999").First());
        var files = fileIds.Count == 0 ? [] : await db.LibraryFiles.AsNoTracking().Where(f => fileIds.Contains(f.Id))
            .Select(f => new { f.Id, f.TagsJson, f.Path, f.Duration }).ToDictionaryAsync(f => f.Id, ct);

        var matches = new Dictionary<Guid, ImportMatch>();
        foreach (var line in lines)
        {
            if (line.TrackId is { } trackId && tracks.TryGetValue(trackId, out var track))
            {
                var (albumId, editionId, album) = owned.TryGetValue(trackId, out var mine) ? (mine.AlbumId, mine.EditionId, mine.Title)
                    : listed.TryGetValue(trackId, out var on) ? (on.AlbumId, (Guid?)on.EditionId, on.Title)
                    : (Guid.Empty, null, null);
                matches[line.Id] = new ImportMatch(trackId, null, track.Title, track.ArtistCredit, track.ArtistId,
                    albumId == Guid.Empty ? null : albumId, album, albumId == Guid.Empty ? null : LibraryView.CoverUrl(albumId, editionId), track.Duration?.TotalSeconds);
            }
            else if (line.FileId is { } fileId && files.TryGetValue(fileId, out var file))
            {
                var tags = FileTags.Parse(file.TagsJson);
                matches[line.Id] = new ImportMatch(null, fileId, tags.Title ?? Path.GetFileNameWithoutExtension(file.Path), tags.Artist ?? tags.AlbumArtist ?? "",
                    null, null, tags.Album, LocalMusic.CoverUrl(fileId), file.Duration?.TotalSeconds);
            }
        }
        return matches;
    }

    private static ImportLineInfo Info(ImportLine l, Dictionary<Guid, ImportMatch> matches) =>
        new(l.Id, l.Position, Song(l), (Client.ImportOutcome)(int)l.Outcome, matches.GetValueOrDefault(l.Id), l.Confidence, l.Chosen, l.InReview);

    private static ImportSong Song(ImportLine l) => new(l.Title, l.Artist, l.Album, l.DurationSeconds, l.Isrc, l.MusicBrainzId, l.Path);

    private async Task<List<ImportSummary>> SummariesAsync(IQueryable<ListImport> imports, CancellationToken ct)
    {
        var rows = await imports.AsNoTracking().OrderByDescending(i => i.CreatedAt).ToListAsync(ct);
        var ids = rows.Select(i => i.Id).ToList();
        var counts = (await db.ImportLines.Where(l => ids.Contains(l.ImportId)).GroupBy(l => new { l.ImportId, l.Outcome, l.InReview })
                .Select(g => new { g.Key.ImportId, g.Key.Outcome, g.Key.InReview, Count = g.Count() }).ToListAsync(ct))
            .ToLookup(c => c.ImportId);
        var playlistIds = rows.Select(i => i.PlaylistId).OfType<Guid>().Distinct().ToList();
        var names = await db.Playlists.Where(p => playlistIds.Contains(p.Id) && p.DeletedAt == null).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        return rows.Select(i => new ImportSummary(i.Id, i.Name, (ImportFormat)(int)i.Source, (ImportState)(int)i.Status,
                i.CreatedAt, i.AppliedAt, counts[i.Id].Sum(c => c.Count),
                counts[i.Id].GroupBy(c => c.Outcome).ToDictionary(g => (Client.ImportOutcome)(int)g.Key, g => g.Sum(c => c.Count)),
                i.PlaylistId is { } p && names.ContainsKey(p) ? p : null, i.PlaylistId is { } q ? names.GetValueOrDefault(q) : null,
                i.TaskId, counts[i.Id].Where(c => c.InReview).Sum(c => c.Count)))
            .ToList();
    }

    private static string Check(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ImportRefusedException("text", "There's nothing to import.");
        if (text.Length > MaxText)
            throw new ImportRefusedException("text", "Keep a list to 10 MB.");
        return text;
    }

    private static string? Clip(string? text, int length) => text is null ? null : text.Length > length ? text[..length] : text;
}
