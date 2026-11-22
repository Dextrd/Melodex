using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Core.Data;
using Melodex.Core.Library;

namespace Melodex.Core.Listening;

/// <summary>
/// The For you tab's suggestions (#5), from what each person plays: artists like those they play most that they have
/// nothing of, albums like their most played that they don't have, and albums of their own they haven't played in a
/// while. Its mixes are <see cref="MixService"/>'s.
/// </summary>
public sealed class ForYouService(MelodexDbContext db, PlayService plays, LibraryView library, IServiceScopeFactory scopes)
{
    public const int Take = 12;

    /// <summary>How long since an album of their own was last played for it to be one to go back to.</summary>
    public static readonly TimeSpan AWhile = TimeSpan.FromDays(60);

    // How many of their most played artists and albums to look for others like.
    private const int FromArtists = 5, FromAlbums = 4;

    /// <summary>Artists like those they play most, closest first, who aren't in the library.</summary>
    public async Task<IReadOnlyList<SimilarArtist>> ArtistsAsync(Guid userId, CancellationToken ct)
    {
        var top = (await plays.TopAsync(userId, TopKind.Artist, PlayPeriod.Year, 20, ct)).Select(t => t.ArtistId).OfType<Guid>().ToList();
        var answers = await Task.WhenAll(top.Take(FromArtists).Select(id => InScopeAsync(d => d.SimilarArtistsAsync(id, 20, ct))));
        var found = answers.SelectMany(a => a ?? [])
            .Where(a => !a.InLibrary && (a.Id is not { } id || !top.Contains(id)))
            .GroupBy(a => a.Id?.ToString() ?? a.Name.ToLowerInvariant())
            .Select(g => g.First() with { Score = g.Sum(a => a.Score), From = g.SelectMany(a => a.From).Distinct().ToList() })
            .OrderByDescending(a => a.Score).Take(Take)
            .ToList();
        return found.Select(a => a with { Score = a.Score / found[0].Score }).ToList();
    }

    /// <summary>Albums like those they play most, closest first, that they don't have.</summary>
    public async Task<IReadOnlyList<SimilarAlbum>> AlbumsAsync(Guid userId, CancellationToken ct)
    {
        var top = (await plays.TopAsync(userId, TopKind.Album, PlayPeriod.Year, 20, ct)).Where(t => !t.Local).Select(t => t.Id).ToList();
        var answers = await Task.WhenAll(top.Take(FromAlbums).Select(id => InScopeAsync(d => d.SimilarAlbumsAsync(id, 20, ct))));
        var scored = answers.SelectMany(a => a ?? []).Where(a => !top.Contains(a.AlbumId))
            .GroupBy(a => a.AlbumId)
            .Select(g => (AlbumId: g.Key, Score: g.Sum(a => a.Score), From: (IReadOnlyList<string>)g.SelectMany(a => a.From).Distinct().ToList()))
            .OrderByDescending(a => a.Score).ToList();
        var summaries = (await library.GetAlbumSummariesAsync(scored.Select(s => s.AlbumId).ToList(), ct)).ToDictionary(a => a.Id);
        var found = scored.Where(s => summaries.TryGetValue(s.AlbumId, out var a) && a.Status == AlbumStatus.Missing && !a.Ignored).Take(Take).ToList();
        return found.Select(s => new SimilarAlbum(summaries[s.AlbumId], s.Score / found[0].Score, s.From)).ToList();
    }

    /// <summary>
    /// Albums of their own they played before but not in <see cref="AWhile"/>, the most played first; and, while that's
    /// few, ones they have but never played, at random.
    /// </summary>
    public async Task<IReadOnlyList<AlbumSummary>> YourAlbumsAsync(Guid userId, CancellationToken ct)
    {
        var owned = db.LibraryFiles.Where(f => f.EditionId != null).Select(f => f.Edition!.AlbumId).Distinct();
        var played = await db.Plays.AsNoTracking().Where(p => p.UserId == userId && p.AlbumId != null && owned.Contains(p.AlbumId.Value))
            .GroupBy(p => p.AlbumId!.Value).Select(g => new { Id = g.Key, Count = g.Count(), Last = g.Max(p => p.PlayedAt) }).ToListAsync(ct);
        var since = DateTime.UtcNow - AWhile;
        var ids = played.Where(p => p.Last < since).OrderByDescending(p => p.Count).Select(p => p.Id).Take(Take).ToList();
        if (ids.Count < Take / 2)
        {
            var heard = played.Select(p => p.Id).ToList();
            ids.AddRange(await owned.Where(id => !heard.Contains(id)).OrderBy(_ => EF.Functions.Random()).Take(Take - ids.Count).ToListAsync(ct));
        }
        return (await library.GetAlbumSummariesAsync(ids, ct)).Where(a => !a.Ignored).ToList();
    }

    // Each asked in a scope of its own, at once, so the plugins are waited on once rather than for each in turn.
    private async Task<TResult> InScopeAsync<TResult>(Func<DiscoveryService, Task<TResult>> ask)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await ask(scope.ServiceProvider.GetRequiredService<DiscoveryService>());
    }
}
