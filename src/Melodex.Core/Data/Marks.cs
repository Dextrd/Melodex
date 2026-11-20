using Melodex.Client;

namespace Melodex.Core.Data;

/// <summary>A star or rating one person has given a song, album or artist (#7). Gone once it has neither.</summary>
public class Mark
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public MarkKind Kind { get; set; }

    /// <summary>The song's track ID (a file of your own's file ID), or the album or artist.</summary>
    public Guid ItemId { get; set; }

    public DateTime? StarredAt { get; set; }

    /// <summary>1 to 5.</summary>
    public int? Rating { get; set; }

    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One person's play queue, shared by the web player and Subsonic players (#7).</summary>
public class SavedPlayQueue
{
    public Guid UserId { get; set; }

    /// <summary>The <see cref="PlayQueueItem"/>s, as JSON.</summary>
    public string ItemsJson { get; set; } = "[]";

    /// <summary>While shuffled, the order the queue was given in, as JSON places; null otherwise.</summary>
    public string? OrderedJson { get; set; }

    public int Current { get; set; } = -1;
    public double Position { get; set; }
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
    public string ChangedBy { get; set; } = "web";
}
