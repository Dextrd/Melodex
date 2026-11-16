using Microsoft.EntityFrameworkCore;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins.Index;
using Melodex.Core.Catalogue;
using Melodex.Core.Data;
using Melodex.Core.Library;

namespace Melodex.Core.Tools;

/// <summary>
/// Works out what an imported song is: a song in the library, by the path a playlist file gave, its IDs, or its title,
/// artist and length, as playlist sync finds songs; else a song in the catalogue, by its MusicBrainz ID or ISRC, or by
/// searching each catalogue in turn for its title and artist. One made from a search is only sure when its title,
/// artist and length (where known) agree; otherwise it's the closest, to check.
/// </summary>
public sealed class ImportMatcher(MelodexDbContext db, CatalogueService catalogue, ILogger<ImportMatcher> logger)
{
    /// <summary>A search hit this close is taken as the song.</summary>
    public const double Sure = 0.85;

    /// <summary>Closer than this, it's offered as the closest, to check; further, nothing was found.</summary>
    public const double Close = 0.55;

    /// <summary>Not found, but as close as this, it's suggested in Review.</summary>
    public const double Suggest = 0.35;

    private const int SearchSize = 10;

    private Library? _library;

    /// <summary>Matches the song, unless its match was chosen by hand.</summary>
    public async Task MatchAsync(ImportLine line, CancellationToken ct)
    {
        if (line.Chosen || line.Outcome == ImportOutcome.Dropped)
            return;
        (line.TrackId, line.FileId, line.Confidence, line.SuggestedTrackId) = (null, null, null, null);

        if (await OwnedAsync(line, ct) is { } owned)
        {
            (line.TrackId, line.FileId) = owned;
            line.Outcome = ImportOutcome.Owned;
            line.Confidence = 1;
            return;
        }

        if (!await catalogue.HasCatalogueAsync(ct))
        {
            line.Outcome = ImportOutcome.NotFound;
            return;
        }

        // IDs the catalogue knows already, then an ISRC the core catalogue can look up.
        foreach (var id in Ids(line))
            if (await catalogue.FindAsync(EntityKind.Track, id, ct) is { } known)
            {
                await SetAsync(line, known, 1, ct);
                return;
            }
        var plugin = await catalogue.PluginAsync(ct);
        if (line.Isrc is { } isrc && plugin is IIsrcLookup lookup && (await lookup.LookupIsrcAsync(isrc, ct)).FirstOrDefault() is { } byIsrc)
        {
            await SetAsync(line, (await ImportAsync(byIsrc, ct)).Track.Id, 1, ct);
            return;
        }

        // Each catalogue in order, the core one first: a later one is only asked when nothing sure was found before it.
        var hits = new List<(IndexTrack Track, double Score)>();
        var indexes = await catalogue.IndexesAsync(ct);
        for (var i = 0; i < indexes.Count && !hits.Any(h => h.Score >= Sure); i++)
        {
            IndexSearchResults results;
            try
            {
                results = await indexes[i].Plugin.SearchAsync(new IndexSearchQuery($"{line.Title} {line.Artist}".Trim(), new HashSet<SearchKind> { SearchKind.Track }, SearchSize,
                    new IndexSearchFields(line.Title, line.Artist, line.Album)), ct);
            }
            catch (Exception e) when (i > 0 && LibraryMatcher.IsUnavailable(e, ct))
            {
                logger.LogDebug(e, "{Catalogue} didn't answer for {Title}", indexes[i].Instance.Name, line.Title);
                continue;
            }
            foreach (var hit in results.Tracks)
            {
                // A MusicBrainz ID the catalogue hadn't seen picks its recording out of the results.
                var score = line.MusicBrainzId is { } mbid && hit.Id.Provider == Providers.MusicBrainz && string.Equals(hit.Id.Value, mbid, StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : Score(line, hit);
                hits.Add((hit, score));
            }
        }

        // The closest, in the catalogues' order where they're as close; but one on no edition can't be asked for, so the
        // next closest few are tried before settling for it.
        var close = hits.Where(h => h.Score >= Close).OrderByDescending(h => h.Score).Take(3).ToList();
        if (close.Count == 0)
        {
            line.Outcome = ImportOutcome.NotFound;
            // The closest there was, to suggest when it's matched in Review.
            if (hits.MaxBy(h => h.Score) is { Score: >= Suggest } closest)
                (line.SuggestedTrackId, line.Confidence) = ((await ImportAsync(closest.Track, ct)).Track.Id, Math.Round(closest.Score, 3));
            return;
        }
        (Guid Id, double Score)? first = null;
        foreach (var (hit, score) in close)
        {
            var (track, placed) = await ImportAsync(hit, ct);
            if (placed)
            {
                await SetAsync(line, track.Id, score, ct);
                return;
            }
            first ??= (track.Id, score);
        }
        await SetAsync(line, first!.Value.Id, first.Value.Score, ct);
    }

    /// <summary>
    /// Matches a saved album: one you have some of, by its title and artist, or else the closest the catalogues have.
    /// </summary>
    public async Task MatchAlbumAsync(ImportLine line, CancellationToken ct)
    {
        if (line.Outcome == ImportOutcome.Dropped)
            return;
        (line.AlbumId, line.Confidence) = (null, null);
        var owned = _albums ??= await db.LibraryFiles.AsNoTracking().Where(f => f.EditionId != null && f.HiddenAt == null)
            .Select(f => new OwnedAlbum(f.Edition!.AlbumId, f.Edition.Album.Title, f.Edition.Album.ArtistCredit)).Distinct().ToListAsync(ct);
        var mine = owned.Select(a => (Album: a, Score: AlbumScore(line, a.Title, a.ArtistCredit))).MaxBy(a => a.Score);
        if (mine.Score >= Sure)
        {
            (line.AlbumId, line.Confidence, line.Outcome) = (mine.Album.Id, mine.Score, ImportOutcome.Owned);
            return;
        }
        if (!await catalogue.HasCatalogueAsync(ct))
        {
            line.Outcome = ImportOutcome.NotFound;
            return;
        }

        (IndexAlbum Album, double Score)? best = null;
        var indexes = await catalogue.IndexesAsync(ct);
        for (var i = 0; i < indexes.Count && best is not { Score: >= Sure }; i++)
        {
            IndexSearchResults results;
            try
            {
                results = await indexes[i].Plugin.SearchAsync(new IndexSearchQuery($"{line.Title} {line.Artist}".Trim(), new HashSet<SearchKind> { SearchKind.Album }, SearchSize,
                    new IndexSearchFields(line.Title, line.Artist)), ct);
            }
            catch (Exception e) when (i > 0 && LibraryMatcher.IsUnavailable(e, ct))
            {
                logger.LogDebug(e, "{Catalogue} didn't answer for {Title}", indexes[i].Instance.Name, line.Title);
                continue;
            }
            foreach (var hit in results.Albums)
                if (AlbumScore(line, hit.Title, hit.ArtistCredit) is var score && (best is null || score > best.Value.Score))
                    best = (hit, score);
        }
        if (best is not { Score: >= Close } found)
        {
            line.Outcome = ImportOutcome.NotFound;
            return;
        }
        var album = await catalogue.ImportAlbumUnlessKnownAsync(found.Album, ct);
        await db.SaveChangesAsync(ct);
        line.AlbumId = album.Id;
        line.Confidence = Math.Round(found.Score, 3);
        line.Outcome = owned.Any(a => a.Id == album.Id) ? ImportOutcome.Owned : found.Score >= Sure ? ImportOutcome.Found : ImportOutcome.Unsure;
    }

    private sealed record OwnedAlbum(Guid Id, string Title, string ArtistCredit);

    private List<OwnedAlbum>? _albums;

    /// <summary>How alike an album and one named are: its title most, then its artist; another artist keeps it below sure.</summary>
    public static double AlbumScore(ImportLine line, string title, string artistCredit)
    {
        var titles = TextMatch.Similarity(TextMatch.NormaliseAlbum(line.Title), TextMatch.NormaliseAlbum(title));
        var artist = line.Artist is null ? 0.5 : TextMatch.ArtistSimilarity(line.Artist, artistCredit);
        var score = 0.65 * titles + 0.35 * artist;
        if (titles < 0.8 || artist < 0.5)
            score = Math.Min(score, 0.6);
        return Math.Round(score, 3);
    }

    /// <summary>
    /// How alike a song and a search hit are, from 0 to 1: the title counts most, then the artist, then the album. A
    /// length well out, a title that isn't close, or another artist keeps it below sure.
    /// </summary>
    public static double Score(ImportLine line, IndexTrack hit)
    {
        var title = TextMatch.Similarity(TextMatch.NormaliseTitle(line.Title), TextMatch.NormaliseTitle(hit.Title));
        var artist = line.Artist is null ? 0.5 : TextMatch.ArtistSimilarity(line.Artist, hit.ArtistCredit);
        var album = line.Album is null || hit.Album is null ? 0.5 : TextMatch.Similarity(TextMatch.NormaliseAlbum(line.Album), TextMatch.NormaliseAlbum(hit.Album.Title));
        var score = 0.6 * title + 0.3 * artist + 0.1 * album;
        if (line.DurationSeconds is { } length && hit.Duration is { } duration)
        {
            var difference = Math.Abs(duration.TotalSeconds - length);
            if (difference <= 3)
                score = Math.Min(1, score + 0.05);
            else if (difference > 10)
                score = Math.Min(score, 0.7);
        }
        if (title < 0.8)
            score = Math.Min(score, 0.6);
        if (artist < 0.5)
            score = Math.Min(score, 0.6);
        return Math.Round(score, 3);
    }

    private static IEnumerable<ProviderId> Ids(ImportLine line)
    {
        if (line.MusicBrainzId is { } mbid)
            yield return new ProviderId(Providers.MusicBrainz, mbid);
        if (line.Isrc is { } isrc)
            yield return new ProviderId(Providers.Isrc, isrc);
    }

    // The track, with the album it's best known from, so it has a page and a cover. Unless it's owned, that album's
    // editions are fetched too, so asking for the track has an edition to file it under; placed says whether it has one
    // (or is owned).
    private async Task<(Track Track, bool Placed)> ImportAsync(IndexTrack hit, CancellationToken ct)
    {
        var track = await catalogue.ImportTrackAsync(hit, ct);
        var album = hit.Album is { } source ? await catalogue.ImportAlbumUnlessKnownAsync(source, ct) : null;
        await db.SaveChangesAsync(ct);
        if (await db.LibraryFiles.AnyAsync(f => f.TrackId == track.Id && f.HiddenAt == null, ct) || await db.EditionTracks.AnyAsync(et => et.TrackId == track.Id, ct))
            return (track, true);
        if (album is null)
            return (track, false);
        try
        {
            await catalogue.RefreshEditionsAsync(album, false, ct);
        }
        catch (Exception e) when (LibraryMatcher.IsUnavailable(e, ct))
        {
            // Not known yet, rather than on none: it's tried again when it's asked for.
            logger.LogDebug(e, "Couldn't fetch the editions of {Album}", album.Title);
            return (track, true);
        }
        return (track, await db.EditionTracks.AnyAsync(et => et.TrackId == track.Id, ct));
    }

    private async Task SetAsync(ImportLine line, Guid trackId, double confidence, CancellationToken ct)
    {
        line.TrackId = trackId;
        line.Confidence = Math.Round(confidence, 3);
        line.Outcome = await db.LibraryFiles.AnyAsync(f => f.TrackId == trackId && f.HiddenAt == null, ct) ? ImportOutcome.Owned
            : confidence >= Sure ? ImportOutcome.Found
            : ImportOutcome.Unsure;
    }

    // ---- The library ----

    private sealed record Song(Guid Id, Guid? TrackId, string Path, TimeSpan? Duration, string? Album, string? Released, HashSet<string> Titles, HashSet<string> Artists);

    private sealed record Library(List<Song> Songs, ILookup<string, Song> ByTitle);

    /// <summary>The library song it is: the file at its path, the track its IDs are, or one with its title, an artist in common and its length.</summary>
    private async Task<(Guid? TrackId, Guid? FileId)?> OwnedAsync(ImportLine line, CancellationToken ct)
    {
        var library = _library ??= await LoadAsync(ct);
        if (line.Path is { Length: > 0 } path && library.Songs.FirstOrDefault(s => SamePath(path, s.Path)) is { } atPath)
            return Of(atPath);

        foreach (var id in Ids(line))
            if (await catalogue.FindAsync(EntityKind.Track, id, ct) is { } trackId && library.Songs.Any(s => s.TrackId == trackId))
                return (trackId, null);

        var artists = line.Artist is null ? [] : SongNames.ArtistKeys(line.Artist).ToHashSet();
        var album = line.Album is null ? null : SongNames.TitleKey(line.Album);
        var named = library.ByTitle[SongNames.TitleKey(line.Title)]
            .Where(s => artists.Count == 0 ? line.DurationSeconds is not null : s.Artists.Overlaps(artists))
            .Where(s => s.Duration is null || line.DurationSeconds is not { } length || Math.Abs(s.Duration.Value.TotalSeconds - length) <= 3)
            .OrderBy(s => album is not null && s.Album is not null && SongNames.TitleKey(s.Album) == album ? 0 : 1)
            .ThenBy(s => s.TrackId is null ? 1 : 0)
            // Otherwise the song as it first came out, such as Glory Box on Dummy, not a later live album's.
            .ThenBy(s => s.Released ?? "9999", StringComparer.Ordinal)
            .ThenBy(s => s.Path, StringComparer.Ordinal)
            .FirstOrDefault();
        return named is null ? null : Of(named);

        static (Guid?, Guid?) Of(Song song) => song.TrackId is { } track ? (track, null) : (null, song.Id);
    }

    private async Task<Library> LoadAsync(CancellationToken ct)
    {
        var songs = (await db.LibraryFiles.AsNoTracking().Where(f => f.HiddenAt == null && (f.TrackId != null || f.ExcludedAt != null))
                .Select(f => new { f.Id, f.TrackId, f.Path, f.TagsJson, Title = f.Track == null ? null : f.Track.Title, Artist = f.Track == null ? null : f.Track.ArtistCredit,
                    Duration = f.Duration ?? (f.Track == null ? null : f.Track.Duration), Released = f.Edition == null ? null : f.Edition.Album.FirstReleaseDate })
                .ToListAsync(ct))
            .Select(f =>
            {
                var tags = FileTags.Parse(f.TagsJson);
                return new Song(f.Id, f.TrackId, f.Path, f.Duration, tags.Album, f.Released,
                    new[] { f.Title, tags.Title }.OfType<string>().Select(SongNames.TitleKey).Where(k => k.Length > 0).ToHashSet(),
                    new[] { f.Artist, tags.Artist, tags.AlbumArtist }.OfType<string>().SelectMany(SongNames.ArtistKeys).ToHashSet());
            })
            .ToList();
        return new Library(songs, songs.SelectMany(s => s.Titles.Select(t => (Title: t, Song: s))).ToLookup(x => x.Title, x => x.Song));
    }

    // A playlist's path ends with the library's, as its music folder may be somewhere else where it was made.
    private static bool SamePath(string given, string ours)
    {
        given = given.Replace('\\', '/').TrimStart('/');
        ours = ours.Replace('\\', '/').TrimStart('/');
        return given.EndsWith(ours, StringComparison.OrdinalIgnoreCase) && (given.Length == ours.Length || given[given.Length - ours.Length - 1] == '/');
    }
}
