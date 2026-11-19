using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Melodex.Client;
using Melodex.Core.Data;
using Melodex.Core.Requests;
using Melodex.Core.Tasks;
using Melodex.Core.Wishlists;

namespace Melodex.Core.Library;

/// <summary>
/// Scheduled tasks (#112): when a schedule's window opens, its ticked tasks run, in <see cref="ScheduledTask"/>'s order.
/// Those that are tasks are queued together, so the task queue runs them one after another; once they've all finished
/// (a scan keeps the library busy, so nothing can be asked for while it goes), failed downloads are asked for again,
/// then wishlists, and upgrades last, as they fill what's left of the window. Each of those keeps a few downloads
/// going, asking for the next as each finishes, until all have been asked for and finished or the window closes. Each
/// window runs once, and the schedules take turns: one whose window opens while another's run is going waits for it, if
/// its window's still open.
/// </summary>
public sealed class ScheduledTasks(
    MelodexDbContext db,
    SettingsService settings,
    TaskQueue queue,
    RequestService requests,
    WishlistService wishlists,
    Upgrades upgrades,
    LyricsFinder lyrics,
    ILogger<ScheduledTasks> logger)
{
    private const string Closed = "The window closed before it started";
    private const string NotStarted = "Asked for again on a schedule, but not started before its window closed";

    /// <summary>Those that are tasks on the Activity page, by their kind.</summary>
    private static string? KindOf(ScheduledTask task) => task switch
    {
        ScheduledTask.Scan => LibraryScanTask.TaskKind,
        ScheduledTask.CatalogueUpdate => CatalogueUpdateTask.TaskKind,
        ScheduledTask.LosslessCheck => SpectrumTask.TaskKind,
        ScheduledTask.Lyrics => LyricsTask.TaskKind,
        ScheduledTask.Bars => BarsTask.TaskKind,
        _ => null,
    };

    public static bool IsTask(ScheduledTask task) => KindOf(task) is not null;

    /// <summary>
    /// Moves the run that's going along, or starts the first schedule whose window is open and hasn't run. Run every few
    /// seconds; nothing runs before the first user is made at setup.
    /// </summary>
    public async Task TickAsync(DateTime utcNow, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(ct))
            return;
        var runs = await settings.GetScheduleRunsAsync(ct);
        var run = runs.Current;
        var downloads = runs.Downloads;
        if (run is null)
        {
            downloads = new ScheduleDownloads();
            var schedule = await settings.GetScheduleAsync(ct);
            var unavailable = await UnavailableAsync(ct);
            foreach (var repeat in Enum.GetValues<ScheduleRepeat>())
            {
                var tasks = schedule.Of(repeat).Tasks.Distinct().Where(t => !unavailable.ContainsKey(t)).Order().ToList();
                if (tasks.Count == 0 || ScheduleWindows.Current(schedule, repeat, utcNow) is not { } open
                    || runs.Last.GetValueOrDefault(repeat)?.Opens == open.Start)
                    continue;
                run = new ScheduleRunInfo(repeat, open.Start, open.End, utcNow, null,
                    tasks.Select(t => new ScheduledStep(t, ScheduledStepState.Waiting)).ToList());
                logger.LogInformation("The {Repeat} scheduled tasks are starting: {Tasks}", repeat, string.Join(", ", tasks));
                break;
            }
            if (run is null)
                return;
        }

        (run, downloads) = await AdvanceAsync(run, downloads, utcNow, ct);
        var finished = run.FinishedAt is not null;
        if (finished)
            logger.LogInformation("The {Repeat} scheduled tasks are done: {Steps}", run.Repeat,
                string.Join("; ", run.Steps.Select(s => $"{s.Task} {s.State}" + (s.Detail is { } d ? $" ({d})" : ""))));
        var last = new Dictionary<ScheduleRepeat, ScheduleRunInfo>(runs.Last) { [run.Repeat] = run };
        await settings.SaveScheduleRunsAsync(new ScheduleRuns
        {
            Current = finished ? null : run, Last = last, Downloads = finished ? new ScheduleDownloads() : downloads,
        }, ct);
    }

    /// <summary>Tasks that make no sense as things are, with why.</summary>
    public async Task<Dictionary<ScheduledTask, string>> UnavailableAsync(CancellationToken ct)
    {
        var unavailable = new Dictionary<ScheduledTask, string>();
        var bars = (await settings.GetPlaybackAsync(ct)).Visualiser;
        if (bars != VisualiserBars.WhenMatching)
            unavailable[ScheduledTask.Bars] = bars == VisualiserBars.Off
                ? "The visualiser is off, so there are no bars to make."
                : "The bars are made as each song plays, so none are kept beside songs to make ahead.";
        return unavailable;
    }

    // Each step as far as it can go now.
    private async Task<(ScheduleRunInfo, ScheduleDownloads)> AdvanceAsync(ScheduleRunInfo run, ScheduleDownloads downloads, DateTime utcNow, CancellationToken ct)
    {
        var steps = run.Steps.ToList();
        var open = utcNow < run.Closes;

        // The tasks, all queued at once: the queue runs one at a time, in the order they're asked for.
        for (var i = 0; i < steps.Count; i++)
            if (steps[i] is { State: ScheduledStepState.Waiting } waiting && KindOf(waiting.Task) is { } kind)
                steps[i] = open ? await QueueAsync(waiting, kind, ct) : waiting with { State = ScheduledStepState.Skipped, Detail = Closed };
        var ids = steps.Select(s => s.TaskId).OfType<Guid>().ToList();
        var tasks = await db.BackgroundTasks.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        for (var i = 0; i < steps.Count; i++)
            if (steps[i] is { State: ScheduledStepState.Running, TaskId: { } id })
                steps[i] = tasks.GetValueOrDefault(id) is { } task ? Finished(steps[i], task) : steps[i] with { State = ScheduledStepState.Skipped, Detail = "Its task is gone" };

        // The rest wait for the tasks, then go in turn.
        if (!steps.Any(s => IsTask(s.Task) && s.State == ScheduledStepState.Running))
            for (var i = 0; i < steps.Count; i++)
            {
                if (IsTask(steps[i].Task) || steps[i].State is not (ScheduledStepState.Waiting or ScheduledStepState.Running))
                    continue;
                if (steps[i].State == ScheduledStepState.Waiting && !open)
                {
                    steps[i] = steps[i] with { State = ScheduledStepState.Skipped, Detail = Closed };
                    continue;
                }
                (steps[i], downloads) = await AskAsync(steps[i], run, downloads, open, utcNow, ct);
                if (steps[i].State == ScheduledStepState.Running)
                    break;
                // Each starts afresh.
                downloads = new ScheduleDownloads();
            }

        var done = steps.All(s => s.State is ScheduledStepState.Done or ScheduledStepState.Failed or ScheduledStepState.Skipped);
        return (run with { Steps = steps, FinishedAt = done ? utcNow : null }, downloads);
    }

    private async Task<ScheduledStep> QueueAsync(ScheduledStep step, string kind, CancellationToken ct)
    {
        // Nothing to do is said so, rather than a task that does nothing.
        if (step.Task == ScheduledTask.Lyrics && !await lyrics.HaveSourcesAsync(ct))
            return step with { State = ScheduledStepState.Skipped, Detail = "No lyrics sources are set up" };
        if (step.Task == ScheduledTask.Bars && (await settings.GetPlaybackAsync(ct)).Visualiser is var bars and not VisualiserBars.WhenMatching)
            return step with
            {
                State = ScheduledStepState.Skipped,
                Detail = bars == VisualiserBars.Off ? "The visualiser is off" : "The visualiser's bars are made as songs play, not ahead",
            };
        if (step.Task == ScheduledTask.LosslessCheck && !await SpectrumTask.Unchecked(db).AnyAsync(ct)
            && !await db.BackgroundTasks.AnyAsync(t => t.Kind == LibraryScanTask.TaskKind && (t.Status == BackgroundTaskStatus.Queued || t.Status == BackgroundTaskStatus.Running), ct))
            return step with { State = ScheduledStepState.Done, Detail = "Every lossless file has been checked" };
        var task = await queue.EnqueueAsync(kind, null, ct);
        return step with { State = ScheduledStepState.Running, TaskId = task.Id };
    }

    private static ScheduledStep Finished(ScheduledStep step, BackgroundTask task) => task.Status switch
    {
        BackgroundTaskStatus.Succeeded => step with { State = ScheduledStepState.Done, Detail = task.Progress },
        BackgroundTaskStatus.Failed => step with { State = ScheduledStepState.Failed, Detail = task.Error },
        BackgroundTaskStatus.Cancelled => step with { State = ScheduledStepState.Skipped, Detail = "Cancelled" },
        _ => step with { Detail = task.Progress },
    };

    private async Task<(ScheduledStep, ScheduleDownloads)> AskAsync(ScheduledStep step, ScheduleRunInfo run, ScheduleDownloads downloads, bool open,
        DateTime utcNow, CancellationToken ct)
    {
        var atATime = Math.Clamp((await settings.GetScheduleAsync(ct)).DownloadsAtATime, 1, ScheduleSettings.MostDownloadsAtATime);
        try
        {
            switch (step.Task)
            {
                case ScheduledTask.RetryFailed:
                    return await RetryingAsync(step, downloads, atATime, open, utcNow, ct);
                case ScheduledTask.Wishlist when step.State == ScheduledStepState.Waiting && !await wishlists.CanFetchAsync(ct):
                    return (step with { State = ScheduledStepState.Skipped, Detail = "No retriever is on to fetch them" }, downloads);
                case ScheduledTask.Wishlist:
                    return await WishingAsync(step, downloads, atATime, open, ct);
                case ScheduledTask.Upgrades when step.State == ScheduledStepState.Waiting:
                    await upgrades.StartScheduledAsync(run.Opens, run.Closes, utcNow, ct);
                    return (await UpgradingAsync(step, run, ct), downloads);
                case ScheduledTask.Upgrades:
                    return (await UpgradingAsync(step, run, ct), downloads);
                default:
                    return (step with { State = ScheduledStepState.Skipped }, downloads);
            }
        }
        catch (LibraryBusyException)
        {
            // Asked again next time round, once the library's free.
            return (step, downloads);
        }
    }

    // Those asked for that haven't finished.
    private Task<int> GoingAsync(IReadOnlyList<Guid> asked, CancellationToken ct) =>
        db.Requests.CountAsync(r => asked.Contains(r.Id) && !RequestService.Finished.Contains(r.Status), ct);

    private IQueryable<Request> Failed() =>
        db.Requests.Where(r => r.Status == RequestStatus.Failed && r.ParentRequestId == null && r.Mode == RequestMode.Permanent && r.UpgradesFileId == null);

    // Failed downloads asked for again a few at a time, each once; any not started when the window closes are failed
    // again, as they were, for the next window to ask for.
    private async Task<(ScheduledStep, ScheduleDownloads)> RetryingAsync(ScheduledStep step, ScheduleDownloads downloads, int atATime, bool open,
        DateTime utcNow, CancellationToken ct)
    {
        var asked = downloads.Asked.ToList();
        var tried = downloads.Tried.ToHashSet();
        if (!open)
        {
            var back = await db.Requests.Where(r => asked.Contains(r.Id) && r.Status == RequestStatus.Queued)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, RequestStatus.Failed).SetProperty(r => r.Detail, NotStarted)
                    .SetProperty(r => r.UpdatedAt, utcNow), ct);
            return (step with
            {
                State = ScheduledStepState.Done,
                Detail = $"Asked again for {LibraryMatcher.Plural(asked.Count - back, "download", "downloads")} before the window closed"
                         + (back > 0 ? $"; {back} not started wait for the next" : ""),
            }, new ScheduleDownloads());
        }

        var untried = (await Failed().OrderBy(r => r.QueueOrder).Select(r => r.Id).ToListAsync(ct)).Where(id => !tried.Contains(id.ToString())).ToList();
        var places = atATime - await GoingAsync(asked, ct);
        var taken = Math.Clamp(places, 0, untried.Count);
        foreach (var id in untried.Take(taken))
        {
            tried.Add(id.ToString());
            if (await db.Requests.FindAsync([id], ct) is { } request && await requests.RetryAsync(request, ct))
                asked.Add(id);
        }
        if (taken > 0)
            logger.LogInformation("Asked again for {Count} failed downloads on a schedule", taken);
        return (await RollingAsync(step, asked, untried.Count - taken, "Asked again for", "download", "downloads", "No downloads had failed", ct),
            new ScheduleDownloads(asked, tried.ToList()));
    }

    // What's on wishlists asked for a few at a time, the longest wished for first; any not started when the window
    // closes are cancelled, and stay on the wishlist for the next window.
    private async Task<(ScheduledStep, ScheduleDownloads)> WishingAsync(ScheduledStep step, ScheduleDownloads downloads, int atATime, bool open,
        CancellationToken ct)
    {
        var asked = downloads.Asked.ToList();
        var tried = downloads.Tried.ToHashSet();
        if (!open)
        {
            var cancelled = 0;
            foreach (var request in await db.Requests.Where(r => asked.Contains(r.Id) && r.Status == RequestStatus.Queued).ToListAsync(ct))
                if (await requests.CancelAsync(request, ct))
                    cancelled++;
            return (step with
            {
                State = ScheduledStepState.Done,
                Detail = $"Asked for {LibraryMatcher.Plural(asked.Count, "song or album", "songs and albums")}"
                         + (cancelled > 0 ? $"; {cancelled} not started when the window closed were cancelled" : ""),
            }, new ScheduleDownloads());
        }

        var untried = (await WishedAsync(null, ct)).Where(w => !tried.Contains(w.Key)).ToList();
        var places = atATime - await GoingAsync(asked, ct);
        var i = 0;
        try
        {
            // One wished for that's being fetched already, asked for by hand say, takes no place.
            for (; i < untried.Count && places > 0; i++)
            {
                var (request, created) = await requests.CreateAsync(untried[i].Create, untried[i].Username, ct);
                tried.Add(untried[i].Key);
                if (created && request is not null)
                {
                    asked.Add(request.Id);
                    places--;
                }
            }
        }
        catch (LibraryBusyException)
        {
            // The rest next time round.
        }
        return (await RollingAsync(step, asked, untried.Count - i, "Asked for", "song or album", "songs and albums", "Nothing new to ask for", ct),
            new ScheduleDownloads(asked, tried.ToList()));
    }

    // Going while there's more to ask for or what's been asked for hasn't finished; done once neither.
    private async Task<ScheduledStep> RollingAsync(ScheduledStep step, IReadOnlyList<Guid> asked, int left, string verb, string one, string many,
        string nothing, CancellationToken ct)
    {
        var going = await GoingAsync(asked, ct);
        if (left > 0 || going > 0)
            return step with { State = ScheduledStepState.Running, Detail = $"{verb} {LibraryMatcher.Plural(asked.Count, one, many)} so far, {going} going" };
        return step with { State = ScheduledStepState.Done, Detail = asked.Count == 0 ? nothing : $"{verb} {LibraryMatcher.Plural(asked.Count, one, many)}" };
    }

    // A look for upgrades goes on as long as it goes, until everything's been tried or the window closes.
    private async Task<ScheduledStep> UpgradingAsync(ScheduledStep step, ScheduleRunInfo run, CancellationToken ct)
    {
        if (await upgrades.ScheduledRunAsync(run.Opens, ct) is { } going)
            return step with { State = ScheduledStepState.Running, Detail = $"Asked for {LibraryMatcher.Plural(going.Asked, "upgrade", "upgrades")} so far" };
        var ended = await settings.GetUpgradeRunAsync(ct);
        if (ended.Window != run.Opens || ended.ByHand)
            return step with { State = ScheduledStepState.Done, Detail = "A look for upgrades was going already" };
        var how = ended.Done ? "every song below its cutoff tried" : ended.Stopped ? "stopped" : "the window closed";
        return step with { State = ScheduledStepState.Done, Detail = $"Asked for {LibraryMatcher.Plural(ended.Asked, "upgrade", "upgrades")}; {how}" };
    }

    /// <summary>
    /// Retry failed: asks again, all at once, for downloads that failed: those asked for into the library, not upgrades or
    /// replacements. Returns how many.
    /// </summary>
    public async Task<int> RetryFailedAsync(CancellationToken ct)
    {
        var failed = await Failed().OrderBy(r => r.QueueOrder).ToListAsync(ct);
        var again = 0;
        foreach (var request in failed)
            if (await requests.RetryAsync(request, ct))
                again++;
        if (again > 0)
            logger.LogInformation("Asked again for {Count} failed downloads", again);
        return again;
    }

    /// <summary>
    /// Get them all: asks, all at once, for what's on a wishlist, or everyone's, each in the name of who wished for it.
    /// Those being fetched already are left as they are. Returns how many it asked for.
    /// </summary>
    public async Task<int> GetWishlistsAsync(Guid? userId, CancellationToken ct)
    {
        var asked = 0;
        foreach (var wished in await WishedAsync(userId, ct))
            if ((await requests.CreateAsync(wished.Create, wished.Username, ct)).Created)
                asked++;
        if (asked > 0)
            logger.LogInformation("Asked for {Count} songs and albums from wishlists", asked);
        return asked;
    }

    // What's on a wishlist, or everyone's, the longest wished for first: each song or album once, in the name of the
    // first to wish for it.
    private async Task<List<(string Key, CreateRequest Create, string Username)>> WishedAsync(Guid? userId, CancellationToken ct)
    {
        var users = await db.Users.Where(u => userId == null || u.Id == userId)
            .Where(u => db.WishlistEntries.Any(w => w.UserId == u.Id)).Select(u => new { u.Id, u.Username }).ToListAsync(ct);
        var wished = new List<(WishlistItem Item, string Username)>();
        foreach (var user in users)
            wished.AddRange((await wishlists.GetAsync(user.Id, ct)).Items.Select(i => (i, user.Username)));
        return wished.OrderBy(w => w.Item.AddedAt)
            .Select(w => (Key: $"{w.Item.Kind}:{w.Item.TargetId}:{w.Item.EditionId}", Create: new CreateRequest(w.Item.Kind, w.Item.TargetId, w.Item.EditionId), w.Username))
            .DistinctBy(w => w.Key).ToList();
    }

    /// <summary>The schedules, when each next runs and how each last went.</summary>
    public async Task<ScheduleStatus> StatusAsync(CancellationToken ct)
    {
        var schedule = await settings.GetScheduleAsync(ct);
        var runs = await settings.GetScheduleRunsAsync(ct);
        var unavailable = await UnavailableAsync(ct);
        var now = DateTime.UtcNow;
        var windows = Enum.GetValues<ScheduleRepeat>().Select(r => new ScheduleWindowStatus(r,
            schedule.Of(r).Tasks.Any(t => !unavailable.ContainsKey(t)) ? ScheduleWindows.Next(schedule, r, now) : null, runs.Last.GetValueOrDefault(r))).ToList();
        return new ScheduleStatus(schedule, windows, runs.Current, unavailable);
    }
}

/// <summary>Runs scheduled tasks as their windows open, every few seconds. Off in tests, which move them along themselves.</summary>
public sealed class ScheduleRunner(IServiceScopeFactory scopes, IOptions<MelodexOptions> options, ILogger<ScheduleRunner> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.ScheduledTasks)
            return;
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ScheduledTasks>().TickAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning("Couldn't run scheduled tasks: {Message}", e.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
