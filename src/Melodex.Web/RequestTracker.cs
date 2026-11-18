using Melodex.Client;
using Melodex.Web.Components;

namespace Melodex.Web;

/// <summary>
/// Requests, shared by every page so fetch buttons can show what's already on its way: every one still going, however
/// many, and the newest of the rest, with earlier ones loaded as they're wanted (Activity's Earlier). Reloads when the
/// server says requests changed, polling only as a fallback, and tells library pages to reload when a request lands in
/// the library. What's asked for here gets a notification when it starts and when it finishes, one for each lot asked
/// for together.
/// </summary>
public sealed class RequestTracker : IDisposable
{
    private static readonly TimeSpan Busy = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(30);

    // How many finished ones come with each load, and each page of earlier ones.
    private const int Page = 200;

    // Asked for within this long of the last one, it joins its notification.
    private static readonly TimeSpan Together = TimeSpan.FromSeconds(3);

    private readonly MelodexClient api;
    private readonly ServerEvents events;
    private readonly Notices notices;

    // What was asked for here, a lot at a time, until it's all finished.
    private sealed record Lot(string Key, List<RequestInfo> Asked)
    {
        public DateTime Last { get; set; } = DateTime.UtcNow;
    }

    private readonly List<Lot> _lots = [];

    public RequestTracker(MelodexClient api, ServerEvents events, Notices notices)
    {
        this.api = api;
        this.events = events;
        this.notices = notices;
        events.Received += OnEvent;
        events.EnsureStarted();
    }

    private void OnEvent(string type)
    {
        if (type is "requests" or "connected" && _used)
            _ = RefreshAsync();
    }

    private IReadOnlyList<RequestInfo> _requests = [];
    private Timer? _timer;
    private Task? _loading;

    // Kept current once anything has asked for the list, whether through EnsureLoadedAsync or RefreshAsync.
    private bool _used;

    // Earlier ones loaded on top of the newest, kept across reloads; and whether there are more before them.
    private IReadOnlyList<RequestInfo> _earlier = [];
    private bool _more = true;
    private Task? _loadingEarlier;

    /// <summary>Raised whenever the list is refreshed.</summary>
    public event Action? Changed;

    /// <summary>Top-level requests, newest first: all of those still going, and the newest and any earlier loaded of the rest.</summary>
    public IReadOnlyList<RequestInfo> Requests => _requests;

    /// <summary>Whether there are requests before the earliest loaded, for <see cref="LoadEarlierAsync"/>.</summary>
    public bool HasEarlier => _more;

    /// <summary>Loads the page of requests before the earliest loaded.</summary>
    public Task LoadEarlierAsync() => _loadingEarlier ??= LoadEarlierCoreAsync();

    private async Task LoadEarlierCoreAsync()
    {
        try
        {
            if (!_more || _requests.Count == 0)
                return;
            var page = await api.ListRequestsAsync(limit: Page, before: _requests.Min(r => r.CreatedAt));
            _more = page.Count == Page;
            _earlier = _earlier.Concat(page).DistinctBy(r => r.Id).ToList();
            _requests = Merge(_requests, page);
            Changed?.Invoke();
        }
        catch (HttpRequestException)
        {
        }
        finally
        {
            _loadingEarlier = null;
        }
    }

    // Newest first, each once: the later list's copy wins.
    private static List<RequestInfo> Merge(IEnumerable<RequestInfo> first, IEnumerable<RequestInfo> later) =>
        later.Concat(first).DistinctBy(r => r.Id).OrderByDescending(r => r.CreatedAt).ToList();

    public Task EnsureLoadedAsync() => _loading ??= RefreshAsync();

    /// <summary>
    /// The newest request to fetch this artist, album or track into the library, unless it finished there or was
    /// cancelled. Listen-now requests don't count: they aren't fetching it for the library.
    /// </summary>
    public RequestInfo? Find(RequestKind kind, Guid id) =>
        _requests.FirstOrDefault(r => r.Kind == kind && r.TargetId == id && !r.ListenNow) is { State: not (RequestState.InLibrary or RequestState.Cancelled) } request
            ? request
            : null;

    public async Task<RequestInfo> CreateAsync(CreateRequest request)
    {
        var created = await api.CreateRequestAsync(request);
        Asked(created);
        await RefreshAsync();
        return created;
    }

    public async Task RefreshAsync()
    {
        _used = true;
        IReadOnlyList<RequestInfo> latest;
        try
        {
            var going = api.ListRequestsAsync(activeOnly: true, limit: 10_000);
            var newest = await api.ListRequestsAsync(limit: Page);
            if (_earlier.Count == 0)
                _more = newest.Count == Page;
            // Earlier ones loaded before stay, as they were unless they're among those just loaded.
            latest = Merge(_earlier, newest.Concat(await going).DistinctBy(r => r.Id));
        }
        catch (HttpRequestException)
        {
            Schedule(Idle);
            return;
        }
        // Something reaching the library changes what the library pages show.
        var landed = latest.Any(r => _requests.FirstOrDefault(old => old.Id == r.Id) is { } old
                                     && (r.State == RequestState.InLibrary && old.State != RequestState.InLibrary
                                         || (r.Parts?.InLibrary ?? 0) > (old.Parts?.InLibrary ?? 0)));
        _requests = latest;
        Finished();
        Schedule(latest.Any(r => r.Active) && !events.Connected ? Busy : Idle);
        Changed?.Invoke();
        if (landed)
            ScanStatus.RaiseLibraryChanged();
    }

    private void Asked(RequestInfo created)
    {
        var lot = _lots.LastOrDefault();
        if (lot is null || DateTime.UtcNow - lot.Last > Together || lot.Asked.All(r => !r.Active))
            _lots.Add(lot = new Lot($"download:{created.Id}", []));
        lot.Asked.Add(created);
        lot.Last = DateTime.UtcNow;
        notices.Show(new Notice(lot.Key, NoticeKind.Download,
            lot.Asked.Count == 1 ? $"Asked for {created.Title}" : $"Asked for {Things(lot.Asked)}",
            "Follow it in Activity", Href(lot)));
    }

    // Each lot all finished gets its say, then is forgotten.
    private void Finished()
    {
        foreach (var lot in _lots.ToList())
        {
            var now = lot.Asked.Select(r => _requests.FirstOrDefault(n => n.Id == r.Id) ?? r).ToList();
            if (now.Any(r => r.Active) || DateTime.UtcNow - lot.Last < TimeSpan.FromSeconds(1))
                continue;
            _lots.Remove(lot);
            var landed = now.Count(r => r.State == RequestState.InLibrary);
            var failed = now.Count(r => r.State == RequestState.Failed);
            var review = now.Count(r => r.State == RequestState.NeedsReview);
            if (landed + failed + review == 0)
                continue;
            var key = lot.Key + ":done";
            if (now.Count == 1)
            {
                var one = now[0];
                notices.Show(one.State switch
                {
                    RequestState.InLibrary => new Notice(key, NoticeKind.Download, one.Title, "In your library", Href(lot)),
                    RequestState.NeedsReview => new Notice(key, NoticeKind.Download, one.Title, "Fetched, and waiting for you to review it", Href(lot)),
                    _ => new Notice(key, NoticeKind.Download, one.Title, "Couldn't be fetched" + (one.Detail is { } why ? $": {why}" : ""), Href(lot), Failed: true),
                });
                continue;
            }
            var parts = new[] { (landed, "in your library"), (review, "to review"), (failed, "failed") }.Where(p => p.Item1 > 0).Select(p => $"{p.Item1} {p.Item2}");
            notices.Show(new Notice(key, NoticeKind.Download, Things(now) + " finished", string.Join(", ", parts), Href(lot), Failed: landed + review == 0));
        }
    }

    // "12 songs", "2 albums", or "5 things" for a mix.
    private static string Things(IReadOnlyList<RequestInfo> requests) =>
        requests.Select(r => r.Kind).Distinct().Count() == 1
            ? requests[0].Kind switch
            {
                RequestKind.Track => Format.Count(requests.Count, "song", "songs"),
                RequestKind.Album => Format.Count(requests.Count, "album", "albums"),
                _ => Format.Count(requests.Count, "artist", "artists"),
            }
            : Format.Count(requests.Count, "thing", "things");

    private static string Href(Lot lot) => lot.Asked.Count == 1 ? $"activity/downloads?open={lot.Asked[0].Id}" : "activity/downloads";

    private void Schedule(TimeSpan delay)
    {
        _timer ??= new Timer(_ => _ = RefreshAsync());
        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        events.Received -= OnEvent;
        _timer?.Dispose();
    }
}
