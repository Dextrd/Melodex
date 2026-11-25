using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>Scheduled tasks: the daily, weekly and monthly windows, and a window's run of what's ticked for it.</summary>
public sealed class ScheduleTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly MelodexClient _api;

    public ScheduleTests()
    {
        Directory.CreateDirectory(Path.Combine(_dataPath, "music"));
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Melodex:DataPath", _dataPath).UseSetting("Melodex:ScheduledTasks", "false")
            .UseSetting("Melodex:LibraryPath", Path.Combine(_dataPath, "music")));
        _api = new MelodexClient(_factory.CreateSignedInClient());
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(_dataPath);
    }

    [Fact]
    public async Task A_new_install_scans_daily_and_upgrades_weekly_and_upgrades_say_so()
    {
        var status = await _api.GetSchedulesAsync();
        Assert.Equal([ScheduledTask.Scan, ScheduledTask.CatalogueUpdate], status.Settings.Daily.Tasks);
        Assert.Equal(5, status.Settings.DownloadsAtATime);
        Assert.Equal([ScheduledTask.Lyrics, ScheduledTask.RetryFailed, ScheduledTask.Wishlist, ScheduledTask.Upgrades], status.Settings.Weekly.Tasks);
        Assert.Empty(status.Settings.Monthly.Tasks);
        Assert.NotNull(status.Windows.Single(w => w.Repeat == ScheduleRepeat.Daily).Next);
        Assert.Null(status.Windows.Single(w => w.Repeat == ScheduleRepeat.Monthly).Next);
        Assert.Null(status.Running);

        // Upgrades moved to the monthly schedule: their settings, read only now, say when.
        await _api.SaveSchedulesAsync(status.Settings with
        {
            Weekly = status.Settings.Weekly with { Tasks = [ScheduledTask.Lyrics] },
            Monthly = new MonthlySchedule { Day = 31, From = new(22, 0), Until = new(4, 0), Tasks = [ScheduledTask.Upgrades, ScheduledTask.Wishlist, ScheduledTask.Upgrades] },
        });
        var saved = (await _api.GetSchedulesAsync()).Settings;
        Assert.Equal([ScheduledTask.Wishlist, ScheduledTask.Upgrades], saved.Monthly.Tasks);
        var upgrades = await _api.GetUpgradesAsync();
        Assert.True(upgrades.Settings.Scheduled);
        Assert.Equal((new TimeOnly(22, 0), new TimeOnly(4, 0)), (upgrades.Settings.From, upgrades.Settings.Until));
        Assert.Equal(22, upgrades.NextRun!.Value.Hour);
    }

    [Fact]
    public async Task Lossless_checks_and_bars_are_no_longer_scheduled_and_old_schedules_carry_on_without_them()
    {
        // A schedule saved before they moved to Tools (#237) still reads, without them.
        var now = DateTime.UtcNow;
        var from = TimeOnly.FromDateTime(now.AddHours(-1)).ToString("HH:mm");
        var until = TimeOnly.FromDateTime(now.AddHours(1)).ToString("HH:mm");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            db.Settings.Add(new Setting
            {
                Key = "schedule",
                ValueJson = $$"""{ "timeZone": "UTC", "daily": { "from": "{{from}}", "until": "{{until}}", "tasks": ["losslessCheck", "bars", "lyrics"] }, "weekly": { "tasks": [] } }""",
            });
            await db.SaveChangesAsync();
        }
        var status = await _api.GetSchedulesAsync();
        Assert.Equal([ScheduledTask.Lyrics], status.Settings.Daily.Tasks);
        Assert.Empty(status.Unavailable);

        // A run leaves them out.
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ScheduledTasks>().TickAsync(now, default);
        status = await _api.GetSchedulesAsync();
        var run = status.Running ?? status.Windows.Single(w => w.Repeat == ScheduleRepeat.Daily).Last!;
        Assert.Equal([ScheduledTask.Lyrics], run.Steps.Select(s => s.Task));

        // Bars are made from Tools only while they sit beside songs.
        var refused = await Assert.ThrowsAsync<MelodexApiException>(() => _api.MakeBarsAsync());
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        await _api.SavePlaybackSettingsAsync(new SavePlayback(VisualiserBars.WhenMatching));
        Assert.Equal(BarsTask.TaskKind, (await _api.MakeBarsAsync()).Kind);
    }

    [Theory]
    [InlineData("zone")]
    [InlineData("month")]
    [InlineData("twice")]
    [InlineData("sync")]
    [InlineData("downloads")]
    public async Task A_schedule_that_cant_run_is_refused(string wrong)
    {
        var settings = (await _api.GetSchedulesAsync()).Settings;
        settings = wrong switch
        {
            "zone" => settings with { TimeZone = "Nowhere/Atlantis" },
            "month" => settings with { Monthly = settings.Monthly with { Day = 32 } },
            "twice" => settings with { Monthly = settings.Monthly with { Tasks = [ScheduledTask.Scan] } },
            "sync" => settings with { PlaylistSyncMinutes = 0 },
            _ => settings with { DownloadsAtATime = 21 },
        };
        var refused = await Assert.ThrowsAsync<MelodexApiException>(() => _api.SaveSchedulesAsync(settings));
        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
    }

    [Fact]
    public async Task An_install_from_before_keeps_its_upgrade_schedule()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            // As the migration leaves an install that had users: marked, with no schedule yet.
            db.Settings.Add(new Setting { Key = SettingsService.ScheduleFromUpgradesKey, ValueJson = "true" });
            await db.SaveChangesAsync();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            await settings.SaveUpgradesAsync(await settings.GetUpgradesAsync(default) with
            {
                Days = Enum.GetValues<DayOfWeek>(), From = new(2, 0), Until = new(5, 0), TimeZone = "Europe/London",
            }, default);
        }

        var schedule = (await _api.GetSchedulesAsync()).Settings;
        Assert.Equal("Europe/London", schedule.TimeZone);
        Assert.Equal([ScheduledTask.Upgrades], schedule.Daily.Tasks);
        Assert.Equal((new TimeOnly(2, 0), new TimeOnly(5, 0)), (schedule.Daily.From, schedule.Daily.Until));
        Assert.Empty(schedule.Weekly.Tasks);
        Assert.Empty(schedule.Monthly.Tasks);

        // Saving upgrades' other settings, which say when as the schedules do, leaves it as it was.
        var upgrades = (await _api.GetUpgradesAsync()).Settings;
        await _api.SaveUpgradeSettingsAsync(upgrades with { AtATime = 2, Scheduled = false, Days = [] });
        Assert.Equal(schedule, (await _api.GetSchedulesAsync()).Settings, ScheduleComparer.Instance);
    }

    // Records compare their lists by reference, so the tasks are compared by what's in them.
    private sealed class ScheduleComparer : IEqualityComparer<ScheduleSettings>
    {
        public static readonly ScheduleComparer Instance = new();

        public bool Equals(ScheduleSettings? x, ScheduleSettings? y) =>
            x is not null && y is not null && Plain(x) == Plain(y)
            && Enum.GetValues<ScheduleRepeat>().All(r => x.Of(r).Tasks.SequenceEqual(y.Of(r).Tasks));

        public int GetHashCode(ScheduleSettings obj) => 0;

        private static ScheduleSettings Plain(ScheduleSettings s) => s with
        {
            Daily = s.Daily with { Tasks = [] }, Weekly = s.Weekly with { Tasks = [] }, Monthly = s.Monthly with { Tasks = [] },
        };
    }

    [Fact]
    public async Task A_window_runs_its_tasks_once_in_order()
    {
        var settings = (await _api.GetSchedulesAsync()).Settings;
        var now = DateTime.UtcNow;
        // A window open now, in UTC, scanning then asking again for failed downloads.
        await _api.SaveSchedulesAsync(settings with
        {
            TimeZone = "UTC",
            Daily = new DailySchedule { From = TimeOnly.FromDateTime(now.AddHours(-1)), Until = TimeOnly.FromDateTime(now.AddHours(1)), Tasks = [ScheduledTask.RetryFailed, ScheduledTask.Scan] },
            Weekly = settings.Weekly with { Tasks = [] },
        });

        using var scope = _factory.Services.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<ScheduledTasks>();
        await tasks.TickAsync(now, default);
        var running = (await _api.GetSchedulesAsync()).Running!;
        Assert.Equal(ScheduleRepeat.Daily, running.Repeat);
        Assert.Equal([ScheduledTask.Scan, ScheduledTask.RetryFailed], running.Steps.Select(s => s.Task));
        Assert.Equal(ScheduledStepState.Running, running.Steps[0].State);
        Assert.Equal(ScheduledStepState.Waiting, running.Steps[1].State);
        var scan = running.Steps[0].TaskId!.Value;

        // Once the scan's finished, the downloads go.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((await _api.GetTaskAsync(scan))?.State is TaskState.Queued or TaskState.Running && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        ScheduleRunInfo? last = null;
        for (var i = 0; i < 20 && last is null; i++)
        {
            await tasks.TickAsync(now, default);
            var status = await _api.GetSchedulesAsync();
            last = status.Running is null ? status.Windows.Single(w => w.Repeat == ScheduleRepeat.Daily).Last : null;
            if (last is null)
                await Task.Delay(100);
        }
        Assert.NotNull(last?.FinishedAt);
        // The music folder's empty, so the scan fails, saying why; that doesn't stop the rest.
        Assert.Equal(ScheduledStepState.Failed, last.Steps[0].State);
        Assert.Contains("empty", last.Steps[0].Detail);
        Assert.Equal(ScheduledStepState.Done, last.Steps[1].State);
        Assert.Equal("No downloads had failed", last.Steps[1].Detail);

        // The same window doesn't run again.
        await tasks.TickAsync(now.AddMinutes(1), default);
        Assert.Null((await _api.GetSchedulesAsync()).Running);
    }

    [Fact]
    public async Task Failed_downloads_are_retried_a_few_at_a_time_until_the_window_closes()
    {
        // Without the request worker, what's retried stays queued until the test says otherwise.
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            foreach (var worker in services.Where(d => d.ImplementationType == typeof(Melodex.Core.Requests.RequestWorker)).ToList())
                services.Remove(worker);
        }));
        var api = new MelodexClient(factory.CreateSignedInClient());
        var settings = (await api.GetSchedulesAsync()).Settings;
        var now = DateTime.UtcNow;
        await api.SaveSchedulesAsync(settings with
        {
            TimeZone = "UTC", DownloadsAtATime = 2,
            Daily = new DailySchedule { From = TimeOnly.FromDateTime(now.AddHours(-1)), Until = TimeOnly.FromDateTime(now.AddHours(1)), Tasks = [ScheduledTask.RetryFailed] },
            Weekly = settings.Weekly with { Tasks = [] },
        });
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        var failed = Enumerable.Range(0, 5).Select(i => new Request
        {
            TargetKind = EntityKind.Track, TargetId = Guid.NewGuid(), Title = $"Song {i}", Requester = "admin", Status = RequestStatus.Failed, QueueOrder = i,
        }).ToList();
        db.Requests.AddRange(failed);
        await db.SaveChangesAsync();
        var tasks = scope.ServiceProvider.GetRequiredService<ScheduledTasks>();
        async Task<List<Guid>> QueuedAsync() => await db.Requests.AsNoTracking().Where(r => r.Status == RequestStatus.Queued).Select(r => r.Id).ToListAsync();

        // Two at a time, the first in the queue first.
        await tasks.TickAsync(now, default);
        Assert.Equal(failed.Take(2).Select(r => r.Id).Order(), (await QueuedAsync()).Order());
        var step = (await api.GetSchedulesAsync()).Running!.Steps.Single();
        Assert.Equal((ScheduledStepState.Running, "Asked again for 2 downloads so far, 2 going"), (step.State, step.Detail));
        await tasks.TickAsync(now, default);
        Assert.Equal(2, (await QueuedAsync()).Count);

        // As those finish, failing again, the next two go in their place; neither is asked for again.
        await db.Requests.Where(r => r.Status == RequestStatus.Queued).ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, RequestStatus.Failed));
        await tasks.TickAsync(now, default);
        Assert.Equal(failed.Skip(2).Take(2).Select(r => r.Id).Order(), (await QueuedAsync()).Order());

        // The window closes: those not started are failed again, for the next window, and the last is left as it was.
        await tasks.TickAsync(now.AddHours(2), default);
        Assert.Empty(await QueuedAsync());
        Assert.Equal(2, await db.Requests.CountAsync(r => r.Status == RequestStatus.Failed && r.Detail != null && r.Detail.Contains("not started before its window closed")));
        var last = (await api.GetSchedulesAsync()).Windows.Single(w => w.Repeat == ScheduleRepeat.Daily).Last!.Steps.Single();
        Assert.Equal((ScheduledStepState.Done, "Asked again for 2 downloads before the window closed; 2 not started wait for the next"), (last.State, last.Detail));
    }

    [Fact]
    public async Task What_hasnt_started_when_the_window_closes_is_skipped()
    {
        var settings = (await _api.GetSchedulesAsync()).Settings;
        var now = DateTime.UtcNow;
        await _api.SaveSchedulesAsync(settings with
        {
            TimeZone = "UTC",
            Daily = settings.Daily with { Tasks = [] },
            Weekly = new WeeklySchedule { Day = now.DayOfWeek, From = new(0, 0), Until = new(0, 0), Tasks = [ScheduledTask.Wishlist] },
        });
        using var scope = _factory.Services.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<ScheduledTasks>();
        var store = scope.ServiceProvider.GetRequiredService<SettingsService>();
        // A run left going, its window since closed: the server was off, say.
        var opens = now.Date;
        await store.SaveScheduleRunsAsync(new ScheduleRuns
        {
            Current = new ScheduleRunInfo(ScheduleRepeat.Weekly, opens, opens.AddDays(1), opens, null, [new ScheduledStep(ScheduledTask.Wishlist, ScheduledStepState.Waiting)]),
        }, default);

        await tasks.TickAsync(opens.AddDays(1).AddMinutes(1), default);
        var last = (await _api.GetSchedulesAsync()).Windows.Single(w => w.Repeat == ScheduleRepeat.Weekly).Last!;
        Assert.Equal(ScheduledStepState.Skipped, last.Steps.Single().State);
        Assert.NotNull(last.FinishedAt);
    }
}
