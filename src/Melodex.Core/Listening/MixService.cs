using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Melodex.Client;
using Melodex.Core.Data;
using Melodex.Core.Playlists;

namespace Melodex.Core.Listening;

/// <summary>
/// Mixes made for each person (#5), on the For you tab. Up to four daily mixes, each around a group of the artists they
/// play most, lately counting more: their songs and songs like them, mostly ones they have, with the server's radio
/// share of ones they don't. And a weekly one, made each Monday, of songs they don't have like what they've played
/// lately, and ListenBrainz's recommendations for them where their account is linked. Each is picked as a radio picks
/// its songs, so the same artist doesn't come too often, nor a song they skipped. They're made when first asked for
/// that day or week, replacing the last; Keep copies one into a playlist.
/// </summary>
public sealed class MixService(MelodexDbContext db, PlayService plays, DiscoveryService discovery, RadioService radio, PlaylistService playlists,
    IServiceScopeFactory scopes)
{
    public const int Dailies = 4, DailySongs = 25, WeeklySongs = 30;

    // How many of the artists they play most are grouped into daily mixes, and how many more can join each one's.
    private const int TopArtists = 12, Joining = 2;

    // How many songs they play most a daily mix, of its artists', and the weekly one start from.
    private const int SeedSongs = 5, WeeklySeeds = 8;

    // How many plugin questions are asked at once while making them.
    private const int AtOnce = 6;

    // So two requests at once don't both make a person's mixes.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Making = new();

    /// <summary>Their mixes, made first if they're from before today, or the weekly one from before this Monday.</summary>
    public async Task<IReadOnlyList<MixSummary>> ListAsync(Guid userId, CancellationToken ct)
    {
        await EnsureAsync(userId, DateTime.UtcNow, ct);
        var mixes = await db.Mixes.AsNoTracking().Where(m => m.UserId == userId).ToListAsync(ct);
        var summaries = new List<MixSummary>();
        foreach (var mix in mixes.OrderBy(m => m.Kind).ThenBy(m => m.Number))
            summaries.Add(Summary(mix, await discovery.SongsOfAsync(mix.Tracks.Take(12).ToList(), ct)));
        return summaries;
    }

    public async Task<MixDetail?> GetAsync(Guid userId, Guid id, CancellationToken ct)
    {
        if (await db.Mixes.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id && m.UserId == userId, ct) is not { } mix)
            return null;
        var songs = await discovery.SongsOfAsync(mix.Tracks, ct);
        return new MixDetail(Summary(mix, songs), songs);
    }

    /// <summary>
    /// A copy of the mix as one of their playlists, by its name and the day it was made unless they name it. Songs they
    /// don't have hold their place, as an import's do, until they're in the library.
    /// </summary>
    public async Task<PlaylistDetail?> KeepAsync(Guid userId, Guid id, KeepMix body, CancellationToken ct)
    {
        if (await db.Mixes.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id && m.UserId == userId, ct) is not { } mix)
            return null;
        var name = string.IsNullOrWhiteSpace(body.Name) ? $"{mix.Name}, {mix.MadeAt:d MMM yyyy}" : body.Name.Trim();
        var songs = (await discovery.SongsOfAsync(mix.Tracks, ct))
            .Select(s => new ImportedSong(s.TrackId, null, s.Title, s.ArtistCredit, s.AlbumTitle, s.DurationSeconds)).ToList();
        var (playlist, _) = (await playlists.ImportAsync(userId, null, name, songs, ct))!.Value;
        return await playlists.GetAsync(userId, playlist, ct);
    }

    /// <summary>Makes their daily mixes if none were made today, and the weekly one if it wasn't made since Monday.</summary>
    public async Task EnsureAsync(Guid userId, DateTime now, CancellationToken ct)
    {
        var gate = Making.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var today = now.Date;
            var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            var made = await db.Mixes.Where(m => m.UserId == userId).Select(m => new { m.Kind, m.MadeAt }).ToListAsync(ct);
            if (!made.Any(m => m.Kind == MixKind.Daily && m.MadeAt >= today))
                await ReplaceAsync(userId, MixKind.Daily, await DailiesAsync(userId, now, ct), ct);
            if (!made.Any(m => m.Kind == MixKind.Weekly && m.MadeAt >= monday))
                await ReplaceAsync(userId, MixKind.Weekly, await WeeklyAsync(userId, now, ct), ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The artists they play most, lately counting three times over, in groups: each led by the next not yet in one, with
    /// up to two others they play that are like them. A mix for each group, of their songs and songs like them.
    /// </summary>
    private async Task<List<Mix>> DailiesAsync(Guid userId, DateTime now, CancellationToken ct)
    {
        var lately = await plays.TopAsync(userId, TopKind.Artist, PlayPeriod.Month, 50, ct);
        var longer = await plays.TopAsync(userId, TopKind.Artist, PlayPeriod.Year, 50, ct);
        var top = lately.Select(t => (t, Weight: 3.0)).Concat(longer.Select(t => (t, Weight: 1.0)))
            .Where(x => x.t.ArtistId is not null)
            .GroupBy(x => x.t.ArtistId!.Value)
            .Select(g => (Id: g.Key, Name: g.First().t.Name, Score: g.Sum(x => x.t.Plays * x.Weight)))
            .OrderByDescending(a => a.Score).Take(TopArtists).ToList();
        // Asked all at once first, so each is answered from what's kept rather than waited on in turn.
        await WarmAsync(top.Take(Dailies * 2).Select(a => a.Id), (d, id, c) => d.SimilarArtistsAsync(id, 30, c), ct);
        var grouped = new HashSet<Guid>();
        var groups = new List<List<(Guid Id, string Name)>>();
        foreach (var head in top)
        {
            if (groups.Count == Dailies)
                break;
            if (!grouped.Add(head.Id))
                continue;
            var like = (await discovery.SimilarArtistsAsync(head.Id, 30, ct) ?? []).Select(a => a.Id).OfType<Guid>().ToHashSet();
            var group = new List<(Guid, string)> { (head.Id, head.Name) };
            foreach (var other in top.Where(a => !grouped.Contains(a.Id) && like.Contains(a.Id)).Take(Joining))
            {
                grouped.Add(other.Id);
                group.Add((other.Id, other.Name));
            }
            groups.Add(group);
        }

        var songs = await plays.TopAsync(userId, TopKind.Song, PlayPeriod.Year, 500, ct);
        var share = await radio.MissingShareAsync(ct);
        var seeded = groups.Select(group => (Group: group, Seeds: songs
            .Where(s => !s.Local && s.ArtistId is { } a && group.Any(g => g.Id == a)).Select(s => s.Id).Take(SeedSongs).ToList())).ToList();
        await WarmAsync(seeded.SelectMany(g => g.Seeds), (d, id, c) => d.SimilarSongsAsync(id, RadioService.Asked, c), ct);
        var mixes = new List<Mix>();
        foreach (var (group, seeds) in seeded)
        {
            var artists = group.Select(a => a.Id).ToList();
            if (seeds.Count == 0)
                continue;
            var name = $"Daily mix {mixes.Count + 1}";
            var picked = await radio.MixAsync(userId, name, seeds, artists, DailySongs, share, ct);
            if (picked.Count > 0)
                mixes.Add(new Mix { UserId = userId, Kind = MixKind.Daily, Number = mixes.Count + 1, Name = name, Artists = group.Select(a => a.Name).ToList(),
                    Tracks = picked.Select(s => s.TrackId).ToList(), MadeAt = now });
        }
        return mixes;
    }

    /// <summary>
    /// Songs they don't have, like those they've played most lately, or this year when they've played little lately, with
    /// ListenBrainz's recommendations for them among them.
    /// </summary>
    private async Task<List<Mix>> WeeklyAsync(Guid userId, DateTime now, CancellationToken ct)
    {
        var top = await plays.TopAsync(userId, TopKind.Song, PlayPeriod.Month, WeeklySeeds, ct);
        if (top.Count < WeeklySeeds)
            top = await plays.TopAsync(userId, TopKind.Song, PlayPeriod.Year, WeeklySeeds, ct);
        var seeds = top.Where(t => !t.Local).Select(t => t.Id).ToList();
        if (seeds.Count == 0)
            return [];
        var artists = top.Select(t => t.ArtistId).OfType<Guid>().Distinct().Take(3).ToList();
        await WarmAsync(seeds, (d, id, c) => d.SimilarSongsAsync(id, RadioService.Asked, c), ct);
        const string name = "Weekly discoveries";
        var like = (await radio.MixAsync(userId, name, seeds, artists, WeeklySongs, 1, ct)).Where(s => !s.Owned).ToList();
        var recommended = (await discovery.RecommendAsync(userId, WeeklySongs, ct)).Where(s => !s.Owned).ToList();
        // One recommendation in three, while there are any.
        var songs = new List<DiscoveredSong>();
        int l = 0, r = 0;
        while (songs.Count < WeeklySongs && (l < like.Count || r < recommended.Count))
        {
            var next = songs.Count % 3 == 2 && r < recommended.Count || l >= like.Count ? recommended[r++] : like[l++];
            if (songs.All(s => s.TrackId != next.TrackId))
                songs.Add(next);
        }
        if (songs.Count == 0)
            return [];
        var heard = songs.GroupBy(s => s.ArtistCredit).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(3).ToList();
        return [new Mix { UserId = userId, Kind = MixKind.Weekly, Number = 1, Name = name, Artists = heard, Tracks = songs.Select(s => s.TrackId).ToList(), MadeAt = now }];
    }

    // Asks the plugins about each at once, a few at a time, each in a scope of its own: their answers are kept a while,
    // so making the mixes after finds them there.
    private async Task WarmAsync(IEnumerable<Guid> ids, Func<DiscoveryService, Guid, CancellationToken, Task> ask, CancellationToken ct) =>
        await Parallel.ForEachAsync(ids.Distinct(), new ParallelOptions { MaxDegreeOfParallelism = AtOnce, CancellationToken = ct }, async (id, c) =>
        {
            await using var scope = scopes.CreateAsyncScope();
            await ask(scope.ServiceProvider.GetRequiredService<DiscoveryService>(), id, c);
        });

    private async Task ReplaceAsync(Guid userId, MixKind kind, List<Mix> mixes, CancellationToken ct)
    {
        await db.Mixes.Where(m => m.UserId == userId && m.Kind == kind).ExecuteDeleteAsync(ct);
        db.Mixes.AddRange(mixes);
        await db.SaveChangesAsync(ct);
    }

    private static MixSummary Summary(Mix mix, IReadOnlyList<DiscoveredSong> songs) => new(mix.Id, mix.Kind, mix.Name, mix.Artists,
        songs.Select(s => s.CoverUrl).OfType<string>().Distinct().Take(4).ToList(), mix.Tracks.Count, DateTime.SpecifyKind(mix.MadeAt, DateTimeKind.Utc));
}

/// <summary>
/// Makes the mixes of everyone who's played something lately ahead of time, each hour, and asks about their artists and
/// albums for you, so the For you tab doesn't wait on the plugins the first time it's opened each day. Off in tests, which make them when asked.
/// </summary>
public sealed class MixMaker(IServiceScopeFactory scopes, IOptions<MelodexOptions> options, ILogger<MixMaker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    // Whose mixes are made ahead: those who've played something in this long.
    private static readonly TimeSpan Lately = TimeSpan.FromDays(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.ScheduledTasks)
            return;
        // Not while the server's starting up.
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                List<Guid> users;
                using (var scope = scopes.CreateScope())
                {
                    var since = DateTime.UtcNow - Lately;
                    users = await scope.ServiceProvider.GetRequiredService<MelodexDbContext>().Plays.Where(p => p.PlayedAt >= since)
                        .Select(p => p.UserId).Distinct().ToListAsync(stoppingToken);
                }
                foreach (var user in users)
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<MixService>().EnsureAsync(user, DateTime.UtcNow, stoppingToken);
                    // And the tab's artists and albums asked about, so the plugins' answers are kept for it.
                    var forYou = scope.ServiceProvider.GetRequiredService<ForYouService>();
                    await forYou.ArtistsAsync(user, stoppingToken);
                    await forYou.AlbumsAsync(user, stoppingToken);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning("Couldn't make mixes: {Message}", e.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
