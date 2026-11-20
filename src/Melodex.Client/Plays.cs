namespace Melodex.Client;

/// <summary>What a song was played from.</summary>
public enum PlaySourceKind { Album, Playlist, Artist, Radio, Other }

/// <summary>Where a play came from: an album, a playlist, a radio. The name is kept as it was, in case the thing goes.</summary>
public sealed record PlaySource(PlaySourceKind Kind, Guid? Id = null, string? Name = null);

/// <summary>A song someone played (#2), as it was when they played it.</summary>
/// <param name="TrackId">The song: its track ID, or a file of your own's file ID.</param>
/// <param name="FileId">The file played, when it was one in the library.</param>
/// <param name="Client">What played it: <c>web</c>, or a Subsonic player's name.</param>
public sealed record PlayRecord(
    Guid Id,
    Guid TrackId,
    Guid? FileId,
    string Title,
    string ArtistCredit,
    Guid? ArtistId,
    Guid? AlbumId,
    string? AlbumTitle,
    string? CoverUrl,
    double? DurationSeconds,
    DateTime PlayedAt,
    PlaySource? Source,
    string Client)
{
    /// <summary>A file of your own no catalogue knows: its pages are the own-music ones.</summary>
    public bool Local => FileId is { } file && file == TrackId;
}

/// <summary>
/// Body of <c>POST /plays</c>: a song played for half its length or four minutes, whichever came first. A song in the
/// library needs only its IDs, and the rest is filled in; any other needs its title and artist.
/// </summary>
/// <param name="PlayedAt">When it started; now if left out.</param>
/// <param name="Client">What played it, as history shows it; <c>web</c> if left out.</param>
public sealed record RecordPlay(
    Guid TrackId,
    Guid? FileId = null,
    string? Title = null,
    string? ArtistCredit = null,
    Guid? ArtistId = null,
    Guid? AlbumId = null,
    string? AlbumTitle = null,
    string? CoverUrl = null,
    double? DurationSeconds = null,
    DateTime? PlayedAt = null,
    PlaySource? Source = null,
    string? Client = null);

/// <summary>A page of someone's plays, the latest first.</summary>
public sealed record PlayPage(IReadOnlyList<PlayRecord> Items, int Total);

/// <summary>How far back most played looks.</summary>
public enum PlayPeriod { Week, Month, Year, All }

/// <summary>What most played lists.</summary>
public enum TopKind { Song, Album, Artist }

/// <summary>A song, album or artist among someone's most played.</summary>
/// <param name="Id">The track (or own file), album or artist; for an artist no catalogue knows, empty.</param>
/// <param name="ArtistCredit">For a song or album, its artist.</param>
/// <param name="Local">Of your own files, which no catalogue knows: its pages are the own-music ones.</param>
public sealed record TopItem(
    Guid Id,
    string Name,
    string? ArtistCredit,
    Guid? ArtistId,
    Guid? AlbumId,
    string? CoverUrl,
    int Plays,
    DateTime LastPlayedAt,
    bool Local);

/// <summary>A service plays are sent on to (#2).</summary>
public enum ScrobbleService { ListenBrainz, LastFm }

/// <summary>Someone's account on a scrobbling service, linked so their plays are sent there.</summary>
/// <param name="Error">Why sending last failed, if it did; once it's because the link no longer works, nothing's sent until it's linked again.</param>
/// <param name="Waiting">Plays not sent yet, tried again later.</param>
public sealed record ScrobbleLink(ScrobbleService Service, string Username, DateTime LinkedAt, DateTime? LastSentAt, string? Error, int Waiting);

/// <summary>Your linked scrobbling accounts, and whether Last.fm can be linked here.</summary>
/// <param name="LastFmApiKey">The admin's Last.fm API key, which linking a Last.fm account asks Last.fm with; null if there's none yet.</param>
public sealed record ScrobbleStatus(IReadOnlyList<ScrobbleLink> Links, string? LastFmApiKey);

/// <summary>Body of <c>PUT /account/scrobbling/listenbrainz</c>: the user token from your ListenBrainz settings.</summary>
public sealed record LinkListenBrainz(string Token);

/// <summary>Body of <c>PUT /account/scrobbling/lastfm</c>: the token Last.fm gave back after you allowed Melodex.</summary>
public sealed record LinkLastFm(string Token);

/// <summary>What the admin set for scrobbling: the Last.fm API account people's plays are sent with.</summary>
/// <param name="HasLastFmSecret">Whether the shared secret is set; it's never shown again.</param>
public sealed record ScrobbleSettingsInfo(string? LastFmApiKey, bool HasLastFmSecret);

/// <summary>Body of <c>PUT /settings/scrobbling</c>.</summary>
/// <param name="LastFmSecret">The shared secret; null keeps the one set, empty takes it away.</param>
public sealed record SaveScrobbleSettings(string? LastFmApiKey, string? LastFmSecret = null);

/// <summary>Someone playing a song now, as Subsonic players' now playing shows.</summary>
public sealed record NowPlayingInfo(string Username, PlayRecord Play, DateTime StartedAt);
