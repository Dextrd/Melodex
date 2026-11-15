using Melodex.Client;

namespace Melodex.Core.Data;

/// <summary>
/// A song or album one user would like but hasn't got: for later, when nothing can fetch it now or they'd rather wait.
/// It goes by itself once the library has it.
/// </summary>
public class WishlistEntry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }

    /// <summary><see cref="RequestKind.Track"/> or <see cref="RequestKind.Album"/>.</summary>
    public RequestKind Kind { get; set; }

    /// <summary>The track or album.</summary>
    public Guid TargetId { get; set; }

    /// <summary>The edition it was wished from, if one was chosen: where a song is shown from, or which of an album's to get.</summary>
    public Guid? EditionId { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
