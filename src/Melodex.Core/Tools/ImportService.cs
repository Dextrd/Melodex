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
            import.Lines.Add(Line(i, songs[i], null));
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
        {
            if (line.Import.Albums)
                throw new ImportRefusedException("trackId", "An album can only be left out or put back.");
            await ChooseAsync(line, trackId, ct);
        }
        if (body.Dropped is true)
            line.Outcome = ImportOutcome.Dropped;
        else if (body.Dropped is false && line.Outcome == ImportOutcome.Dropped)
            line.Outcome = await OutcomeAsync(line, ct);
        await db.SaveChangesAsync(ct);
        return Info(line, await MatchesAsync([line], ct));
    }

    /// <summary>
    /// Makes the playlist (or adds to one) in the list's order, with missing songs holding their places, requests those if
    /// asked, and sends the songs that couldn't be matched, or that need checking, to Review or leaves them out.
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
        if (body.Playlist != ImportPlaylistMode.None && !mayPlaylist && !import.Albums)
            throw new ImportRefusedException("playlist", "You need the Listen permission to make playlists.");

        var lines = import.Lines.OrderBy(l => l.Position).ToList();
        var requested = 0;
        if (import.Albums)
        {
            // Albums aren't put in a playlist: those found are requested, and the rest left as they are.
            if (body.RequestMissing)
                foreach (var albumId in lines.Where(l => l.Outcome is ImportOutcome.Found or ImportOutcome.Unsure).Select(l => l.AlbumId).OfType<Guid>().Distinct())
                    if ((await requests.CreateAsync(new CreateRequest(RequestKind.Album, albumId), requester, ct)).Request is not null)
                        requested++;
            import.Status = ImportStatus.Applied;
            import.AppliedAt = DateTime.UtcNow;
            import.RequestMissing = body.RequestMissing;
            await db.SaveChangesAsync(ct);
            return new ImportApplied(null, 0, requested, 0);
        }
        var wanted = lines.Where(l => l.Outcome == ImportOutcome.Found && l.TrackId is not null).ToList();
        if (body.RequestMissing)
            foreach (var trackId in wanted.Select(l => l.TrackId!.Value).Distinct())
                if ((await requests.CreateAsync(new CreateRequest(RequestKind.Track, trackId), requester, ct)).Request is not null)
                    requested++;

        var placed = lines.Where(l => l.Outcome is ImportOutcome.Owned or ImportOutcome.Found && (l.TrackId is not null || l.FileId is not null)).ToList();
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

        // One that needs checking is matched in Review as one that couldn't be, its match the suggestion there.
        var notFound = lines.Where(l => l.Outcome is ImportOutcome.NotFound or ImportOutcome.Unsure).ToList();
        foreach (var line in notFound)
        {
            line.InReview = body.NotFoundToReview;
            if (line.Outcome == ImportOutcome.Unsure && line.TrackId is { } unsure)
                line.SuggestedTrackId = unsure;
        }
        var toReview = body.NotFoundToReview ? notFound.Count : 0;
        import.Status = ImportStatus.Applied;
        import.AppliedAt = DateTime.UtcNow;
        import.PlaylistId = playlistId;
        import.RequestMissing = body.RequestMissing;
        await db.SaveChangesAsync(ct);
        return new ImportApplied(playlistId, placed.Count, requested, toReview);
    }

    // ---- Services' exports ----

    /// <summary>The most a service's export may be: 30 MB.</summary>
    public const int MaxExport = 30 * 1024 * 1024;

    /// <summary>What a service's export holds, with what each list adds to an earlier import of it.</summary>
    public async Task<ServicePeek> PeekServiceAsync(Guid userId, IReadOnlyList<(string Name, byte[] Bytes)> files, CancellationToken ct)
    {
        var export = ReadExport(files);
        var lists = new List<ServiceList>();
        foreach (var list in export.Lists)
        {
            var before = await EarlierAsync(userId, export.Service, list, ct);
            lists.Add(new ServiceList(list.Key, list.Name, list.Kind, list.Items.Count, list.Items.Take(5).Select(i => i.Song).ToList(),
                before.PlaylistId, before.PlaylistName, list.Items.Count(i => i.Id is null || !before.Known.Contains(i.Id))));
        }
        return new ServicePeek(export.Service, lists);
    }

    /// <summary>
    /// Imports the chosen lists of a service's export, one import each, matched in the background. A list imported
    /// before brings only what's new since, to add to the playlist it made; one with nothing new is left out.
    /// </summary>
    public async Task<ImportBatch> CreateServiceAsync(Guid userId, IReadOnlyList<(string Name, byte[] Bytes)> files, CreateServiceImport body, CancellationToken ct)
    {
        var export = ReadExport(files);
        var chosen = export.Lists.Where(l => body.Lists.Contains(l.Key)).ToList();
        if (chosen.Count == 0)
            throw new ImportRefusedException("lists", "Choose something to import.");
        var batch = Guid.CreateVersion7();
        var made = new List<ListImport>();
        var unchanged = 0;
        foreach (var list in chosen)
        {
            var name = ImportName(list, body.Folder, body.LikedName);
            var before = await EarlierAsync(userId, export.Service, list, ct);
            var items = list.Items.Where(i => i.Id is null || !before.Known.Contains(i.Id)).ToList();
            if (items.Count == 0)
            {
                unchanged++;
                continue;
            }
            var import = new ListImport
            {
                UserId = userId, Name = name, Source = (Data.ImportSource)(int)export.Service, BatchId = batch,
                ServiceList = list.Key, Albums = list.Kind == ServiceListKind.Albums, UpdatesPlaylistId = before.PlaylistId,
            };
            for (var i = 0; i < items.Count; i++)
                import.Lines.Add(Line(i, items[i].Song, items[i].Id));
            db.Imports.Add(import);
            made.Add(import);
        }
        await db.SaveChangesAsync(ct);
        foreach (var import in made)
            import.TaskId = (await queue.EnqueueAsync(ImportTask.TaskKind, JsonSerializer.Serialize(import.Id), ct)).Id;
        await db.SaveChangesAsync(ct);
        return new ImportBatch(batch, await SummariesAsync(db.Imports.Where(i => i.BatchId == batch), ct), unchanged);
    }

    /// <summary>The imports made together from one export.</summary>
    public async Task<ImportBatch?> BatchAsync(Guid userId, Guid batchId, CancellationToken ct)
    {
        var imports = await SummariesAsync(db.Imports.Where(i => i.BatchId == batchId && i.UserId == userId), ct);
        return imports.Count == 0 ? null : new ImportBatch(batchId, imports.OrderBy(i => i.Albums).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
    }

    /// <summary>
    /// Applies every import of a batch that's ready: each list's songs make its playlist, or are added to the one an
    /// earlier import of it made; albums are requested.
    /// </summary>
    public async Task<ImportBatchApplied?> ApplyBatchAsync(Guid userId, string requester, bool mayPlaylist, Guid batchId, ApplyImportBatch body, CancellationToken ct)
    {
        var imports = await db.Imports.AsNoTracking().Where(i => i.BatchId == batchId && i.UserId == userId).ToListAsync(ct);
        if (imports.Count == 0)
            return null;
        if (imports.Any(i => i.Status == ImportStatus.Matching))
            throw new ImportRefusedException("import", "Some are still being matched.");
        var (applied, added, requested, toReview) = (0, 0, 0, 0);
        foreach (var import in imports.Where(i => i.Status == ImportStatus.Ready))
        {
            var updates = import.UpdatesPlaylistId is { } existing && await db.Playlists.AnyAsync(p => p.Id == existing && p.UserId == userId && p.DeletedAt == null, ct);
            var mode = import.Albums || !mayPlaylist ? ImportPlaylistMode.None : updates ? ImportPlaylistMode.Existing : ImportPlaylistMode.New;
            if (await ApplyAsync(userId, requester, mayPlaylist, import.Id,
                    new ApplyImport(mode, import.Name, updates ? import.UpdatesPlaylistId : null, body.RequestMissing, body.NotFoundToReview), ct) is not { } done)
                continue;
            applied++;
            added += done.Added;
            requested += done.Requested;
            toReview += done.ToReview;
        }
        return new ImportBatchApplied(applied, added, requested, toReview);
    }

    private static ServiceExport ReadExport(IReadOnlyList<(string Name, byte[] Bytes)> files)
    {
        if (files.Count == 0)
            throw new ImportRefusedException("files", "Choose the export's zip, or the files from it.");
        if (files.Sum(f => (long)f.Bytes.Length) > MaxExport)
            throw new ImportRefusedException("files", "Keep it to 30 MB. If it's bigger, choose only the files with your library and playlists in.");
        try
        {
            return ServiceExports.Read(files)
                ?? throw new ImportRefusedException("files", "That isn't an export Melodex can read. Choose the zip the service sent, or the files from it.");
        }
        catch (InvalidDataException)
        {
            throw new ImportRefusedException("files", "That zip couldn't be opened.");
        }
    }

    // What an import of a list is called: its playlist's name, under the folder chosen.
    private static string ImportName(ServiceListData list, string? folder, string? likedName)
    {
        var name = list.Kind switch
        {
            ServiceListKind.Liked => likedName?.Trim() is { Length: > 0 } liked ? liked : list.Name,
            _ => list.Name,
        };
        var full = list.Kind == ServiceListKind.Albums ? name : $"{folder?.TrimStart() ?? ""}{name}";
        return full.Length > PlaylistService.MaxName ? full[..PlaylistService.MaxName] : full;
    }

    // An earlier import of the same list (known by its name on the service), from the same service: the playlist it made, if it's still there, and the IDs of
    // everything in it and those before it.
    private async Task<(Guid? PlaylistId, string? PlaylistName, HashSet<string> Known)> EarlierAsync(Guid userId, ImportFormat service, ServiceListData list,
        CancellationToken ct)
    {
        var source = (Data.ImportSource)(int)service;
        var albums = list.Kind == ServiceListKind.Albums;
        var earlier = db.Imports.Where(i => i.UserId == userId && i.Source == source && i.ServiceList == list.Key && (!albums || i.Status == ImportStatus.Applied));
        var known = (await db.ImportLines.Where(l => earlier.Any(i => i.Id == l.ImportId) && l.ExternalId != null).Select(l => l.ExternalId!).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        if (albums)
            return (null, null, known);
        var playlist = await earlier.Where(i => i.PlaylistId != null).OrderByDescending(i => i.AppliedAt)
            .Join(db.Playlists.Where(p => p.UserId == userId && p.DeletedAt == null), i => i.PlaylistId, p => p.Id, (i, p) => new { p.Id, p.Name })
            .FirstOrDefaultAsync(ct);
        // Only what made it into a playlist that's still there counts as imported.
        return playlist is null ? (null, null, []) : (playlist.Id, playlist.Name, known);
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
        // Each suggestion as a match would be, from a line standing for it.
        var suggestions = await MatchesAsync(lines.Where(l => l.Line.SuggestedTrackId is not null)
            .Select(l => new ImportLine { Id = l.Line.Id, TrackId = l.Line.SuggestedTrackId }).ToList(), ct);
        return lines.Select(l => new UnmatchedImportSong(l.Line.Id, l.Line.ImportId, l.Name, l.PlaylistId is { } p && names.ContainsKey(p) ? p : null,
            l.PlaylistId is { } q ? names.GetValueOrDefault(q) : null, Song(l.Line), l.AppliedAt ?? l.CreatedAt,
            suggestions.GetValueOrDefault(l.Line.Id), suggestions.ContainsKey(l.Line.Id) ? l.Line.Confidence : null)).ToList();
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
        (line.TrackId, line.FileId, line.Confidence, line.Chosen, line.SuggestedTrackId) = (trackId, null, 1, true, null);
        line.Outcome = await OutcomeAsync(line, ct);
    }

    // What a line is by its match: owned, found or not sure of, or not found.
    private async Task<ImportOutcome> OutcomeAsync(ImportLine line, CancellationToken ct) => line switch
    {
        { AlbumId: { } album } when await db.LibraryFiles.AnyAsync(f => f.EditionId != null && f.Edition!.AlbumId == album && f.HiddenAt == null, ct) => ImportOutcome.Owned,
        { AlbumId: not null, Confidence: >= ImportMatcher.Sure } => ImportOutcome.Found,
        { AlbumId: not null } => ImportOutcome.Unsure,
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
        var albumIds = lines.Select(l => l.AlbumId).OfType<Guid>().Distinct().ToList();
        var albums = albumIds.Count == 0 ? [] : await db.Albums.AsNoTracking().Where(a => albumIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Title, a.ArtistCredit, a.ArtistId }).ToDictionaryAsync(a => a.Id, ct);
        var files = fileIds.Count == 0 ? [] : await db.LibraryFiles.AsNoTracking().Where(f => fileIds.Contains(f.Id))
            .Select(f => new { f.Id, f.TagsJson, f.Path, f.Duration }).ToDictionaryAsync(f => f.Id, ct);

        var matches = new Dictionary<Guid, ImportMatch>();
        foreach (var line in lines)
        {
            if (line.AlbumId is { } albumMatch && albums.TryGetValue(albumMatch, out var matchedAlbum))
                matches[line.Id] = new ImportMatch(null, null, matchedAlbum.Title, matchedAlbum.ArtistCredit, matchedAlbum.ArtistId, matchedAlbum.Id, null,
                    LibraryView.CoverUrl(matchedAlbum.Id, null), null);
            else if (line.TrackId is { } trackId && tracks.TryGetValue(trackId, out var track))
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
        var playlistIds = rows.Select(i => i.PlaylistId).Concat(rows.Select(i => i.UpdatesPlaylistId)).OfType<Guid>().Distinct().ToList();
        var names = await db.Playlists.Where(p => playlistIds.Contains(p.Id) && p.DeletedAt == null).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        return rows.Select(i => new ImportSummary(i.Id, i.Name, (ImportFormat)(int)i.Source, (ImportState)(int)i.Status,
                i.CreatedAt, i.AppliedAt, counts[i.Id].Sum(c => c.Count),
                counts[i.Id].GroupBy(c => c.Outcome).ToDictionary(g => (Client.ImportOutcome)(int)g.Key, g => g.Sum(c => c.Count)),
                i.PlaylistId is { } p && names.ContainsKey(p) ? p : null, i.PlaylistId is { } q ? names.GetValueOrDefault(q) : null,
                i.TaskId, counts[i.Id].Where(c => c.InReview).Sum(c => c.Count), i.BatchId, i.Albums,
                i.UpdatesPlaylistId is { } u && names.ContainsKey(u) ? u : null, i.UpdatesPlaylistId is { } v ? names.GetValueOrDefault(v) : null))
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

    private static ImportLine Line(int position, ImportSong song, string? externalId) => new()
    {
        Position = position,
        Title = Clip(song.Title, 500)!,
        Artist = Clip(song.Artist, 500),
        Album = Clip(song.Album, 500),
        DurationSeconds = song.DurationSeconds,
        Isrc = Clip(song.Isrc, 20),
        MusicBrainzId = Clip(song.MusicBrainzId, 36),
        Path = Clip(song.Path, 2000),
        ExternalId = Clip(externalId, 500),
    };

    private static string? Clip(string? text, int length) => text is null ? null : text.Length > length ? text[..length] : text;
}
