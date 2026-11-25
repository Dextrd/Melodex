using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Contracts.Catalogue;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Listening;

namespace Melodex.Core.Subsonic;

/// <summary>A song as players see it: one file in the library.</summary>
public sealed record SubsonicSong(
    LibraryTrack Track, long Size, int? BitrateKbps, int? BitDepth, int? SampleRate, string? Codec, int? Year,
    IReadOnlyList<string> Genres, DateTime Created, string? MusicBrainzId, FileGains? Gains = null)
{
    public Guid Id => Track.FileId;
    public string ContentType => MediaTypes.Bare(MediaTypes.For(Track.Path, Codec));
    public string Suffix => Track.Format ?? Path.GetExtension(Track.Path).TrimStart('.').ToLowerInvariant();
    public int? TrackNumber => int.TryParse(new string((Track.Number ?? "").SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray()), out var n) ? n : null;
}

public sealed record SubsonicAlbum(
    Guid Id, string Name, Guid ArtistId, string Artist, int? Year, string? CoverUrl, IReadOnlyList<SubsonicSong> Songs,
    IReadOnlyList<string> Genres, string? MusicBrainzId, AlbumType Type, IReadOnlyList<AlbumSecondaryType> SecondaryTypes,
    AlbumStatus Status = AlbumStatus.Complete, int? OwnedTracks = null, int? TotalTracks = null)
{
    public DateTime Created => Songs.Count == 0 ? DateTime.UnixEpoch : Songs.Min(s => s.Created);
    public int Duration => Songs.Sum(s => (int)Math.Round(s.Track.DurationSeconds ?? 0));
}

public sealed record SubsonicArtist(Guid Id, string Name, string? SortName, string? ImageUrl, IReadOnlyList<SubsonicAlbum> Albums, string? MusicBrainzId);

/// <summary>
/// The library as Subsonic players browse it (#7): the artists, albums and songs you have, as the Library page lists
/// them, with what players want to know of each file. Worked out again when the library changes, from the library's own
/// lists; IDs are Melodex's, the file's own for a song.
/// </summary>
public sealed class SubsonicLibrary(LibraryCache cache, IServiceScopeFactory scopes)
{
    /// <summary>Articles players leave off when they sort and index artists by name.</summary>
    public const string IgnoredArticles = "The El La Los Las Le Les";

    private sealed record Snapshot(object Artists, object Albums, object Tracks, View View);

    public sealed record View(
        IReadOnlyList<SubsonicArtist> Artists, IReadOnlyDictionary<Guid, SubsonicArtist> ArtistsById,
        IReadOnlyList<SubsonicAlbum> Albums, IReadOnlyDictionary<Guid, SubsonicAlbum> AlbumsById,
        IReadOnlyList<SubsonicSong> Songs, IReadOnlyDictionary<Guid, SubsonicSong> SongsById,
        IReadOnlyDictionary<Guid, SubsonicAlbum> AlbumOfSong);

    private Snapshot? _last;
    private readonly SemaphoreSlim _building = new(1, 1);

    public async Task<View> GetAsync(CancellationToken ct)
    {
        var artists = await cache.GetArtistsAsync(ct);
        var albums = await cache.GetAlbumsAsync(ct);
        var tracks = await cache.GetTracksAsync(ct);
        if (_last is { } last && ReferenceEquals(last.Artists, artists) && ReferenceEquals(last.Albums, albums) && ReferenceEquals(last.Tracks, tracks))
            return last.View;
        await _building.WaitAsync(ct);
        try
        {
            if (_last is { } again && ReferenceEquals(again.Artists, artists) && ReferenceEquals(again.Albums, albums) && ReferenceEquals(again.Tracks, tracks))
                return again.View;
            var view = await BuildAsync(artists, albums, tracks, ct);
            _last = new Snapshot(artists, albums, tracks, view);
            return view;
        }
        finally
        {
            _building.Release();
        }
    }

    private async Task<View> BuildAsync(IReadOnlyList<LibraryArtist> artists, IReadOnlyList<LibraryAlbum> albums, IReadOnlyList<LibraryTrack> tracks,
        CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        var files = await db.LibraryFiles.AsNoTracking().Where(f => f.HiddenAt == null)
            .Select(f => new { f.Id, f.Size, f.BitrateKbps, f.BitDepth, f.SampleRate, f.Codec, f.Genres, f.TagsJson, f.TrackGain, f.TrackPeak, f.AlbumGain, f.AlbumPeak })
            .ToDictionaryAsync(f => f.Id, ct);
        // Songs whose files have no genres take the catalogue's, for their album or else its artist.
        var albumGenres = await db.Albums.AsNoTracking().Where(a => a.Genres != null && a.Genres != "")
            .ToDictionaryAsync(a => a.Id, a => a.Genres, ct);
        var artistGenres = await db.Artists.AsNoTracking().Where(a => a.Genres != null && a.Genres != "")
            .ToDictionaryAsync(a => a.Id, a => a.Genres, ct);
        var albumArtists = albums.GroupBy(a => a.Album.Id).ToDictionary(g => g.Key, g => g.First().Album.ArtistId);

        static int? YearOf(string? date) => date is { Length: >= 4 } && int.TryParse(date[..4], out var year) ? year : null;

        var songs = new List<SubsonicSong>();
        foreach (var track in tracks)
        {
            if (!files.TryGetValue(track.FileId, out var file))
                continue;
            var tags = FileTags.Parse(file.TagsJson);
            var artist = track.AlbumId is { } album && albumArtists.TryGetValue(album, out var albumArtist) ? albumArtist : track.ArtistId;
            var genres = SongGenres.Of(file.Genres, track.AlbumId is { } a ? albumGenres.GetValueOrDefault(a) : null,
                artist is { } r ? artistGenres.GetValueOrDefault(r) : null);
            songs.Add(new SubsonicSong(track, file.Size, file.BitrateKbps, file.BitDepth, file.SampleRate, file.Codec, YearOf(tags.Date), genres,
                CreatedOf(track.FileId), tags.MusicBrainzRecordingId, new FileGains(file.TrackGain, file.TrackPeak, file.AlbumGain, file.AlbumPeak)));
        }
        var byAlbum = songs.Where(s => s.Track.AlbumId is not null).ToLookup(s => s.Track.AlbumId!.Value);

        var albumList = new List<SubsonicAlbum>();
        foreach (var (artistName, summary) in albums)
        {
            var own = byAlbum[summary.Id].OrderBy(s => s.Track.Disc ?? 1).ThenBy(s => s.TrackNumber ?? int.MaxValue)
                .ThenBy(s => s.Track.Number, StringComparer.Ordinal).ThenBy(s => s.Track.Path, StringComparer.Ordinal).ToList();
            if (own.Count == 0)
                continue;
            // An album's songs take its year, where the catalogue knows it.
            var year = YearOf(summary.FirstReleaseDate);
            if (year is not null)
                own = own.Select(s => s with { Year = year }).ToList();
            var genres = SongGenres.Common(own.Select(s => s.Genres));
            albumList.Add(new SubsonicAlbum(summary.Id, summary.Title, summary.ArtistId, summary.ArtistCredit.Length > 0 ? summary.ArtistCredit : artistName,
                year ?? own.Select(s => s.Year).FirstOrDefault(y => y is not null), summary.CoverUrl, own, genres,
                summary.Ids.FirstOrDefault(i => i.Provider == "musicbrainz")?.Value, summary.Type, summary.SecondaryTypes,
                summary.Status, summary.OwnedTracks, summary.TotalTracks));
        }
        var albumsById = albumList.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
        var albumOfSong = albumsById.Values.SelectMany(a => a.Songs.Select(s => (s.Id, a))).ToDictionary(x => x.Id, x => x.a);
        // Songs with their album's year.
        songs = songs.Select(s => albumOfSong.TryGetValue(s.Id, out var a) ? a.Songs.First(x => x.Id == s.Id) : s).ToList();

        var albumsByArtist = albumsById.Values.ToLookup(a => a.ArtistId);
        var artistList = artists
            .Select(a => new SubsonicArtist(a.Id, a.Name, a.SortName, a.ImageUrl,
                albumsByArtist[a.Id].OrderBy(x => x.Year ?? 9999).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
                a.Ids.FirstOrDefault(i => i.Provider == "musicbrainz")?.Value))
            .Where(a => a.Albums.Count > 0)
            .ToList();
        return new View(artistList, artistList.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First()),
            albumList, albumsById, songs, songs.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First()), albumOfSong);
    }

    /// <summary>When a file was first found: its ID is a version 7 GUID, which starts with the time it was made.</summary>
    public static DateTime CreatedOf(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        long ms = 0;
        for (var i = 0; i < 6; i++)
            ms = ms << 8 | bytes[i];
        return ms is > 0 and < 253402300799999 ? DateTime.UnixEpoch.AddMilliseconds(ms) : DateTime.UnixEpoch;
    }

    // ---- As players see them ----

    public static string Iso(DateTime time) => time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static string AlbumCover(Guid id) => $"al-{id}";
    public static string ArtistCover(Guid id) => $"ar-{id}";
    public static string SongCover(Guid id) => $"mf-{id}";

    /// <summary>The name to sort and index by: its sort name, else its name without a leading article.</summary>
    public static string SortKey(SubsonicArtist artist)
    {
        if (!string.IsNullOrWhiteSpace(artist.SortName))
            return artist.SortName;
        foreach (var article in IgnoredArticles.Split(' '))
            if (artist.Name.StartsWith(article + " ", StringComparison.OrdinalIgnoreCase))
                return artist.Name[(article.Length + 1)..];
        return artist.Name;
    }

    /// <summary>The index an artist is listed under: the first letter of its sort key, or # for anything else.</summary>
    public static string IndexOf(SubsonicArtist artist)
    {
        var first = SortKey(artist).Normalize(NormalizationForm.FormD).FirstOrDefault(char.IsLetterOrDigit);
        return char.IsLetter(first) && first < 128 ? char.ToUpperInvariant(first).ToString() : "#";
    }

    public static JsonObject ArtistNode(SubsonicArtist artist) => Compact(new JsonObject
    {
        ["id"] = artist.Id.ToString(),
        ["name"] = artist.Name,
        ["coverArt"] = ArtistCover(artist.Id),
        ["albumCount"] = artist.Albums.Count,
        ["sortName"] = SortKey(artist),
        ["musicBrainzId"] = artist.MusicBrainzId,
        ["roles"] = new JsonArray("artist", "albumartist"),
    });

    public static JsonObject AlbumNode(SubsonicAlbum album, bool asDirectory = false)
    {
        var node = new JsonObject
        {
            ["id"] = album.Id.ToString(),
            ["name"] = album.Name,
            ["artist"] = album.Artist,
            ["artistId"] = album.ArtistId.ToString(),
            ["coverArt"] = AlbumCover(album.Id),
            ["songCount"] = album.Songs.Count,
            ["duration"] = album.Duration,
            ["created"] = Iso(album.Created),
            ["year"] = album.Year,
            ["genre"] = album.Genres.FirstOrDefault(),
            ["genres"] = new JsonArray(album.Genres.Select(g => (JsonNode)new JsonObject { ["name"] = g }).ToArray()),
            ["musicBrainzId"] = album.MusicBrainzId,
            ["displayArtist"] = album.Artist,
            ["sortName"] = album.Name,
            ["releaseTypes"] = new JsonArray(new[] { album.Type.ToString() }.Concat(album.SecondaryTypes.Select(t => t.ToString())).Select(t => (JsonNode)t).ToArray()),
            ["isCompilation"] = album.SecondaryTypes.Contains(AlbumSecondaryType.Compilation),
            // Melodex's own (the melodex extension): whether you have all of it, and how much.
            ["melodexStatus"] = album.Status == AlbumStatus.Partial ? "partial" : "complete",
            ["melodexOwnedSongs"] = album.OwnedTracks,
            ["melodexTotalSongs"] = album.TotalTracks,
        };
        if (asDirectory)
        {
            // As a folder, in getMusicDirectory and getAlbumList.
            node["parent"] = album.ArtistId.ToString();
            node["isDir"] = true;
            node["title"] = album.Name;
            node["album"] = album.Name;
        }
        return Compact(node);
    }

    public static JsonObject SongNode(SubsonicSong song, SubsonicAlbum? album) => Compact(new JsonObject
    {
        ["id"] = song.Id.ToString(),
        ["parent"] = (album?.Id ?? song.Track.AlbumId)?.ToString(),
        ["isDir"] = false,
        ["title"] = song.Track.Title,
        ["album"] = album?.Name ?? song.Track.AlbumTitle,
        ["artist"] = song.Track.ArtistCredit,
        ["track"] = song.TrackNumber,
        ["year"] = song.Year,
        ["genre"] = song.Genres.FirstOrDefault(),
        ["coverArt"] = SongCover(song.Id),
        ["size"] = song.Size,
        ["contentType"] = song.ContentType,
        ["suffix"] = song.Suffix,
        ["duration"] = song.Track.DurationSeconds is { } seconds ? (int)Math.Round(seconds) : null,
        ["bitRate"] = song.BitrateKbps,
        ["bitDepth"] = song.BitDepth,
        ["samplingRate"] = song.SampleRate,
        ["path"] = song.Track.Path,
        ["discNumber"] = song.Track.Disc,
        ["created"] = Iso(song.Created),
        ["albumId"] = (album?.Id ?? song.Track.AlbumId)?.ToString(),
        ["artistId"] = song.Track.ArtistId?.ToString(),
        ["type"] = "music",
        ["mediaType"] = "song",
        ["isVideo"] = false,
        ["displayArtist"] = song.Track.ArtistCredit,
        ["displayAlbumArtist"] = album?.Artist,
        ["sortName"] = song.Track.Title,
        ["musicBrainzId"] = song.MusicBrainzId,
        ["genres"] = new JsonArray(song.Genres.Select(g => (JsonNode)new JsonObject { ["name"] = g }).ToArray()),
        // OpenSubsonic's, as the file's ReplayGain has it (#23).
        ["replayGain"] = song.Gains is { TrackGain: not null } or { AlbumGain: not null } ? Compact(new JsonObject
        {
            ["trackGain"] = song.Gains.TrackGain,
            ["albumGain"] = song.Gains.AlbumGain,
            ["trackPeak"] = song.Gains.TrackPeak,
            ["albumPeak"] = song.Gains.AlbumPeak,
        }) : null,
        // Melodex's own (the melodex extension): the song's ID in Melodex's API, where a file of your own goes by its file's.
        ["melodexTrackId"] = song.Track.TrackId.ToString(),
    });

    /// <summary>Leaves out what isn't known, rather than sending nulls.</summary>
    private static JsonObject Compact(JsonObject node)
    {
        foreach (var name in node.Where(p => p.Value is null).Select(p => p.Key).ToList())
            node.Remove(name);
        return node;
    }
}
