using Melodex.Client;

namespace Melodex.Web;

/// <summary>
/// Shows a notification for each long task while it runs, updating as it goes, and says how it ended: scans, catalogue
/// updates, imports and formatting. What's queued after them on its own, such as finding lyrics, doesn't pop up.
/// Reads the running tasks when the server says tasks changed, and every few seconds while one runs.
/// </summary>
public sealed class TaskNotices : IDisposable
{
    private static readonly TimeSpan Busy = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(30);

    // The kinds that get one.
    private static readonly HashSet<string> Shown = ["library-scan", "catalogue-update", "library-format", "tools-import"];

    private readonly MelodexClient _api;
    private readonly Notices _notices;
    private readonly ServerEvents _events;
    private readonly Dictionary<Guid, TaskInfo> _running = [];
    private Timer? _timer;
    private bool _started, _reading;

    public TaskNotices(MelodexClient api, Notices notices, ServerEvents events)
    {
        _api = api;
        _notices = notices;
        _events = events;
    }

    public static string KeyOf(Guid taskId) => $"task:{taskId}";

    public void EnsureStarted()
    {
        if (_started)
            return;
        _started = true;
        _events.Received += OnEvent;
        _events.EnsureStarted();
        _ = RefreshAsync();
    }

    /// <summary>A task just started here, shown at once rather than at the next look.</summary>
    public void Started(TaskInfo task)
    {
        EnsureStarted();
        Show(task);
        _running[task.Id] = task;
        Schedule(Busy);
    }

    private void OnEvent(string type)
    {
        if (type is "tasks" or "connected")
            _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_reading)
            return;
        _reading = true;
        try
        {
            var active = (await _api.ListTasksAsync(active: true)).Where(t => Shown.Contains(t.Kind)).ToList();
            foreach (var task in active.Where(t => t.State == TaskState.Running || _running.ContainsKey(t.Id)))
            {
                Show(task);
                _running[task.Id] = task;
            }
            foreach (var id in _running.Keys.Except(active.Select(t => t.Id)).ToList())
            {
                _running.Remove(id);
                if (await _api.GetTaskAsync(id) is { } done)
                    Finished(done);
            }
            Schedule(_running.Count > 0 ? Busy : Idle);
        }
        catch (Exception e) when (e is HttpRequestException or MelodexApiException)
        {
            Schedule(Idle);
        }
        finally
        {
            _reading = false;
        }
    }

    private void Show(TaskInfo task)
    {
        var key = KeyOf(task.Id);
        // Closed while it ran: left closed, and not shown again when it's done.
        if (_notices.WasClosed(key))
            return;
        _notices.Show(new Notice(key, NoticeKind.Task, task.Title ?? task.Kind,
            task.Paused ? "Paused" : task.State == TaskState.Queued ? "Waiting its turn" : task.Progress ?? "Starting…",
            $"activity?open={task.Id}", Working: true));
    }

    private void Finished(TaskInfo task)
    {
        var key = KeyOf(task.Id);
        if (_notices.WasClosed(key))
            return;
        if (task.State == TaskState.Cancelled)
        {
            _notices.Close(key);
            return;
        }
        var failed = task.State == TaskState.Failed;
        _notices.Show(new Notice(key, NoticeKind.Task, (task.Title ?? task.Kind) + (failed ? " failed" : " done"),
            failed ? task.Error : task.Progress, $"activity?open={task.Id}", Failed: failed));
    }

    private void Schedule(TimeSpan after)
    {
        // With events arriving, a look is only needed while something runs, for its progress.
        if (after == Idle && _events.Connected)
        {
            _timer?.Dispose();
            _timer = null;
            return;
        }
        _timer?.Dispose();
        _timer = new Timer(_ => _ = RefreshAsync(), null, after, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        _events.Received -= OnEvent;
        _timer?.Dispose();
    }
}
