using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Core.Data;

namespace Melodex.Core.Listening;

/// <summary>
/// Each person's stars and ratings (#7), as the web app and Subsonic players set them, and the play queue they share.
/// </summary>
public sealed class MarkService(MelodexDbContext db)
{
    public async Task<IReadOnlyList<ItemMark>> ListAsync(Guid user, CancellationToken ct) =>
        (await db.Marks.AsNoTracking().Where(m => m.UserId == user).ToListAsync(ct))
        .OrderByDescending(m => m.StarredAt ?? m.ChangedAt).Select(Info).ToList();

    /// <summary>Stars, unstars or rates something; what's left of its mark, or null once it has neither.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A rating that isn't 0 to 5.</exception>
    public async Task<ItemMark?> SetAsync(Guid user, MarkKind kind, Guid id, SetMark change, CancellationToken ct)
    {
        if (change.Rating is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(change), "A rating is 1 to 5, or 0 to take it away.");
        var mark = await db.Marks.FirstOrDefaultAsync(m => m.UserId == user && m.Kind == kind && m.ItemId == id, ct);
        if (mark is null)
        {
            mark = new Mark { UserId = user, Kind = kind, ItemId = id };
            db.Marks.Add(mark);
        }
        if (change.Starred is { } starred)
            mark.StarredAt = starred ? mark.StarredAt ?? DateTime.UtcNow : null;
        if (change.Rating is { } rating)
            mark.Rating = rating == 0 ? null : rating;
        mark.ChangedAt = DateTime.UtcNow;
        var gone = mark.StarredAt is null && mark.Rating is null;
        if (gone)
        {
            if (db.Entry(mark).State == EntityState.Added)
                db.Entry(mark).State = EntityState.Detached;
            else
                db.Marks.Remove(mark);
        }
        await db.SaveChangesAsync(ct);
        return gone ? null : Info(mark);
    }

    private static ItemMark Info(Mark mark) => new(mark.Kind, mark.ItemId, mark.StarredAt, mark.Rating);

    // ---- The play queue ----

    /// <summary>The queue this person left, or null if they have none.</summary>
    public async Task<PlayQueue?> GetQueueAsync(Guid user, CancellationToken ct)
    {
        var saved = await db.PlayQueues.AsNoTracking().FirstOrDefaultAsync(q => q.UserId == user, ct);
        if (saved is null)
            return null;
        var items = JsonSerializer.Deserialize<List<PlayQueueItem>>(saved.ItemsJson, MelodexJson.Options) ?? [];
        var ordered = saved.OrderedJson is null ? null : JsonSerializer.Deserialize<List<int>>(saved.OrderedJson, MelodexJson.Options);
        return new PlayQueue(items, saved.Current, saved.Position, ordered, saved.ChangedAt, saved.ChangedBy);
    }

    /// <summary>Keeps this person's queue in place of the one before; an empty one clears it.</summary>
    /// <exception cref="ArgumentException">More than <see cref="MaxQueue"/> songs, or a current place outside the queue.</exception>
    public async Task<PlayQueue?> SaveQueueAsync(Guid user, SavePlayQueue queue, CancellationToken ct)
    {
        if (queue.Items.Count > MaxQueue)
            throw new ArgumentException($"A queue holds at most {MaxQueue} songs.", nameof(queue));
        if (queue.Items.Count > 0 && (queue.Current < -1 || queue.Current >= queue.Items.Count))
            throw new ArgumentException("The current song has to be one in the queue, or -1 for none.", nameof(queue));
        var saved = await db.PlayQueues.FirstOrDefaultAsync(q => q.UserId == user, ct);
        if (queue.Items.Count == 0)
        {
            if (saved is not null)
            {
                db.PlayQueues.Remove(saved);
                await db.SaveChangesAsync(ct);
            }
            return null;
        }
        if (saved is null)
        {
            saved = new SavedPlayQueue { UserId = user };
            db.PlayQueues.Add(saved);
        }
        saved.ItemsJson = JsonSerializer.Serialize(queue.Items, MelodexJson.Options);
        saved.OrderedJson = queue.Ordered is { Count: > 0 } ordered && ordered.All(i => i >= 0 && i < queue.Items.Count)
            ? JsonSerializer.Serialize(ordered, MelodexJson.Options) : null;
        saved.Current = queue.Current;
        saved.Position = Math.Max(queue.Position, 0);
        saved.ChangedAt = DateTime.UtcNow;
        saved.ChangedBy = string.IsNullOrWhiteSpace(queue.Client) ? "web" : queue.Client.Trim()[..Math.Min(queue.Client.Trim().Length, 100)];
        await db.SaveChangesAsync(ct);
        return await GetQueueAsync(user, ct);
    }

    /// <summary>The most songs a queue keeps.</summary>
    public const int MaxQueue = 5000;
}
