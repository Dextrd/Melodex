using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Plugins;

namespace Melodex.Core.Wishlists;

/// <summary>
/// Each user's wishlist: songs and albums they'd like, kept for when something can fetch them. A song leaves it once
/// the library has it, and an album once the library has all of it.
/// </summary>
public sealed class WishlistService(MelodexDbContext db, LibraryView library, RetrievalPlugins retrieval)
{
    public async Task<Wishlist> GetAsync(Guid userId, CancellationToken ct)
    {
        var entries = await db.WishlistEntries.Where(w => w.UserId == userId).OrderByDescending(w => w.AddedAt).ThenByDescending(w => w.Id).ToListAsync(ct);
        var items = new List<(WishlistEntry Entry, WishlistItem? Item)>();

        var trackIds = entries.Where(e => e.Kind == RequestKind.Track).Select(e => e.TargetId).ToList();
        var owned = (await db.LibraryFiles.Where(f => f.TrackId != null && trackIds.Contains(f.TrackId.Value)).Select(f => f.TrackId!.Value).ToListAsync(ct)).ToHashSet();
        var tracks = await db.Tracks.Where(t => trackIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var appearances = (await db.EditionTracks.Where(et => trackIds.Contains(et.TrackId))
                .Select(et => new { et.TrackId, et.EditionId, et.Title, et.Duration, et.Edition.AlbumId, Album = et.Edition.Album.Title })
                .ToListAsync(ct))
            .ToLookup(a => a.TrackId);

        var albumIds = entries.Where(e => e.Kind == RequestKind.Album).Select(e => e.TargetId).ToList();
        var albums = (await library.GetAlbumSummariesAsync(albumIds, ct)).ToDictionary(a => a.Id);

        var gone = new List<WishlistEntry>();
        var result = new List<WishlistItem>();
        foreach (var entry in entries)
        {
            if (entry.Kind == RequestKind.Album)
            {
                if (!albums.TryGetValue(entry.TargetId, out var album) || album.Status == AlbumStatus.Complete)
                {
                    gone.Add(entry);
                    continue;
                }
                result.Add(new WishlistItem(entry.Id, entry.Kind, entry.TargetId, entry.EditionId, album.Title, album.ArtistCredit, album.ArtistId,
                    album.Id, album.Title, LibraryView.CoverUrl(album.Id, entry.EditionId), null, entry.AddedAt));
            }
            else
            {
                if (owned.Contains(entry.TargetId) || !tracks.TryGetValue(entry.TargetId, out var track))
                {
                    gone.Add(entry);
                    continue;
                }
                // Shown as it is on the edition it was wished from, or else on any it's on.
                var on = appearances[track.Id].OrderByDescending(a => a.EditionId == entry.EditionId).FirstOrDefault();
                result.Add(new WishlistItem(entry.Id, entry.Kind, entry.TargetId, entry.EditionId, on?.Title ?? track.Title, track.ArtistCredit, track.ArtistId,
                    on?.AlbumId, on?.Album, on is null ? null : LibraryView.CoverUrl(on.AlbumId, on.EditionId),
                    (on?.Duration ?? track.Duration)?.TotalSeconds, entry.AddedAt));
            }
        }
        if (gone.Count > 0)
        {
            db.WishlistEntries.RemoveRange(gone);
            await db.SaveChangesAsync(ct);
        }
        return new Wishlist(await CanFetchAsync(ct), result);
    }

    /// <summary>Whether a retriever is on, so what's wished for can be got now.</summary>
    public async Task<bool> CanFetchAsync(CancellationToken ct) => (await retrieval.GetChainAsync(ct)).Count > 0;

    /// <summary>Puts it on the wishlist, if it's a song or album the catalogue knows; once only.</summary>
    public async Task<string?> AddAsync(Guid userId, AddToWishlist add, CancellationToken ct)
    {
        var known = add.Kind switch
        {
            RequestKind.Track => await db.Tracks.AnyAsync(t => t.Id == add.TargetId, ct),
            RequestKind.Album => await db.Albums.AnyAsync(a => a.Id == add.TargetId, ct),
            _ => false,
        };
        if (!known)
            return add.Kind is RequestKind.Track or RequestKind.Album ? "There's no such song or album." : "Only songs and albums go on a wishlist.";
        if (await db.WishlistEntries.FirstOrDefaultAsync(w => w.UserId == userId && w.TargetId == add.TargetId, ct) is { } already)
            already.EditionId = add.EditionId ?? already.EditionId;
        else
            db.WishlistEntries.Add(new WishlistEntry { UserId = userId, Kind = add.Kind, TargetId = add.TargetId, EditionId = add.EditionId });
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Takes the song or album off the wishlist; false if it wasn't on it.</summary>
    public async Task<bool> RemoveAsync(Guid userId, Guid targetId, CancellationToken ct) =>
        await db.WishlistEntries.Where(w => w.UserId == userId && w.TargetId == targetId).ExecuteDeleteAsync(ct) > 0;
}
