using Melodex.Client;

namespace Melodex.Web;

/// <summary>
/// The schedules, read once and kept for the pages that say when their tasks run, and how tasks and schedules are put
/// in words. Saving them on their tab keeps the copy here up to date.
/// </summary>
public sealed class Schedules(MelodexClient api)
{
    private Task<ScheduleSettings?>? _loading;

    /// <summary>The schedules, or null if they couldn't be read.</summary>
    public Task<ScheduleSettings?> GetAsync() => _loading ??= LoadAsync();

    public void Saved(ScheduleSettings settings) => _loading = Task.FromResult<ScheduleSettings?>(settings);

    private async Task<ScheduleSettings?> LoadAsync()
    {
        try
        {
            return (await api.GetSchedulesAsync()).Settings;
        }
        catch (Exception e) when (e is HttpRequestException or MelodexApiException)
        {
            _loading = null;
            return null;
        }
    }

    public static string Name(ScheduledTask task) => task switch
    {
        ScheduledTask.Scan => "Scan the library",
        ScheduledTask.CatalogueUpdate => "Update the catalogue",
        ScheduledTask.Lyrics => "Look for missing lyrics",
        ScheduledTask.RetryFailed => "Retry failed downloads",
        ScheduledTask.Wishlist => "Get what's on wishlists",
        _ => "Look for upgrades",
    };

    public static string Hint(ScheduledTask task) => task switch
    {
        ScheduledTask.Scan => "Reads new and changed files and matches them, then does what the library settings say follows a scan.",
        ScheduledTask.CatalogueUpdate => "Fills in every library artist's discography and the editions of their albums.",
        ScheduledTask.Lyrics => "Asks the lyrics plugins again for songs without lyrics.",
        ScheduledTask.RetryFailed => "Asks again for downloads that failed, a few at a time. Any not started when the window closes wait for the next.",
        ScheduledTask.Wishlist => "Asks for what's on everyone's wishlist, in their name, a few at a time, if a retriever is on to fetch it. Any not started when the window closes are cancelled, and stay wished for.",
        _ => "Goes last, keeping Upgrades at a time going until every song below its cutoff has been tried or the window closes.",
    };

    /// <summary>Where it can be run now, by hand.</summary>
    public static (string Where, string Href) RunNow(ScheduledTask task) => task switch
    {
        ScheduledTask.Scan or ScheduledTask.CatalogueUpdate => ("Scan menu", "library"),
        ScheduledTask.Lyrics => ("Settings → General", "settings#lyrics"),
        ScheduledTask.RetryFailed => ("Activity → Downloads", "activity/downloads"),
        ScheduledTask.Wishlist => ("Wishlist", "wishlist"),
        _ => ("Tools → Look for upgrades", "tools"),
    };

    public static string Name(ScheduleRepeat repeat) => repeat switch
    {
        ScheduleRepeat.Daily => "Daily",
        ScheduleRepeat.Weekly => "Weekly",
        _ => "Monthly",
    };

    /// <summary>When a schedule's window comes round, as "every day, 01:00–06:00".</summary>
    public static string When(ScheduleSettings settings, ScheduleRepeat repeat)
    {
        var window = settings.Of(repeat);
        var times = $"{window.From:HH:mm}–{window.Until:HH:mm}{(window.Until <= window.From ? " the next day" : "")}";
        return window switch
        {
            WeeklySchedule weekly => $"every {weekly.Day}, {times}",
            MonthlySchedule monthly => $"on the {Ordinal(monthly.Day)} of each month{(monthly.Day > 28 ? " (or its last day)" : "")}, {times}",
            _ => $"every day, {times}",
        };
    }

    public static string Ordinal(int day) => day + (day % 100 is 11 or 12 or 13 ? "th" : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
}
