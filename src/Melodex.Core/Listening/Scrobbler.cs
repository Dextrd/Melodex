using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Contracts.Plugins.Scrobbling;
using Melodex.Core.Data;
using Melodex.Core.Plugins;

namespace Melodex.Core.Listening;

/// <summary>
/// Sends plays on to the accounts people have linked with the scrobbling plugins (#2, #236), and tells them what's
/// playing now. Plays waiting to be sent are rows, so they survive a restart; one that can't be sent is tried again
/// later, for as long as it takes. An account whose link the service turns down is left until it's linked again, and
/// one whose plugin is off waits for it to come back on.
/// </summary>
public sealed class Scrobbler(IServiceScopeFactory scopes, IDataProtectionProvider protection, ILogger<Scrobbler> log)
    : BackgroundService
{
    /// <summary>The longest a play waits before it's tried again.</summary>
    public static readonly TimeSpan LongestWait = TimeSpan.FromHours(6);

    private readonly SemaphoreSlim _wake = new(0, 1);
    private IDataProtector Protector => protection.CreateProtector("Melodex.Scrobbling");

    public string Protect(string secret) => Protector.Protect(secret);

    public string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return null;
        try
        {
            return Protector.Unprotect(stored);
        }
        catch (CryptographicException)
        {
            // Encrypted with keys that are gone.
            return null;
        }
    }

    /// <summary>There are plays to send.</summary>
    public void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already woken.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = TimeSpan.FromMinutes(1);
            try
            {
                if (await SendDueAsync(stoppingToken))
                    wait = TimeSpan.Zero;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                log.LogError(e, "Couldn't send plays on");
            }
            if (wait > TimeSpan.Zero)
                await _wake.WaitAsync(wait, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    /// <summary>Sends what's due, a batch for each account. True if there may be more.</summary>
    public async Task<bool> SendDueAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        var plugins = (await scope.ServiceProvider.GetRequiredService<ScrobblingPlugins>().GetAsync(ct)).ToDictionary(p => p.Instance.Id, p => p.Plugin);
        if (plugins.Count == 0)
            return false;
        var now = DateTime.UtcNow;
        var on = plugins.Keys.ToList();
        var accounts = await db.ScrobbleSends.Where(s => s.NextAttemptAt <= now && !s.Account.Refused && on.Contains(s.Account.PluginInstanceId))
            .Select(s => s.Account).Distinct().ToListAsync(ct);
        var more = false;
        foreach (var account in accounts)
        {
            var sends = await db.ScrobbleSends.Include(s => s.Play).Where(s => s.AccountId == account.Id && s.NextAttemptAt <= now)
                .OrderBy(s => s.Play.PlayedAt).Take(ScrobblingLimits.Batch).ToListAsync(ct);
            if (sends.Count == 0)
                continue;
            var outcome = await SendAsync(plugins[account.PluginInstanceId], account, sends.Select(s => s.Play).ToList(), ct);
            switch (outcome)
            {
                case Outcome.Sent:
                    db.ScrobbleSends.RemoveRange(sends);
                    account.LastSentAt = DateTime.UtcNow;
                    account.Error = null;
                    more |= sends.Count == ScrobblingLimits.Batch;
                    break;
                case Outcome.Rejected:
                    // The service won't take these whatever happens, so they aren't tried again.
                    db.ScrobbleSends.RemoveRange(sends);
                    break;
                case Outcome.Refused:
                    account.Refused = true;
                    break;
                default:
                    foreach (var send in sends)
                    {
                        send.Attempts++;
                        send.NextAttemptAt = DateTime.UtcNow + Backoff(send.Attempts);
                    }
                    break;
            }
            await db.SaveChangesAsync(ct);
        }
        return more;
    }

    /// <summary>How long to wait after a number of failed tries: doubling from a minute, up to <see cref="LongestWait"/>.</summary>
    public static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromMinutes(Math.Min(Math.Pow(2, Math.Min(attempts - 1, 20)), LongestWait.TotalMinutes));

    private enum Outcome { Sent, Failed, Rejected, Refused }

    private async Task<Outcome> SendAsync(IScrobblingPlugin plugin, ScrobbleAccount account, IReadOnlyList<Play> plays, CancellationToken ct)
    {
        try
        {
            if (Unprotect(account.Secret) is not { } secret)
                throw new ScrobbleLinkRefusedException("Its login can't be read any more: link it again.");
            await plugin.ScrobbleAsync(secret, plays.Select(ListenOf).ToList(), ct);
            return Outcome.Sent;
        }
        catch (ScrobbleLinkRefusedException e)
        {
            account.Error = e.Message;
            return Outcome.Refused;
        }
        catch (ScrobbleRejectedException e)
        {
            account.Error = e.Message;
            log.LogWarning("{Plugin} turned down {Count} plays: {Error}", account.PluginInstanceId, plays.Count, e.Message);
            return Outcome.Rejected;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            account.Error = e is TaskCanceledException ? "It didn't answer in time." : e.Message;
            return Outcome.Failed;
        }
    }

    /// <summary>A play as scrobbling plugins take it.</summary>
    public static Listen ListenOf(Play play) => new(
        play.Title,
        play.ArtistCredit,
        new DateTimeOffset(DateTime.SpecifyKind(play.PlayedAt, DateTimeKind.Utc)),
        play.AlbumTitle,
        play.DurationSeconds is > 0 and var seconds ? TimeSpan.FromSeconds(seconds) : null,
        play.RecordingMbid,
        play.ReleaseMbid,
        play.Client == "web" ? "Melodex" : play.Client);

    // ---- Now playing ----

    private readonly ConcurrentDictionary<Guid, NowPlaying> _now = new();

    private sealed record NowPlaying(string Username, Play Play, DateTime StartedAt);

    /// <summary>Who's playing what now, as Subsonic players' now playing shows: songs started and not yet over.</summary>
    public IReadOnlyList<NowPlayingInfo> Playing()
    {
        var now = DateTime.UtcNow;
        foreach (var (user, playing) in _now)
            if (now - playing.StartedAt > TimeSpan.FromSeconds((playing.Play.DurationSeconds ?? 600) + 60))
                _now.TryRemove(user, out _);
        return _now.Values.OrderByDescending(p => p.StartedAt)
            .Select(p => new NowPlayingInfo(p.Username, PlayService.Info(p.Play), p.StartedAt)).ToList();
    }

    /// <summary>Someone started a song: shown as playing now here, and told to their linked accounts, as far as they answer.</summary>
    public async Task StartedAsync(Guid user, string username, Play play, CancellationToken ct)
    {
        _now[user] = new NowPlaying(username, play, DateTime.UtcNow);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        var plugins = (await scope.ServiceProvider.GetRequiredService<ScrobblingPlugins>().GetAsync(ct)).ToDictionary(p => p.Instance.Id, p => p.Plugin);
        foreach (var account in await db.ScrobbleAccounts.AsNoTracking().Where(a => a.UserId == user && !a.Refused).ToListAsync(ct))
        {
            if (!plugins.TryGetValue(account.PluginInstanceId, out var plugin) || Unprotect(account.Secret) is not { } secret)
                continue;
            try
            {
                await plugin.NowPlayingAsync(secret, ListenOf(play), ct);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Only a nicety: the play itself is sent when it's over.
                log.LogDebug("Couldn't tell {Plugin} what's playing: {Error}", account.PluginInstanceId, e.Message);
            }
        }
    }
}
