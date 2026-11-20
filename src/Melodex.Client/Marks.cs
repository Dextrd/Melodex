namespace Melodex.Client;

/// <summary>What a star or rating is on.</summary>
public enum MarkKind
{
    /// <summary>A song, by its track ID: a file of your own goes by its file's.</summary>
    Song,
    Album,
    Artist,
}

/// <summary>A star or rating you've given a song, album or artist in your library (#7). Each person has their own.</summary>
/// <param name="StarredAt">When it was starred; null if it isn't.</param>
/// <param name="Rating">1 to 5; null if it isn't rated.</param>
public sealed record ItemMark(MarkKind Kind, Guid Id, DateTime? StarredAt, int? Rating);

/// <summary>Body of <c>PUT /marks/{kind}/{id}</c>: what to change, leaving out what stays as it is.</summary>
/// <param name="Starred">Star it, or take its star away.</param>
/// <param name="Rating">1 to 5, or 0 to take its rating away.</param>
public sealed record SetMark(bool? Starred = null, int? Rating = null);

/// <summary>A song in your play queue, with what a player shows of it, so the queue can be shown before anything loads.</summary>
/// <param name="TrackId">The song: its track ID, or a file of your own's file ID.</param>
/// <param name="FileId">A file of your own that no catalogue knows.</param>
/// <param name="EditionId">The edition it was queued from.</param>
/// <param name="Missing">Not in the library: it's fetched to listen to as the queue comes near it. Subsonic players don't see these.</param>
/// <param name="ListenId">Fetched to listen to: its listen-now track. Subsonic players don't see these either.</param>
public sealed record PlayQueueItem(
    Guid TrackId,
    string Title,
    string ArtistCredit,
    Guid? FileId = null,
    Guid? EditionId = null,
    Guid? ArtistId = null,
    Guid? AlbumId = null,
    string? AlbumTitle = null,
    string? CoverUrl = null,
    double? DurationSeconds = null,
    bool Missing = false,
    Guid? ListenId = null);

/// <summary>
/// Your play queue, kept on the server (#7): the web player and Subsonic players share it, so one picks up where the
/// other left off.
/// </summary>
/// <param name="Current">The place in <paramref name="Items"/> of the song playing, or -1.</param>
/// <param name="Position">How far into it, in seconds.</param>
/// <param name="Ordered">While shuffled, the order it was given in, as places in <paramref name="Items"/>, so shuffle can be turned off.</param>
/// <param name="ChangedBy">What saved it last: <c>web</c>, or a Subsonic player's name.</param>
public sealed record PlayQueue(
    IReadOnlyList<PlayQueueItem> Items,
    int Current,
    double Position,
    IReadOnlyList<int>? Ordered,
    DateTime ChangedAt,
    string ChangedBy);

/// <summary>Body of <c>PUT /player/queue</c>. An empty queue clears it.</summary>
/// <param name="Client">Who's saving it, shown as <see cref="PlayQueue.ChangedBy"/>; <c>web</c> if left out.</param>
public sealed record SavePlayQueue(
    IReadOnlyList<PlayQueueItem> Items,
    int Current,
    double Position,
    IReadOnlyList<int>? Ordered = null,
    string? Client = null);
