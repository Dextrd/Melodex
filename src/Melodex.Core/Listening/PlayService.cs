using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Core.Data;
using Melodex.Core.Subsonic;

namespace Melodex.Core.Listening;

/// <summary>
/// Each person's listening history (#2): the songs they played, in the web player or a Subsonic player, kept as they
/// were then, and sent on to the scrobbling accounts they've linked.
/// </summary>
public sealed class PlayService(MelodexDbContext db, SubsonicLibrary library, Scrobbler scrobbler)
{
    /// <summary>The same song again this soon after is the same play told twice, as some players do.</summary>
    public static readonly TimeSpan Repeat = TimeSpan.FromSeconds(30);

    /// <summary>Records a play; the one already recorded if this is the same play again.</summary>
    /// <exception cref="ArgumentException">A song not in the library without its title and artist, or a time to come.</exception>
    public async Task<PlayRecord> RecordAsync(Guid user, RecordPlay body, CancellationToken ct)
    {
        var play = await ToPlayAsync(user, body, ct);
        var near = await db.Plays.AsNoTracking()
            .Where(p => p.UserId == user && p.TrackId == play.TrackId && p.PlayedAt > play.PlayedAt - Repeat && p.PlayedAt < play.PlayedAt + Repeat)
            .FirstOrDefaultAsync(ct);
        if (near is not null)
            return Info(near);
        db.Plays.Add(play);
        foreach (var account in await db.ScrobbleAccounts.Where(a => a.UserId == user).ToListAsync(ct))
            db.ScrobbleSends.Add(new ScrobbleSend { Play = play, PlayId = play.Id, AccountId = account.Id });
        await db.SaveChangesAsync(ct);
        scrobbler.Wake();
        return Info(play);
    }

    /// <summary>What a play would be, filled in from the library where the song's there; not saved.</summary>
    /// <exception cref="ArgumentException">A song not in the library without its title and artist, or a time to come.</exception>
    public async Task<Play> ToPlayAsync(Guid user, RecordPlay body, CancellationToken ct)
    {
        var at = body.PlayedAt is { } given ? DateTime.SpecifyKind(given.ToUniversalTime(), DateTimeKind.Utc) : DateTime.UtcNow;
        if (at > DateTime.UtcNow.AddMinutes(5))
            throw new ArgumentException("A play can't be in the future.", nameof(body));
        var view = await library.GetAsync(ct);
        var song = body.FileId is { } file && view.SongsById.TryGetValue(file, out var byFile) ? byFile
            : view.SongsById.TryGetValue(body.TrackId, out var own) ? own
            : view.Songs.FirstOrDefault(s => s.Track.TrackId == body.TrackId);
        var client = string.IsNullOrWhiteSpace(body.Client) ? "web" : body.Client.Trim()[..Math.Min(body.Client.Trim().Length, 100)];
        var play = new Play
        {
            UserId = user,
            TrackId = song?.Track.TrackId ?? body.TrackId,
            FileId = song?.Id ?? body.FileId,
            Title = song?.Track.Title ?? (string.IsNullOrWhiteSpace(body.Title)
                ? throw new ArgumentException("A song that isn't in the library needs its title and artist.", nameof(body)) : body.Title.Trim()),
            ArtistCredit = song?.Track.ArtistCredit ?? (string.IsNullOrWhiteSpace(body.ArtistCredit)
                ? throw new ArgumentException("A song that isn't in the library needs its title and artist.", nameof(body)) : body.ArtistCredit.Trim()),
            ArtistId = song is null ? body.ArtistId : song.Track.ArtistId,
            AlbumId = song is null ? body.AlbumId : song.Track.AlbumId,
            AlbumTitle = song is null ? body.AlbumTitle : song.Track.AlbumTitle,
            CoverUrl = song is null ? body.CoverUrl : song.Track.CoverUrl,
            DurationSeconds = song?.Track.DurationSeconds ?? body.DurationSeconds,
            RecordingMbid = song?.MusicBrainzId,
            ReleaseMbid = song is not null && view.AlbumOfSong.TryGetValue(song.Id, out var album) ? album.MusicBrainzId : null,
            PlayedAt = at,
            SourceKind = body.Source?.Kind,
            SourceId = body.Source?.Id,
            SourceName = body.Source?.Name,
            Client = client,
        };
        return play;
    }

    /// <summary>Someone's plays, the latest first.</summary>
    public async Task<PlayPage> ListAsync(Guid user, int skip, int take, CancellationToken ct)
    {
        var plays = db.Plays.AsNoTracking().Where(p => p.UserId == user);
        var total = await plays.CountAsync(ct);
        var page = await plays.OrderByDescending(p => p.PlayedAt).Skip(Math.Max(skip, 0)).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        return new PlayPage(page.Select(Info).ToList(), total);
    }

    /// <summary>Takes a play out of someone's history; false if it isn't theirs. One already sent on stays where it was sent.</summary>
    public async Task<bool> DeleteAsync(Guid user, Guid id, CancellationToken ct) =>
        await db.Plays.Where(p => p.Id == id && p.UserId == user).ExecuteDeleteAsync(ct) > 0;

    /// <summary>When a period starts, counting back from now.</summary>
    public static DateTime? Since(PlayPeriod period) => period switch
    {
        PlayPeriod.Week => DateTime.UtcNow.AddDays(-7),
        PlayPeriod.Month => DateTime.UtcNow.AddMonths(-1),
        PlayPeriod.Year => DateTime.UtcNow.AddYears(-1),
        _ => null,
    };

    /// <summary>Someone's most played songs, albums or artists over a period, the most first.</summary>
    public async Task<IReadOnlyList<TopItem>> TopAsync(Guid user, TopKind kind, PlayPeriod period, int take, CancellationToken ct)
    {
        var since = Since(period);
        var plays = await db.Plays.AsNoTracking().Where(p => p.UserId == user && (since == null || p.PlayedAt >= since))
            .OrderByDescending(p => p.PlayedAt).ToListAsync(ct);
        IEnumerable<IGrouping<object, Play>> groups = kind switch
        {
            TopKind.Song => plays.GroupBy(p => (object)p.TrackId),
            TopKind.Album => plays.Where(p => p.AlbumId is not null).GroupBy(p => (object)p.AlbumId!.Value),
            // An artist no catalogue knows goes by name.
            _ => plays.GroupBy(p => p.ArtistId is { } id ? id : (object)p.ArtistCredit.ToLowerInvariant()),
        };
        return groups.Select(g => (Plays: g.ToList(), Latest: g.First()))
            .OrderByDescending(g => g.Plays.Count).ThenByDescending(g => g.Latest.PlayedAt)
            .Take(Math.Clamp(take, 1, 500))
            .Select(g => kind switch
            {
                TopKind.Song => new TopItem(g.Latest.TrackId, g.Latest.Title, g.Latest.ArtistCredit, g.Latest.ArtistId, g.Latest.AlbumId, g.Latest.CoverUrl,
                    g.Plays.Count, g.Latest.PlayedAt, Info(g.Latest).Local),
                TopKind.Album => new TopItem(g.Latest.AlbumId!.Value, g.Latest.AlbumTitle ?? "", g.Latest.ArtistCredit, g.Latest.ArtistId, g.Latest.AlbumId,
                    g.Latest.CoverUrl, g.Plays.Count, g.Latest.PlayedAt, Info(g.Latest).Local),
                _ => new TopItem(g.Latest.ArtistId ?? Guid.Empty, g.Latest.ArtistCredit, null, g.Latest.ArtistId, null, null,
                    g.Plays.Count, g.Latest.PlayedAt, Info(g.Latest).Local),
            })
            .ToList();
    }

    /// <summary>How often and when last someone played each song (by track) and album, for Subsonic players.</summary>
    public async Task<PlayCounts> CountsAsync(Guid user, CancellationToken ct)
    {
        var songs = await db.Plays.AsNoTracking().Where(p => p.UserId == user).GroupBy(p => p.TrackId)
            .Select(g => new { g.Key, Count = g.Count(), Last = g.Max(p => p.PlayedAt) }).ToListAsync(ct);
        var albums = await db.Plays.AsNoTracking().Where(p => p.UserId == user && p.AlbumId != null).GroupBy(p => p.AlbumId!.Value)
            .Select(g => new { g.Key, Count = g.Count(), Last = g.Max(p => p.PlayedAt) }).ToListAsync(ct);
        return new PlayCounts(songs.ToDictionary(s => s.Key, s => (s.Count, DateTime.SpecifyKind(s.Last, DateTimeKind.Utc))),
            albums.ToDictionary(a => a.Key, a => (a.Count, DateTime.SpecifyKind(a.Last, DateTimeKind.Utc))));
    }

    public static PlayRecord Info(Play p) => new(
        p.Id, p.TrackId, p.FileId, p.Title, p.ArtistCredit, p.ArtistId, p.AlbumId, p.AlbumTitle, p.CoverUrl, p.DurationSeconds,
        DateTime.SpecifyKind(p.PlayedAt, DateTimeKind.Utc), p.SourceKind is { } kind ? new PlaySource(kind, p.SourceId, p.SourceName) : null, p.Client);
}

/// <summary>How often and when last someone played each song, by track, and each album.</summary>
public sealed record PlayCounts(IReadOnlyDictionary<Guid, (int Count, DateTime Last)> Songs, IReadOnlyDictionary<Guid, (int Count, DateTime Last)> Albums)
{
    public bool Any => Songs.Count > 0;
}
