namespace Melodex.Client;

/// <summary>What a song was played from.</summary>
public enum PlaySourceKind { Album, Playlist, Artist, Radio, Other, Mix }

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

/// <summary>How an account is linked with a scrobbling plugin (#236).</summary>
public enum ScrobbleLinkKind
{
    /// <summary>With a token pasted from the service, such as a ListenBrainz user token.</summary>
    Token,

    /// <summary>On the service's own page, which sends you back with one: ask for it with <c>POST .../link-page</c>.</summary>
    Web,
}

/// <summary>A scrobbling plugin the admin set up, which plays can be sent on to, and your account on it if you linked one.</summary>
/// <param name="Id">The plugin's set-up ID, which linking takes.</param>
/// <param name="Homepage">The service's website.</param>
/// <param name="Description">What it is, from the plugin.</param>
/// <param name="Linked">Your account there; null if you haven't linked one.</param>
public sealed record ScrobbleService(Guid Id, string Name, string PluginId, ScrobbleLinkKind Link, string? Homepage, string? Description, ScrobbleLink? Linked);

/// <summary>Someone's account on a scrobbling service, linked so their plays are sent there.</summary>
/// <param name="Error">Why sending last failed, if it did; once it's because the link no longer works, nothing's sent until it's linked again.</param>
/// <param name="Waiting">Plays not sent yet, tried again later.</param>
public sealed record ScrobbleLink(string Username, DateTime LinkedAt, DateTime? LastSentAt, string? Error, int Waiting);

/// <summary>Body of <c>PUT /account/scrobbling/{id}</c>: the token you pasted, or the one the service sent you back with.</summary>
public sealed record LinkScrobbling(string Token);

/// <summary>Body of <c>POST /account/scrobbling/{id}/link-page</c>: where the service should send you back to, with a token.</summary>
public sealed record ScrobbleLinkPageRequest(string Callback);

/// <summary>Where to go to allow Melodex on the service.</summary>
public sealed record ScrobbleLinkPage(string Url);

/// <summary>Someone playing a song now, as Subsonic players' now playing shows.</summary>
public sealed record NowPlayingInfo(string Username, PlayRecord Play, DateTime StartedAt);
