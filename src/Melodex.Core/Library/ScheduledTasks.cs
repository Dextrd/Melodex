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
/// then wishlists, and upgrades last, as they fill what's left of the window. Each window runs once, and the
/// schedules take turns: one whose window opens while another's run is going waits for it, if its window's still open.
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

    /// <summary>Those that are tasks on the Activity page, by their kind.</summary>
    private static string? KindOf(ScheduledTask task) => task switch
    {
        ScheduledTask.Scan => LibraryScanTask.TaskKind,
        ScheduledTask.CatalogueUpdate => CatalogueUpdateTask.TaskKind,
        ScheduledTask.LosslessCheck => SpectrumTask.TaskKind,
        ScheduledTask.Lyrics => LyricsTask.TaskKind,
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
        if (run is null)
        {
            var schedule = await settings.GetScheduleAsync(ct);
            foreach (var repeat in Enum.GetValues<ScheduleRepeat>())
            {
                var tasks = schedule.Of(repeat).Tasks.Distinct().Order().ToList();
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

        run = await AdvanceAsync(run, utcNow, ct);
        var finished = run.FinishedAt is not null;
        if (finished)
            logger.LogInformation("The {Repeat} scheduled tasks are done: {Steps}", run.Repeat,
                string.Join("; ", run.Steps.Select(s => $"{s.Task} {s.State}" + (s.Detail is { } d ? $" ({d})" : ""))));
        var last = new Dictionary<ScheduleRepeat, ScheduleRunInfo>(runs.Last) { [run.Repeat] = run };
        await settings.SaveScheduleRunsAsync(new ScheduleRuns { Current = finished ? null : run, Last = last }, ct);
    }

    // Each step as far as it can go now.
    private async Task<ScheduleRunInfo> AdvanceAsync(ScheduleRunInfo run, DateTime utcNow, CancellationToken ct)
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
                steps[i] = await AskAsync(steps[i], run, utcNow, ct);
                if (steps[i].State == ScheduledStepState.Running)
                    break;
            }

        var done = steps.All(s => s.State is ScheduledStepState.Done or ScheduledStepState.Failed or ScheduledStepState.Skipped);
        return run with { Steps = steps, FinishedAt = done ? utcNow : null };
    }

    private async Task<ScheduledStep> QueueAsync(ScheduledStep step, string kind, CancellationToken ct)
    {
        // Nothing to do is said so, rather than a task that does nothing.
        if (step.Task == ScheduledTask.Lyrics && !await lyrics.HaveSourcesAsync(ct))
            return step with { State = ScheduledStepState.Skipped, Detail = "No lyrics sources are set up" };
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

    private async Task<ScheduledStep> AskAsync(ScheduledStep step, ScheduleRunInfo run, DateTime utcNow, CancellationToken ct)
    {
        try
        {
            switch (step.Task)
            {
                case ScheduledTask.RetryFailed:
                    var again = await RetryFailedAsync(ct);
                    return step with { State = ScheduledStepState.Done, Detail = again == 0 ? "No downloads had failed" : $"Asked again for {LibraryMatcher.Plural(again, "download", "downloads")}" };
                case ScheduledTask.Wishlist:
                    if (!await wishlists.CanFetchAsync(ct))
                        return step with { State = ScheduledStepState.Skipped, Detail = "No retriever is on to fetch them" };
                    var asked = await GetWishlistsAsync(null, ct);
                    return step with { State = ScheduledStepState.Done, Detail = asked == 0 ? "Nothing new to ask for" : $"Asked for {LibraryMatcher.Plural(asked, "song or album", "songs and albums")}" };
                case ScheduledTask.Upgrades when step.State == ScheduledStepState.Waiting:
                    await upgrades.StartScheduledAsync(run.Opens, run.Closes, utcNow, ct);
                    return await UpgradingAsync(step, run, ct);
                case ScheduledTask.Upgrades:
                    return await UpgradingAsync(step, run, ct);
                default:
                    return step with { State = ScheduledStepState.Skipped };
            }
        }
        catch (LibraryBusyException)
        {
            // Asked again next time round, once the library's free.
            return step;
        }
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

    /// <summary>Asks again for downloads that failed: those asked for into the library, not upgrades or replacements. Returns how many.</summary>
    public async Task<int> RetryFailedAsync(CancellationToken ct)
    {
        var failed = await db.Requests.Where(r => r.Status == RequestStatus.Failed && r.ParentRequestId == null && r.Mode == RequestMode.Permanent
                                                  && r.UpgradesFileId == null).OrderBy(r => r.QueueOrder).ToListAsync(ct);
        var again = 0;
        foreach (var request in failed)
            if (await requests.RetryAsync(request, ct))
                again++;
        if (again > 0)
            logger.LogInformation("Asked again for {Count} failed downloads", again);
        return again;
    }

    /// <summary>
    /// Asks for what's on a wishlist, or everyone's, each in the name of who wished for it. Those being fetched already
    /// are left as they are. Returns how many it asked for.
    /// </summary>
    public async Task<int> GetWishlistsAsync(Guid? userId, CancellationToken ct)
    {
        var users = await db.Users.Where(u => userId == null || u.Id == userId)
            .Where(u => db.WishlistEntries.Any(w => w.UserId == u.Id)).Select(u => new { u.Id, u.Username }).ToListAsync(ct);
        var asked = 0;
        foreach (var user in users)
            foreach (var item in (await wishlists.GetAsync(user.Id, ct)).Items)
                if ((await requests.CreateAsync(new CreateRequest(item.Kind, item.TargetId, item.EditionId), user.Username, ct)).Created)
                    asked++;
        if (asked > 0)
            logger.LogInformation("Asked for {Count} songs and albums from wishlists", asked);
        return asked;
    }

    /// <summary>The schedules, when each next runs and how each last went.</summary>
    public async Task<ScheduleStatus> StatusAsync(CancellationToken ct)
    {
        var schedule = await settings.GetScheduleAsync(ct);
        var runs = await settings.GetScheduleRunsAsync(ct);
        var now = DateTime.UtcNow;
        var windows = Enum.GetValues<ScheduleRepeat>().Select(r => new ScheduleWindowStatus(r,
            schedule.Of(r).Tasks.Count > 0 ? ScheduleWindows.Next(schedule, r, now) : null, runs.Last.GetValueOrDefault(r))).ToList();
        return new ScheduleStatus(schedule, windows, runs.Current);
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
