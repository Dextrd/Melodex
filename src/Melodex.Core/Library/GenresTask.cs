using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Contracts.Plugins.Index;
using Melodex.Core.Catalogue;
using Melodex.Core.Data;
using Melodex.Core.Listening;
using Melodex.Core.Plugins;
using Melodex.Core.Tasks;

namespace Melodex.Core.Library;

/// <summary>
/// Songs' genres (#229). Most files have no genre tags, so the catalogue's are kept for each album and artist in the
/// library, and a song without its file's own has those: its album's, else its artist's.
/// </summary>
public static class SongGenres
{
    /// <summary>How many of the catalogue's genres an album or artist keeps, the most voted for.</summary>
    public const int Kept = 3;

    /// <summary>Genres kept one on a line, as files' and the catalogue's are.</summary>
    public static IReadOnlyList<string> Lines(string? genres) => (genres ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>A song's genres: its file's own, or where it has none, its album's or else its artist's from the catalogue.</summary>
    public static IReadOnlyList<string> Of(string? file, string? album, string? artist)
    {
        var own = Lines(file);
        if (own.Count > 0)
            return own;
        var catalogue = Lines(album) is { Count: > 0 } ofAlbum ? ofAlbum : Lines(artist);
        return catalogue.Select(Name).ToList();
    }

    /// <summary>The genres of several songs, the most common first.</summary>
    public static IReadOnlyList<string> Common(IEnumerable<IReadOnlyList<string>> songs) =>
        songs.SelectMany(s => s).GroupBy(g => g, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();

    /// <summary>Genres' words said as letters.</summary>
    public static readonly HashSet<string> Initials = ["edm", "idm", "ebm", "uk", "us", "r&b", "dnb"];

    // Short words that stay small inside a genre's name.
    private static readonly HashSet<string> Small = ["and", "of", "the", "n", "in", "a"];

    /// <summary>
    /// A catalogue genre as it's shown and written in tags: "trip hop" as "Trip Hop", "post-punk" as "Post-Punk", "drum and
    /// bass" as "Drum and Bass", and "UK garage" and "R&amp;B" as they're said.
    /// </summary>
    public static string Name(string genre)
    {
        var words = genre.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
            words[i] = Initials.Contains(words[i]) ? words[i].ToUpperInvariant()
                : i > 0 && Small.Contains(words[i]) ? words[i]
                : string.Join('-', words[i].Split('-').Select(Capital));
        return string.Join(' ', words);
    }

    private static string Capital(string part) => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..];
}

/// <summary>
/// Looks up the genres of the library's albums and artists in the catalogue (#229): the most voted for, up to three. An
/// artist the catalogue has none for takes the discovery plugins' (ListenBrainz's). Queued after a scan when any are new
/// or haven't been looked up for a while; each is saved as it's found, so a stopped task carries on where it left off.
/// </summary>
public sealed class GenresTask(MelodexDbContext db, CatalogueService catalogue, DiscoveryService discovery, ILogger<GenresTask> logger) : ITaskHandler
{
    public const string TaskKind = "library-genres";
    public const string Title = "Looking up genres";

    /// <summary>How long genres are kept before they're looked up again, as people go on voting.</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(90);

    // Busy or unreachable this many times in a row, the catalogue is left alone till the next scan.
    private const int BusyInARow = 3;

    public string Kind => TaskKind;

    /// <summary>Albums in the library whose genres haven't been looked up lately.</summary>
    public static IQueryable<Album> AlbumsWanting(MelodexDbContext db)
    {
        var stale = DateTime.UtcNow - KeptFor;
        return db.Albums.Where(a => (a.GenresCheckedAt == null || a.GenresCheckedAt < stale)
                                    && a.Editions.Any(e => db.LibraryFiles.Any(f => f.EditionId == e.Id && f.HiddenAt == null)));
    }

    /// <summary>Artists in the library whose genres haven't been looked up lately.</summary>
    public static IQueryable<Artist> ArtistsWanting(MelodexDbContext db)
    {
        var stale = DateTime.UtcNow - KeptFor;
        return db.Artists.Where(a => a.InLibrary && (a.GenresCheckedAt == null || a.GenresCheckedAt < stale));
    }

    /// <summary>Whether there's anything to look up, and anywhere to look.</summary>
    public static async Task<bool> WantedAsync(MelodexDbContext db, CatalogueService catalogue, CancellationToken ct) =>
        (await catalogue.IndexesAsync(ct)).Any(i => i.Plugin is IGenreLookup)
        && (await AlbumsWanting(db).AnyAsync(ct) || await ArtistsWanting(db).AnyAsync(ct));

    public async Task RunAsync(BackgroundTask task, IProgress<string> progress, CancellationToken ct)
    {
        var albums = await AlbumsWanting(db).OrderBy(a => a.Title).Select(a => a.Id).ToListAsync(ct);
        var artists = await ArtistsWanting(db).OrderBy(a => a.SortName ?? a.Name).Select(a => a.Id).ToListAsync(ct);
        var todo = albums.Select(id => (Kind: EntityKind.Album, Id: id)).Concat(artists.Select(id => (Kind: EntityKind.Artist, Id: id))).ToList();
        int done = 0, found = 0, busy = 0;
        foreach (var (kind, id) in todo)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report($"Looking up genres: {done + 1} of {todo.Count}");
            IReadOnlyList<string> genres;
            try
            {
                genres = await LookUpAsync(kind, id, ct);
                busy = 0;
            }
            catch (Exception e) when (e is IndexUnavailableException or HttpRequestException or RemotePluginException or TaskCanceledException
                                      && !ct.IsCancellationRequested)
            {
                logger.LogInformation("Couldn't look up genres for {Kind} {Id}: {Reason}", kind, id, e.Message);
                if (++busy < BusyInARow)
                    continue;
                progress.Report($"Found genres for {LibraryMatcher.Plural(found, "album or artist", "albums and artists")}; "
                                + $"the catalogue is busy, so the other {todo.Count - done} are looked up after the next scan");
                return;
            }
            var lines = string.Join('\n', genres);
            if (kind == EntityKind.Album)
                await db.Albums.Where(a => a.Id == id).ExecuteUpdateAsync(u => u.SetProperty(a => a.Genres, lines).SetProperty(a => a.GenresCheckedAt, DateTime.UtcNow), ct);
            else
                await db.Artists.Where(a => a.Id == id).ExecuteUpdateAsync(u => u.SetProperty(a => a.Genres, lines).SetProperty(a => a.GenresCheckedAt, DateTime.UtcNow), ct);
            done++;
            if (genres.Count > 0)
                found++;
        }
        progress.Report($"Found genres for {found} of {LibraryMatcher.Plural(done, "album or artist", "albums and artists")}");
    }

    // The catalogue's genres; for an artist it has none for, the discovery plugins'.
    private async Task<IReadOnlyList<string>> LookUpAsync(EntityKind kind, Guid id, CancellationToken ct)
    {
        var genres = (await catalogue.GenresAsync(kind, id, ct) ?? []).Select(g => g.Name).ToList();
        if (genres.Count == 0 && kind == EntityKind.Artist)
            genres = (await discovery.ArtistGenresAsync(id, ct)).Select(g => g.Name).ToList();
        return genres.Select(g => g.Trim().ToLowerInvariant()).Where(g => g.Length > 0 && !g.Contains('\n')).Distinct().Take(SongGenres.Kept).ToList();
    }
}
