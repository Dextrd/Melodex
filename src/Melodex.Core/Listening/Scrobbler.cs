using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Core.Data;
using Melodex.Core.Library;

namespace Melodex.Core.Listening;

/// <summary>The Last.fm API account plays are sent with, as kept: the secret encrypted.</summary>
public sealed class ScrobbleSettings
{
    public string? LastFmApiKey { get; set; }
    public string? LastFmSecret { get; set; }
}

/// <summary>Why a scrobbling service turned something down, in words to show.</summary>
public sealed class ScrobbleException(string message) : Exception(message);

/// <summary>
/// Sends plays on to the ListenBrainz and Last.fm accounts people have linked (#2), and tells them what's playing now.
/// Plays waiting to be sent are rows, so they survive a restart; one that can't be sent is tried again later, for as
/// long as it takes. An account whose link the service turns down is left until it's linked again.
/// </summary>
public sealed class Scrobbler(IServiceScopeFactory scopes, IHttpClientFactory clients, IDataProtectionProvider protection, ILogger<Scrobbler> log)
    : BackgroundService
{
    public const string HttpClientName = "scrobbling";
    public const string ListenBrainzApi = "https://api.listenbrainz.org/1/";
    public const string LastFmApi = "https://ws.audioscrobbler.com/2.0/";
    public const string LastFmAuth = "https://www.last.fm/api/auth/";

    /// <summary>How many plays go in one request.</summary>
    private const int ListenBrainzBatch = 100;
    private const int LastFmBatch = 50;

    /// <summary>The longest a play waits before it's tried again.</summary>
    public static readonly TimeSpan LongestWait = TimeSpan.FromHours(6);

    private static readonly string Version =
        typeof(Scrobbler).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1";

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

    private HttpClient Client()
    {
        var client = clients.CreateClient(HttpClientName);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Melodex", Version));
        return client;
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
        var settings = await scope.ServiceProvider.GetRequiredService<SettingsService>().GetScrobblingAsync(ct);
        var now = DateTime.UtcNow;
        var accounts = await db.ScrobbleSends.Where(s => s.NextAttemptAt <= now && !s.Account.Refused)
            .Select(s => s.Account).Distinct().ToListAsync(ct);
        var more = false;
        foreach (var account in accounts)
        {
            var batch = account.Service == ScrobbleService.ListenBrainz ? ListenBrainzBatch : LastFmBatch;
            var sends = await db.ScrobbleSends.Include(s => s.Play).Where(s => s.AccountId == account.Id && s.NextAttemptAt <= now)
                .OrderBy(s => s.Play.PlayedAt).Take(batch).ToListAsync(ct);
            if (sends.Count == 0)
                continue;
            var outcome = await SendAsync(account, sends.Select(s => s.Play).ToList(), settings, ct);
            switch (outcome)
            {
                case Outcome.Sent:
                    db.ScrobbleSends.RemoveRange(sends);
                    account.LastSentAt = DateTime.UtcNow;
                    account.Error = null;
                    more |= sends.Count == batch;
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

    private async Task<Outcome> SendAsync(ScrobbleAccount account, IReadOnlyList<Play> plays, ScrobbleSettings settings, CancellationToken ct)
    {
        try
        {
            if (Unprotect(account.Secret) is not { } secret)
                throw new RefusedException("Its login can't be read any more: link it again.");
            if (account.Service == ScrobbleService.ListenBrainz)
                await ListenBrainzAsync(secret, plays.Count == 1 ? "single" : "import", plays, ct);
            else
                await LastFmScrobbleAsync(secret, plays, settings, ct);
            return Outcome.Sent;
        }
        catch (RefusedException e)
        {
            account.Error = e.Message;
            return Outcome.Refused;
        }
        catch (RejectedException e)
        {
            account.Error = e.Message;
            log.LogWarning("{Service} turned down {Count} plays: {Error}", account.Service, plays.Count, e.Message);
            return Outcome.Rejected;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ScrobbleException or JsonException)
        {
            account.Error = e is TaskCanceledException ? "It didn't answer in time." : e.Message;
            return Outcome.Failed;
        }
    }

    /// <summary>The link no longer works: nothing's sent until it's linked again.</summary>
    private sealed class RefusedException(string message) : Exception(message);

    /// <summary>The plays themselves were turned down, so trying them again won't help.</summary>
    private sealed class RejectedException(string message) : Exception(message);

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
        var settings = await scope.ServiceProvider.GetRequiredService<SettingsService>().GetScrobblingAsync(ct);
        foreach (var account in await db.ScrobbleAccounts.AsNoTracking().Where(a => a.UserId == user && !a.Refused).ToListAsync(ct))
        {
            try
            {
                if (Unprotect(account.Secret) is not { } secret)
                    continue;
                if (account.Service == ScrobbleService.ListenBrainz)
                    await ListenBrainzAsync(secret, "playing_now", [play], ct);
                else
                    await LastFmNowPlayingAsync(secret, play, settings, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ScrobbleException or RefusedException or RejectedException
                                          or JsonException)
            {
                // Only a nicety: the play itself is sent when it's over.
                log.LogDebug("Couldn't tell {Service} what's playing: {Error}", account.Service, e.Message);
            }
        }
    }

    // ---- ListenBrainz ----

    /// <summary>The ListenBrainz username a user token belongs to.</summary>
    /// <exception cref="ScrobbleException">The token isn't one, or ListenBrainz couldn't be asked.</exception>
    public async Task<string> ListenBrainzUserAsync(string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ListenBrainzApi + "validate-token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token.Trim());
        try
        {
            using var response = await Client().SendAsync(request, ct);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            if (body?["valid"]?.GetValue<bool>() == true && (string?)body["user_name"] is { Length: > 0 } user)
                return user;
            throw new ScrobbleException("ListenBrainz doesn't know that token. Copy it from your ListenBrainz settings.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new ScrobbleException($"ListenBrainz couldn't be reached: {e.Message}");
        }
    }

    private async Task ListenBrainzAsync(string token, string type, IReadOnlyList<Play> plays, CancellationToken ct)
    {
        var payload = new JsonArray();
        foreach (var play in plays)
        {
            var info = new JsonObject
            {
                ["media_player"] = play.Client == "web" ? "Melodex" : play.Client,
                ["submission_client"] = "Melodex",
                ["submission_client_version"] = Version,
            };
            if (play.RecordingMbid is { } recording)
                info["recording_mbid"] = recording;
            if (play.ReleaseMbid is { } release)
                info["release_mbid"] = release;
            if (play.DurationSeconds is > 0 and var seconds)
                info["duration_ms"] = (long)Math.Round(seconds * 1000);
            var metadata = new JsonObject { ["artist_name"] = play.ArtistCredit, ["track_name"] = play.Title, ["additional_info"] = info };
            if (play.AlbumTitle is { Length: > 0 } album)
                metadata["release_name"] = album;
            var listen = new JsonObject { ["track_metadata"] = metadata };
            if (type != "playing_now")
                listen["listened_at"] = new DateTimeOffset(DateTime.SpecifyKind(play.PlayedAt, DateTimeKind.Utc)).ToUnixTimeSeconds();
            payload.Add(listen);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, ListenBrainzApi + "submit-listens")
        {
            Content = new StringContent(new JsonObject { ["listen_type"] = type, ["payload"] = payload }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        using var response = await Client().SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
            return;
        var error = await ErrorOfAsync(response, ct);
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new RefusedException("ListenBrainz no longer takes this token: link it again."),
            HttpStatusCode.BadRequest => new RejectedException($"ListenBrainz turned them down: {error}"),
            _ => new ScrobbleException($"ListenBrainz answered {(int)response.StatusCode}: {error}"),
        };
    }

    private static async Task<string> ErrorOfAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return (string?)JsonNode.Parse(text)?["error"] ?? response.ReasonPhrase ?? "";
        }
        catch (JsonException)
        {
            return response.ReasonPhrase ?? "";
        }
    }

    // ---- Last.fm ----

    /// <summary>
    /// Turns the token Last.fm gave back, once someone allowed Melodex, into a session key: their username and the key.
    /// </summary>
    /// <exception cref="ScrobbleException">No API key is set, or Last.fm turned the token down.</exception>
    public async Task<(string Username, string SessionKey)> LastFmSessionAsync(string token, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var settings = await scope.ServiceProvider.GetRequiredService<SettingsService>().GetScrobblingAsync(ct);
        var body = await LastFmCallAsync(settings, new() { ["method"] = "auth.getSession", ["token"] = token.Trim() }, HttpMethod.Get, ct);
        if ((string?)body["session"]?["name"] is { Length: > 0 } name && (string?)body["session"]?["key"] is { Length: > 0 } key)
            return (name, key);
        throw new ScrobbleException("Last.fm didn't give a session.");
    }

    private async Task LastFmScrobbleAsync(string session, IReadOnlyList<Play> plays, ScrobbleSettings settings, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string> { ["method"] = "track.scrobble", ["sk"] = session };
        // Last.fm takes only songs longer than half a minute.
        var sent = plays.Where(p => p.DurationSeconds is null or > 30).ToList();
        if (sent.Count == 0)
            return;
        for (var i = 0; i < sent.Count; i++)
        {
            var play = sent[i];
            parameters[$"artist[{i}]"] = play.ArtistCredit;
            parameters[$"track[{i}]"] = play.Title;
            parameters[$"timestamp[{i}]"] = new DateTimeOffset(DateTime.SpecifyKind(play.PlayedAt, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString();
            if (play.AlbumTitle is { Length: > 0 } album)
                parameters[$"album[{i}]"] = album;
            if (play.DurationSeconds is > 0 and var seconds)
                parameters[$"duration[{i}]"] = ((int)Math.Round(seconds)).ToString();
            if (play.RecordingMbid is { } mbid)
                parameters[$"mbid[{i}]"] = mbid;
            parameters[$"chosenByUser[{i}]"] = "1";
        }
        await LastFmCallAsync(settings, parameters, HttpMethod.Post, ct);
    }

    private async Task LastFmNowPlayingAsync(string session, Play play, ScrobbleSettings settings, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string>
        {
            ["method"] = "track.updateNowPlaying", ["sk"] = session, ["artist"] = play.ArtistCredit, ["track"] = play.Title,
        };
        if (play.AlbumTitle is { Length: > 0 } album)
            parameters["album"] = album;
        if (play.DurationSeconds is > 0 and var seconds)
            parameters["duration"] = ((int)Math.Round(seconds)).ToString();
        await LastFmCallAsync(settings, parameters, HttpMethod.Post, ct);
    }

    /// <summary>A signed Last.fm call, as JSON.</summary>
    private async Task<JsonNode> LastFmCallAsync(ScrobbleSettings settings, Dictionary<string, string> parameters, HttpMethod method, CancellationToken ct)
    {
        if (settings.LastFmApiKey is not { Length: > 0 } apiKey || Unprotect(settings.LastFmSecret) is not { Length: > 0 } secret)
            throw new ScrobbleException("The admin hasn't set a Last.fm API key and secret in Settings → Playback.");
        parameters["api_key"] = apiKey;
        parameters["api_sig"] = LastFmSignature(parameters, secret);
        parameters["format"] = "json";
        using var request = method == HttpMethod.Get
            ? new HttpRequestMessage(HttpMethod.Get, LastFmApi + "?" + string.Join('&', parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}")))
            : new HttpRequestMessage(HttpMethod.Post, LastFmApi) { Content = new FormUrlEncodedContent(parameters) };
        using var response = await Client().SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? body;
        try
        {
            body = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            throw new ScrobbleException($"Last.fm answered {(int)response.StatusCode}.");
        }
        if (body?["error"] is { } code)
        {
            var message = (string?)body["message"] ?? "";
            throw code.GetValue<int>() switch
            {
                // Invalid session or authentication: the person has to link again.
                4 or 9 or 14 or 15 => parameters["method"] == "auth.getSession"
                    ? new ScrobbleException($"Last.fm turned it down: {message}. Try linking again.")
                    : new RefusedException($"Last.fm no longer takes this link ({message}): link it again."),
                6 or 7 => new RejectedException($"Last.fm turned them down: {message}"),
                10 or 26 => new ScrobbleException($"Last.fm doesn't take the admin's API key: {message}"),
                13 => new ScrobbleException("Last.fm says the signature is wrong: check the admin's shared secret."),
                _ => new ScrobbleException($"Last.fm: {message}"),
            };
        }
        if (!response.IsSuccessStatusCode || body is null)
            throw new ScrobbleException($"Last.fm answered {(int)response.StatusCode}.");
        return body;
    }

    /// <summary>Last.fm's signature: MD5 of every parameter's name and value, sorted by name, then the shared secret.</summary>
    public static string LastFmSignature(IReadOnlyDictionary<string, string> parameters, string secret)
    {
        var text = new StringBuilder();
        foreach (var (name, value) in parameters.Where(p => p.Key is not ("format" or "callback")).OrderBy(p => p.Key, StringComparer.Ordinal))
            text.Append(name).Append(value);
        text.Append(secret);
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
