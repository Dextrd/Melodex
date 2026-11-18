using Melodex.Client;

namespace Melodex.Web;

/// <summary>What a notification is about, each kind of which can be turned off under Appearance.</summary>
public enum NoticeKind
{
    /// <summary>A long task: a scan, a catalogue update, an import, formatting.</summary>
    Task,

    /// <summary>Something you asked for being fetched.</summary>
    Download,

    /// <summary>Something you did that goes on elsewhere, such as looking for an upgrade.</summary>
    Action,
}

/// <param name="Key">Shown again with the same key, it replaces the one showing.</param>
/// <param name="Working">Still going: it shows a spinner and stays until it's done.</param>
/// <param name="Failed">It went wrong: it shows as an error.</param>
public sealed record Notice(string Key, NoticeKind Kind, string Title, string? Text = null, string? Href = null,
    bool Working = false, bool Failed = false)
{
    /// <summary>When it was first shown, for the order they stack in.</summary>
    public DateTime Shown { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// The notifications that slide in from the bottom right, above the player: the newest at the bottom. One that isn't
/// working closes itself after a few seconds; each can be closed sooner. Kinds the signed-in user turned off aren't shown.
/// </summary>
public sealed class Notices(AuthState auth) : IDisposable
{
    /// <summary>How long one that's done stays.</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(6);

    private readonly List<Notice> _shown = [];
    private readonly Dictionary<string, Timer> _closing = [];
    private readonly HashSet<string> _closedByYou = [];

    /// <summary>Oldest first, so the newest is at the bottom.</summary>
    public IReadOnlyList<Notice> Shown => _shown;

    public event Action? Changed;

    /// <summary>Whether the signed-in user wants this kind.</summary>
    public bool Wants(NoticeKind kind) => auth.Status?.Appearance is not { } appearance || kind switch
    {
        NoticeKind.Task => appearance.TaskNotices,
        NoticeKind.Download => appearance.DownloadNotices,
        _ => appearance.ActionNotices,
    };

    /// <summary>Shows it, or updates the one with its key, keeping its place.</summary>
    public void Show(Notice notice)
    {
        if (!Wants(notice.Kind))
            return;
        var at = _shown.FindIndex(n => n.Key == notice.Key);
        if (at >= 0)
            _shown[at] = notice with { Shown = _shown[at].Shown };
        else
            _shown.Add(notice);
        Forget(notice.Key);
        if (!notice.Working)
            _closing[notice.Key] = new Timer(_ => Close(notice.Key), null, Linger, Timeout.InfiniteTimeSpan);
        Changed?.Invoke();
    }

    /// <summary>The one showing with this key, if it is.</summary>
    public Notice? Find(string key) => _shown.FirstOrDefault(n => n.Key == key);

    /// <summary>Whether it was closed with its close button, so it isn't shown again.</summary>
    public bool WasClosed(string key) => _closedByYou.Contains(key);

    /// <param name="byYou">With its close button, rather than as it times out.</param>
    public void Close(string key, bool byYou = false)
    {
        if (byYou)
            _closedByYou.Add(key);
        Forget(key);
        if (_shown.RemoveAll(n => n.Key == key) > 0)
            Changed?.Invoke();
    }

    public void CloseAll()
    {
        foreach (var notice in _shown.ToList())
        {
            _closedByYou.Add(notice.Key);
            Forget(notice.Key);
        }
        _shown.Clear();
        Changed?.Invoke();
    }

    private void Forget(string key)
    {
        if (_closing.Remove(key, out var timer))
            timer.Dispose();
    }

    public void Dispose()
    {
        foreach (var timer in _closing.Values)
            timer.Dispose();
    }
}
