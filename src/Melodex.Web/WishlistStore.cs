using Melodex.Client;

namespace Melodex.Web;

/// <summary>
/// Your wishlist as the app knows it, shared by every page, so a menu can say whether a song's on it and the Listen now
/// buttons can give way to it while nothing can fetch. Loaded once when first needed, and again when the Wishlist tab opens.
/// </summary>
public sealed class WishlistStore(MelodexClient api)
{
    private Task? _loading;
    private HashSet<Guid> _ids = [];

    /// <summary>Raised whenever it's loaded or changed.</summary>
    public event Action? Changed;

    public IReadOnlyList<WishlistItem> Items { get; private set; } = [];

    /// <summary>A retriever is on; until it's known, it's taken to be, so Listen now shows as it always has.</summary>
    public bool CanFetch { get; private set; } = true;

    public bool Loaded { get; private set; }

    public bool Contains(Guid targetId) => _ids.Contains(targetId);

    public Task EnsureLoadedAsync() => _loading ??= RefreshAsync();

    public async Task RefreshAsync()
    {
        try
        {
            var wishlist = await api.GetWishlistAsync();
            Items = wishlist.Items;
            CanFetch = wishlist.CanFetch;
            _ids = wishlist.Items.Select(i => i.TargetId).ToHashSet();
            Loaded = true;
        }
        catch (Exception e) when (e is HttpRequestException or MelodexApiException)
        {
            // A user without a wishlist, such as one who may only browse: nothing's on it.
        }
        Changed?.Invoke();
    }

    public async Task AddAsync(RequestKind kind, Guid targetId, Guid? editionId)
    {
        await api.AddToWishlistAsync(new AddToWishlist(kind, targetId, editionId));
        _ids.Add(targetId);
        await RefreshAsync();
    }

    public async Task RemoveAsync(Guid targetId)
    {
        await api.RemoveFromWishlistAsync(targetId);
        _ids.Remove(targetId);
        Items = Items.Where(i => i.TargetId != targetId).ToList();
        Changed?.Invoke();
    }
}
