namespace Melodex.Client;

/// <summary>A song or album on your wishlist, as the catalogue has it.</summary>
/// <param name="Kind"><see cref="RequestKind.Track"/> or <see cref="RequestKind.Album"/>.</param>
/// <param name="TargetId">The track or album.</param>
/// <param name="EditionId">The edition it was wished from, if one was chosen.</param>
/// <param name="AlbumId">The album: the one wished for, or the one the song is on.</param>
public sealed record WishlistItem(
    Guid Id,
    RequestKind Kind,
    Guid TargetId,
    Guid? EditionId,
    string Title,
    string ArtistCredit,
    Guid ArtistId,
    Guid? AlbumId,
    string? AlbumTitle,
    string? CoverUrl,
    double? DurationSeconds,
    DateTime AddedAt);

/// <summary>Your wishlist, newest first, without what the library has now.</summary>
/// <param name="CanFetch">A retriever is on, so what's on it can be got or listened to now.</param>
public sealed record Wishlist(bool CanFetch, IReadOnlyList<WishlistItem> Items);

/// <summary>A song or album to put on your wishlist, from <paramref name="EditionId"/> if given.</summary>
public sealed record AddToWishlist(RequestKind Kind, Guid TargetId, Guid? EditionId = null);
