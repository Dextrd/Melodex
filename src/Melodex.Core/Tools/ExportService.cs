using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Contracts.Catalogue;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Playlists;

namespace Melodex.Core.Tools;

/// <summary>A file an export made: its bytes, its type and the name to save it as.</summary>
public sealed record ExportFile(byte[] Bytes, string ContentType, string Name);

/// <summary>
/// Exports, for other players, other services' importers, or keeping: playlists as M3U8 or CSV, and the library as CSV.
/// A playlist's CSV is the shape the list import reads back.
/// </summary>
public sealed class ExportService(MelodexDbContext db, PlaylistService playlists, LibraryView library)
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Playlists of the user's, as one file, or a zip of them if there are several. Null if one isn't theirs.</summary>
    public async Task<ExportFile?> PlaylistsAsync(Guid userId, IReadOnlyList<Guid> ids, PlaylistExportFormat format, bool keepMissing, string? prefix, CancellationToken ct)
    {
        if (ids.Count == 0)
            throw new ImportRefusedException("id", "Choose a playlist to export.");
        var details = new List<PlaylistDetail>();
        foreach (var id in ids.Distinct())
        {
            if (await playlists.GetAsync(userId, id, ct) is not { } detail)
                return null;
            details.Add(detail);
        }

        var songs = details.SelectMany(d => d.Songs).ToList();
        var paths = await PathsAsync(songs.Where(Had).ToList(), ct);
        var known = await IdsAsync(songs.Select(s => s.TrackId).OfType<Guid>().Distinct().ToList(), ct);
        var extension = format == PlaylistExportFormat.M3u8 ? "m3u8" : "csv";

        byte[] Write(PlaylistDetail detail) => format == PlaylistExportFormat.M3u8
            ? M3u8(detail, paths, keepMissing, prefix)
            : Csv(detail, paths, known, keepMissing);

        if (details.Count == 1)
            return new ExportFile(Write(details[0]), format == PlaylistExportFormat.M3u8 ? "audio/x-mpegurl" : "text/csv",
                $"{FileName(details[0].Playlist.Name)}.{extension}");

        // Several go in a zip, each named as its playlist is; the same name twice gets a number.
        using var zipped = new MemoryStream();
        using (var zip = new ZipArchive(zipped, ZipArchiveMode.Create, leaveOpen: true))
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var detail in details)
            {
                var name = string.Join('/', detail.Playlist.Name.Split('/').Select(FileName));
                var entry = $"{name}.{extension}";
                for (var n = 2; !taken.Add(entry); n++)
                    entry = $"{name} ({n}).{extension}";
                await using var stream = zip.CreateEntry(entry).Open();
                await stream.WriteAsync(Write(detail), ct);
            }
        }
        return new ExportFile(zipped.ToArray(), "application/zip", $"Playlists {DateTime.Now:yyyy-MM-dd}.zip");
    }

    /// <summary>The library as a CSV: a row for each song you have, or each album you have some of.</summary>
    public async Task<ExportFile> LibraryAsync(LibraryExportRows rows, IReadOnlyList<LibraryExportColumn> columns, CancellationToken ct)
    {
        var allowed = LibraryExportColumns.For(rows);
        var chosen = columns.Count == 0 ? allowed : allowed.Where(columns.Contains).ToList();
        if (chosen.Count == 0)
            throw new ImportRefusedException("column", "Choose a column to export.");

        var albums = await library.GetLibraryAlbumsAsync(ct);
        var csv = new StringBuilder();
        Row(csv, chosen.Select(LibraryExportColumns.Name));
        if (rows == LibraryExportRows.Albums)
        {
            var ids = chosen.Contains(LibraryExportColumn.MusicBrainzId) ? await IdsAsync(albums.Select(a => a.Album.Id).ToList(), ct, EntityKind.Album) : [];
            foreach (var album in albums.OrderBy(a => a.ArtistName, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Album.FirstReleaseDate ?? "9999", StringComparer.Ordinal))
            {
                var a = album.Album;
                Row(csv, chosen.Select(column => column switch
                {
                    LibraryExportColumn.Artist => album.ArtistName,
                    LibraryExportColumn.Album => a.Title,
                    LibraryExportColumn.Year => Year(a.FirstReleaseDate),
                    LibraryExportColumn.Owned => a.Status == AlbumStatus.Complete || a.TotalTracks is null ? "All" : $"{a.OwnedTracks} of {a.TotalTracks}",
                    LibraryExportColumn.Format => a.Quality?.Codec?.ToUpperInvariant(),
                    LibraryExportColumn.Quality => a.Quality is { } q ? (a.MixedQuality ? $"{q.Label}, mixed" : q.Label) : null,
                    LibraryExportColumn.MusicBrainzId => ids.GetValueOrDefault(a.Id).Mbid,
                    _ => null,
                }));
            }
        }
        else
        {
            var tracks = await library.GetLibraryTracksAsync(ct);
            var years = albums.ToDictionary(a => a.Album.Id, a => Year(a.Album.FirstReleaseDate));
            var ids = chosen.Contains(LibraryExportColumn.Isrc) || chosen.Contains(LibraryExportColumn.MusicBrainzId)
                ? await IdsAsync(tracks.Where(t => !t.Local).Select(t => t.TrackId).Distinct().ToList(), ct)
                : [];
            foreach (var track in tracks.OrderBy(t => t.ArtistCredit, StringComparer.CurrentCultureIgnoreCase)
                         .ThenBy(t => t.AlbumTitle, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Disc).ThenBy(t => t.Path, StringComparer.Ordinal))
            {
                var known = track.Local ? default : ids.GetValueOrDefault(track.TrackId);
                Row(csv, chosen.Select(column => column switch
                {
                    LibraryExportColumn.Artist => track.ArtistCredit,
                    LibraryExportColumn.Album => track.AlbumTitle,
                    LibraryExportColumn.Title => track.Title,
                    LibraryExportColumn.Year => track.AlbumId is { } album ? years.GetValueOrDefault(album) : null,
                    LibraryExportColumn.Disc => track.Disc?.ToString(CultureInfo.InvariantCulture),
                    LibraryExportColumn.Track => track.Number,
                    LibraryExportColumn.Length => Length(track.DurationSeconds),
                    LibraryExportColumn.Path => track.Path,
                    LibraryExportColumn.Format => track.Format?.ToUpperInvariant(),
                    LibraryExportColumn.Quality => track.Quality?.Label,
                    LibraryExportColumn.Isrc => known.Isrc,
                    LibraryExportColumn.MusicBrainzId => known.Mbid,
                    _ => null,
                }));
            }
        }
        return new ExportFile(Utf8.GetBytes(csv.ToString()), "text/csv",
            $"Library {(rows == LibraryExportRows.Songs ? "songs" : "albums")} {DateTime.Now:yyyy-MM-dd}.csv");
    }

    // A song with a file to point at.
    private static bool Had(PlaylistSong song) => song.State is PlaylistSongState.Library or PlaylistSongState.Local;

    // Each song's file, by its entry: a file of your own is its own, and a song you have is its track's (the first, if
    // there are copies).
    private async Task<Dictionary<Guid, string>> PathsAsync(IReadOnlyList<PlaylistSong> songs, CancellationToken ct)
    {
        var fileIds = songs.Select(s => s.FileId).OfType<Guid>().Distinct().ToList();
        var trackIds = songs.Where(s => s.FileId is null).Select(s => s.TrackId).OfType<Guid>().Distinct().ToList();
        var byFile = new Dictionary<Guid, string>();
        var byTrack = new Dictionary<Guid, string>();
        foreach (var chunk in fileIds.Chunk(500))
            foreach (var f in await db.LibraryFiles.Where(f => chunk.Contains(f.Id)).Select(f => new { f.Id, f.Path }).ToListAsync(ct))
                byFile[f.Id] = f.Path;
        foreach (var chunk in trackIds.Chunk(500))
            foreach (var f in (await db.LibraryFiles.Where(f => f.TrackId != null && chunk.Contains(f.TrackId.Value) && f.HiddenAt == null)
                         .Select(f => new { TrackId = f.TrackId!.Value, f.Path }).ToListAsync(ct)).OrderBy(f => f.Path, StringComparer.Ordinal))
                byTrack.TryAdd(f.TrackId, f.Path);
        return songs.Select(s => (s.EntryId, Path: s.FileId is { } file ? byFile.GetValueOrDefault(file) : s.TrackId is { } track ? byTrack.GetValueOrDefault(track) : null))
            .Where(s => s.Path is not null).ToDictionary(s => s.EntryId, s => s.Path!);
    }

    private static byte[] M3u8(PlaylistDetail detail, IReadOnlyDictionary<Guid, string> paths, bool keepMissing, string? prefix)
    {
        var text = new StringBuilder("#EXTM3U\n");
        text.Append("#PLAYLIST:").Append(OneLine(detail.Playlist.Name)).Append('\n');
        var root = string.IsNullOrWhiteSpace(prefix) ? null : prefix.Trim().TrimEnd('/', '\\');
        foreach (var song in detail.Songs)
        {
            var path = paths.GetValueOrDefault(song.EntryId);
            if (path is null && !keepMissing)
                continue;
            var length = song.DurationSeconds is { } seconds ? ((int)Math.Round(seconds)).ToString(CultureInfo.InvariantCulture) : "-1";
            text.Append("#EXTINF:").Append(length).Append(',').Append(OneLine(song.Artist.Length > 0 ? $"{song.Artist} - {song.Title}" : song.Title)).Append('\n');
            if (song.AlbumTitle is { Length: > 0 } album)
                text.Append("#EXTALB:").Append(OneLine(album)).Append('\n');
            if (path is not null)
                text.Append(root is null ? path : $"{root}/{path}").Append('\n');
        }
        return Utf8.GetBytes(text.ToString());
    }

    private static byte[] Csv(PlaylistDetail detail, IReadOnlyDictionary<Guid, string> paths, IReadOnlyDictionary<Guid, (string? Isrc, string? Mbid)> ids, bool keepMissing)
    {
        var csv = new StringBuilder();
        Row(csv, ["Artist", "Title", "Album", "Length", "ISRC", "MusicBrainz ID"]);
        foreach (var song in detail.Songs)
        {
            if (!keepMissing && !paths.ContainsKey(song.EntryId))
                continue;
            var known = song.State == PlaylistSongState.Local || song.TrackId is not { } track ? default : ids.GetValueOrDefault(track);
            Row(csv, [song.Artist, song.Title, song.AlbumTitle, Length(song.DurationSeconds), known.Isrc, known.Mbid]);
        }
        return Utf8.GetBytes(csv.ToString());
    }

    // Each track's or album's ISRC and MusicBrainz ID, from the crosswalk: the first of each, as a track can have several.
    private async Task<Dictionary<Guid, (string? Isrc, string? Mbid)>> IdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct, EntityKind kind = EntityKind.Track)
    {
        var found = new List<(Guid Id, string Provider, string Value)>();
        foreach (var chunk in ids.Chunk(500))
            found.AddRange((await db.Crosswalk.Where(m => m.EntityKind == kind && chunk.Contains(m.EntityId)
                        && (m.Provider == Providers.Isrc || m.Provider == Providers.MusicBrainz))
                    .OrderBy(m => m.Id).Select(m => new { m.EntityId, m.Provider, m.Value }).ToListAsync(ct))
                .Select(m => (m.EntityId, m.Provider, m.Value)));
        return found.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => (
            (string?)g.FirstOrDefault(m => m.Provider == Providers.Isrc).Value,
            (string?)g.FirstOrDefault(m => m.Provider == Providers.MusicBrainz).Value));
    }

    private static string? Year(string? date) => date is { Length: >= 4 } ? date[..4] : null;

    private static string? Length(double? seconds) => seconds is { } s
        ? TimeSpan.FromSeconds(Math.Round(s)) is var t && t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture)
        : null;

    private static void Row(StringBuilder csv, IEnumerable<string?> cells)
    {
        csv.AppendJoin(',', cells.Select(Cell));
        csv.Append("\r\n");
    }

    // Quoted when it has to be; a cell starting as a formula would be is quoted with a ' first, so a spreadsheet shows it as text.
    private static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        if (value[0] is '=' or '+' or '-' or '@' && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    private static string OneLine(string text) => text.Replace('\n', ' ').Replace('\r', ' ');

    private static string FileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var safe = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim('.', ' ');
        return safe.Length == 0 ? "Playlist" : safe;
    }
}
