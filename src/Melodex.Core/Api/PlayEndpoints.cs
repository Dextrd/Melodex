using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Core.Access;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Listening;

namespace Melodex.Core.Api;

/// <summary>Each person's listening history (#2), and the ListenBrainz and Last.fm accounts their plays are sent to.</summary>
public static class PlayEndpoints
{
    public static RouteGroupBuilder MapPlayEndpoints(this RouteGroupBuilder api)
    {
        var plays = api.MapGroup("/plays").WithTags("Listening history");

        plays.MapGet("", async (HttpContext http, PlayService service, CancellationToken ct, int skip = 0, int take = 50) =>
                Owner(http) is { } user ? Results.Ok(await service.ListAsync(user, skip, take, ct)) : NoOwner())
            .RequireScope(Scopes.Read)
            .WithName("ListPlays")
            .WithSummary("Your plays, the latest first, a page at a time (at most 500).")
            .Produces<PlayPage>();

        plays.MapGet("/top/{kind}", async (string kind, HttpContext http, PlayService service, CancellationToken ct, PlayPeriod period = PlayPeriod.All,
                int take = 50) =>
            {
                if (Owner(http) is not { } user)
                    return NoOwner();
                if (!Enum.TryParse<TopKind>(kind.TrimEnd('s'), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["kind"] = ["Choose songs, albums or artists."] });
                return Results.Ok(await service.TopAsync(user, parsed, period, take, ct));
            })
            .RequireScope(Scopes.Read)
            .WithName("ListMostPlayed")
            .WithSummary("Your most played songs, albums or artists over the last week, month or year, or all time; the most first.")
            .Produces<IReadOnlyList<TopItem>>().ProducesValidationProblem();

        plays.MapPost("", async (RecordPlay body, HttpContext http, PlayService service, CancellationToken ct) =>
            {
                if (Owner(http) is not { } user)
                    return NoOwner();
                try
                {
                    return Results.Ok(await service.RecordAsync(user, body, ct));
                }
                catch (ArgumentException e)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["play"] = [e.Message.Split(" (Parameter")[0]] });
                }
            })
            .RequireScope(Scopes.Listen)
            .WithName("RecordPlay")
            .WithSummary("Record a song you played, once it's played for half its length or four minutes, whichever comes first. It's sent "
                         + "on to your linked ListenBrainz and Last.fm accounts. The same song again within 30 seconds is taken for the same play, "
                         + "and the one already recorded comes back.")
            .Produces<PlayRecord>().ProducesValidationProblem();

        plays.MapPut("/now", async (RecordPlay body, HttpContext http, PlayService service, Scrobbler scrobbler, MelodexDbContext db,
                CancellationToken ct) =>
            {
                if (Owner(http) is not { } user)
                    return NoOwner();
                try
                {
                    var play = await service.ToPlayAsync(user, body, ct);
                    var username = (await db.Users.FindAsync([user], ct))?.Username ?? "";
                    // Linked accounts are told in the background, so a slow one doesn't hold up the player.
                    _ = Task.Run(() => scrobbler.StartedAsync(user, username, play, CancellationToken.None), CancellationToken.None);
                    return Results.NoContent();
                }
                catch (ArgumentException e)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["play"] = [e.Message.Split(" (Parameter")[0]] });
                }
            })
            .RequireScope(Scopes.Listen)
            .WithName("SetNowPlaying")
            .WithSummary("Say what you've started playing: Subsonic players' now playing shows it, and your linked accounts are told.")
            .Produces(StatusCodes.Status204NoContent).ProducesValidationProblem();

        plays.MapDelete("/{id:guid}", async (Guid id, HttpContext http, PlayService service, CancellationToken ct) =>
                Owner(http) is not { } user ? NoOwner() : await service.DeleteAsync(user, id, ct) ? Results.NoContent() : Results.NotFound())
            .RequireScope(Scopes.Listen)
            .WithName("DeletePlay")
            .WithSummary("Take a play out of your history. One already sent to ListenBrainz or Last.fm stays there.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);

        var scrobbling = api.MapGroup("/account/scrobbling").WithTags("Listening history").RequireScope(Scopes.SignedIn);

        scrobbling.MapGet("", async (HttpContext http, MelodexDbContext db, SettingsService settings, CancellationToken ct) =>
            {
                if (Owner(http) is not { } user)
                    return NoOwner();
                return Results.Ok(new ScrobbleStatus(await LinksAsync(db, user, ct), await LastFmKeyAsync(settings, ct)));
            })
            .WithName("GetScrobbling")
            .WithSummary("Your linked ListenBrainz and Last.fm accounts, and the API key to link Last.fm with, if the admin has set one.")
            .Produces<ScrobbleStatus>();

        scrobbling.MapPut("/listenbrainz", async (LinkListenBrainz body, HttpContext http, MelodexDbContext db, Scrobbler scrobbler, CancellationToken ct) =>
            {
                if (Owner(http) is not { } user)
                    return NoOwner();
                try
                {
                    var name = await scrobbler.ListenBrainzUserAsync(body.Token, ct);
                    return Results.Ok(await LinkAsync(db, scrobbler, user, ScrobbleService.ListenBrainz, name, body.Token.Trim(), ct));
                }
                catch (ScrobbleException e)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["token"] = [e.Message] });
                }
            })
            .WithName("LinkListenBrainz")
            .WithSummary("Link your ListenBrainz account with the user token from your ListenBrainz settings. Plays from now on are sent there.")
            .Produces<ScrobbleLink>().ProducesValidationProblem();

        scrobbling.MapPut("/lastfm", async (LinkLastFm body, HttpContext http, MelodexDbContext db, Scrobbler scrobbler, CancellationToken ct) =>
            {
                if (Owner(http) is not { } user)
                    return NoOwner();
                try
                {
                    var (name, session) = await scrobbler.LastFmSessionAsync(body.Token, ct);
                    return Results.Ok(await LinkAsync(db, scrobbler, user, ScrobbleService.LastFm, name, session, ct));
                }
                catch (ScrobbleException e)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["token"] = [e.Message] });
                }
            })
            .WithName("LinkLastFm")
            .WithSummary($"Link your Last.fm account: send someone to {Scrobbler.LastFmAuth}?api_key=KEY&cb=URL, and Last.fm sends them back to "
                         + "the URL with a token, which this takes. Plays from now on are sent there.")
            .Produces<ScrobbleLink>().ProducesValidationProblem();

        scrobbling.MapDelete("/{service}", async (string service, HttpContext http, MelodexDbContext db, CancellationToken ct) =>
            {
                if (Owner(http) is not { } user)
                    return NoOwner();
                if (!Enum.TryParse<ScrobbleService>(service, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                    return Results.NotFound();
                return await db.ScrobbleAccounts.Where(a => a.UserId == user && a.Service == parsed).ExecuteDeleteAsync(ct) > 0
                    ? Results.NoContent() : Results.NotFound();
            })
            .WithName("UnlinkScrobbling")
            .WithSummary("Stop sending your plays to ListenBrainz (listenbrainz) or Last.fm (lastfm). Plays not sent yet aren't sent.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);

        var settings = api.MapGroup("/settings/scrobbling").WithTags("Settings").RequireScope(Scopes.Settings);

        settings.MapGet("", async (SettingsService s, CancellationToken ct) =>
            {
                var saved = await s.GetScrobblingAsync(ct);
                return new ScrobbleSettingsInfo(saved.LastFmApiKey, !string.IsNullOrEmpty(saved.LastFmSecret));
            })
            .WithName("GetScrobblingSettings")
            .WithSummary("The Last.fm API account people's plays are sent with. The shared secret is never shown.");

        settings.MapPut("", async (SaveScrobbleSettings body, SettingsService s, Scrobbler scrobbler, CancellationToken ct) =>
            {
                var saved = await s.GetScrobblingAsync(ct);
                saved.LastFmApiKey = string.IsNullOrWhiteSpace(body.LastFmApiKey) ? null : body.LastFmApiKey.Trim();
                if (body.LastFmSecret is { } secret)
                    saved.LastFmSecret = secret.Trim() is { Length: > 0 } trimmed ? scrobbler.Protect(trimmed) : null;
                await s.SaveScrobblingAsync(saved, ct);
                scrobbler.Wake();
                return Results.NoContent();
            })
            .WithName("SaveScrobblingSettings")
            .WithSummary("Set the Last.fm API key and shared secret (from last.fm/api/account/create) that people link Last.fm with. A secret "
                         + "left out stays as it was; an empty one takes it away.")
            .Produces(StatusCodes.Status204NoContent);

        return api;
    }

    private static async Task<string?> LastFmKeyAsync(SettingsService settings, CancellationToken ct) =>
        (await settings.GetScrobblingAsync(ct)) is { LastFmApiKey: { Length: > 0 } key, LastFmSecret: { Length: > 0 } } ? key : null;

    private static async Task<IReadOnlyList<ScrobbleLink>> LinksAsync(MelodexDbContext db, Guid user, CancellationToken ct)
    {
        var accounts = await db.ScrobbleAccounts.AsNoTracking().Where(a => a.UserId == user).OrderBy(a => a.Service).ToListAsync(ct);
        var waiting = await db.ScrobbleSends.Where(s => s.Account.UserId == user).GroupBy(s => s.AccountId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return accounts.Select(a => Link(a, waiting.GetValueOrDefault(a.Id))).ToList();
    }

    private static ScrobbleLink Link(ScrobbleAccount a, int waiting) =>
        new(a.Service, a.Username, DateTime.SpecifyKind(a.LinkedAt, DateTimeKind.Utc),
            a.LastSentAt is { } sent ? DateTime.SpecifyKind(sent, DateTimeKind.Utc) : null, a.Error, waiting);

    /// <summary>Links an account, or links it again: plays held while it didn't work are sent now.</summary>
    private static async Task<ScrobbleLink> LinkAsync(MelodexDbContext db, Scrobbler scrobbler, Guid user, ScrobbleService service, string username,
        string secret, CancellationToken ct)
    {
        var account = await db.ScrobbleAccounts.FirstOrDefaultAsync(a => a.UserId == user && a.Service == service, ct);
        if (account is null)
        {
            account = new ScrobbleAccount { UserId = user, Service = service, Username = username, Secret = scrobbler.Protect(secret) };
            db.ScrobbleAccounts.Add(account);
        }
        else
        {
            account.Username = username;
            account.Secret = scrobbler.Protect(secret);
            account.LinkedAt = DateTime.UtcNow;
            account.Refused = false;
            account.Error = null;
            await db.ScrobbleSends.Where(s => s.AccountId == account.Id).ExecuteUpdateAsync(u => u.SetProperty(s => s.NextAttemptAt, DateTime.UtcNow), ct);
        }
        await db.SaveChangesAsync(ct);
        scrobbler.Wake();
        return Link(account, await db.ScrobbleSends.CountAsync(s => s.AccountId == account.Id, ct));
    }

    private static Guid? Owner(HttpContext http) => Caller.Of(http).UserId;

    private static IResult NoOwner() =>
        Results.Problem("Listening history belongs to a user, and this key doesn't: make a new one.",
            statusCode: StatusCodes.Status403Forbidden, title: "No user");
}
