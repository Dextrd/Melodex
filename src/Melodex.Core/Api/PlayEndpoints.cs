using Microsoft.EntityFrameworkCore;
using Melodex.Client;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Scrobbling;
using Melodex.Core.Access;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Listening;
using Melodex.Core.Plugins;

namespace Melodex.Core.Api;

/// <summary>Each person's listening history (#2), and the scrobbling accounts their plays are sent to (#236).</summary>
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
                         + "on to your linked scrobbling accounts. The same song again within 30 seconds is taken for the same play, "
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
            .WithSummary("Take a play out of your history. One already sent to a scrobbling service stays there.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);

        var scrobbling = api.MapGroup("/account/scrobbling").WithTags("Listening history").RequireScope(Scopes.SignedIn);

        scrobbling.MapGet("", async (HttpContext http, MelodexDbContext db, ScrobblingPlugins plugins, CancellationToken ct) =>
                Owner(http) is { } user ? Results.Ok(await ServicesAsync(db, plugins, user, ct)) : NoOwner())
            .WithName("GetScrobbling")
            .WithSummary("The scrobbling services the admin set up, such as ListenBrainz and Last.fm, and your account on each, if you linked one.")
            .Produces<IReadOnlyList<ScrobbleService>>();

        scrobbling.MapPost("/{id:guid}/link-page", async (Guid id, ScrobbleLinkPageRequest body, HttpContext http, MelodexDbContext db,
                ScrobblingPlugins plugins, CancellationToken ct) =>
            {
                if (Owner(http) is null)
                    return NoOwner();
                if (await PluginAsync(db, plugins, id, ct) is not { } found)
                    return Results.NotFound();
                if (!Uri.TryCreate(body.Callback, UriKind.Absolute, out var callback) || callback.Scheme is not ("http" or "https"))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["callback"] = ["That isn't a web address."] });
                if (!found.Manifest.Capabilities.Contains(ScrobblingCapabilities.LinkWeb))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["callback"] = [$"{found.Manifest.Name} is linked with a token."] });
                try
                {
                    return Results.Ok(new ScrobbleLinkPage((await found.Plugin.LinkPageAsync(callback, ct)).ToString()));
                }
                catch (Exception e) when (e is ScrobbleLinkException or HttpRequestException or RemotePluginException)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["callback"] = [e.Message] });
                }
            })
            .WithName("GetScrobbleLinkPage")
            .WithSummary("Where to send you to allow Melodex on a service that's linked on its own page, such as Last.fm. It sends you back to the "
                         + "callback with a token in the address, as token, which linking takes.")
            .Produces<ScrobbleLinkPage>().ProducesValidationProblem().Produces(StatusCodes.Status404NotFound);

        scrobbling.MapPut("/{id:guid}", async (Guid id, LinkScrobbling body, HttpContext http, MelodexDbContext db, ScrobblingPlugins plugins,
                Scrobbler scrobbler, CancellationToken ct) =>
            {
                if (Owner(http) is not { } user)
                    return NoOwner();
                if (await PluginAsync(db, plugins, id, ct) is not { } found)
                    return Results.NotFound();
                if (string.IsNullOrWhiteSpace(body.Token))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["token"] = ["Paste the token to link with."] });
                try
                {
                    var linked = await found.Plugin.LinkAsync(body.Token.Trim(), ct);
                    return Results.Ok(await LinkAsync(db, scrobbler, user, id, linked, ct));
                }
                catch (Exception e) when (e is ScrobbleLinkException or HttpRequestException or TaskCanceledException or RemotePluginException
                                              && !ct.IsCancellationRequested)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["token"] = [e is TaskCanceledException ? "It didn't answer in time." : e.Message] });
                }
            })
            .WithName("LinkScrobbling")
            .WithSummary("Link your account on a scrobbling service with a token: one you pasted, such as a ListenBrainz user token, or the one the "
                         + "service sent you back with. Plays from now on are sent there; linking again sends the ones that waited.")
            .Produces<ScrobbleLink>().ProducesValidationProblem().Produces(StatusCodes.Status404NotFound);

        scrobbling.MapDelete("/{id:guid}", async (Guid id, HttpContext http, MelodexDbContext db, CancellationToken ct) =>
            {
                if (Owner(http) is not { } user)
                    return NoOwner();
                return await db.ScrobbleAccounts.Where(a => a.UserId == user && a.PluginInstanceId == id).ExecuteDeleteAsync(ct) > 0
                    ? Results.NoContent() : Results.NotFound();
            })
            .WithName("UnlinkScrobbling")
            .WithSummary("Stop sending your plays to a scrobbling service. Plays not sent yet aren't sent.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);

        return api;
    }

    /// <summary>A scrobbling plugin that's on, by its set-up ID.</summary>
    private static async Task<(IScrobblingPlugin Plugin, PluginManifest Manifest)?> PluginAsync(MelodexDbContext db, ScrobblingPlugins plugins, Guid id,
        CancellationToken ct) =>
        await db.PluginInstances.FirstOrDefaultAsync(p => p.Id == id && p.Type == PluginType.Scrobbling && p.Enabled, ct) is { } instance
            ? await plugins.GetAsync(instance, ct)
            : null;

    private static async Task<IReadOnlyList<ScrobbleService>> ServicesAsync(MelodexDbContext db, ScrobblingPlugins plugins, Guid user, CancellationToken ct)
    {
        var accounts = await db.ScrobbleAccounts.AsNoTracking().Where(a => a.UserId == user).ToDictionaryAsync(a => a.PluginInstanceId, ct);
        var waiting = await db.ScrobbleSends.Where(s => s.Account.UserId == user).GroupBy(s => s.AccountId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return (await plugins.GetAsync(ct)).Select(p => new ScrobbleService(
                p.Instance.Id, p.Instance.Name, p.Instance.PluginId,
                p.Manifest.Capabilities.Contains(ScrobblingCapabilities.LinkWeb) ? ScrobbleLinkKind.Web : ScrobbleLinkKind.Token,
                p.Manifest.Homepage, p.Manifest.Description,
                accounts.TryGetValue(p.Instance.Id, out var account) ? Link(account, waiting.GetValueOrDefault(account.Id)) : null))
            .ToList();
    }

    private static ScrobbleLink Link(ScrobbleAccount a, int waiting) =>
        new(a.Username, DateTime.SpecifyKind(a.LinkedAt, DateTimeKind.Utc),
            a.LastSentAt is { } sent ? DateTime.SpecifyKind(sent, DateTimeKind.Utc) : null, a.Error, waiting);

    /// <summary>Links an account, or links it again: plays held while it didn't work are sent now.</summary>
    private static async Task<ScrobbleLink> LinkAsync(MelodexDbContext db, Scrobbler scrobbler, Guid user, Guid plugin, LinkedAccount linked,
        CancellationToken ct)
    {
        var account = await db.ScrobbleAccounts.FirstOrDefaultAsync(a => a.UserId == user && a.PluginInstanceId == plugin, ct);
        if (account is null)
        {
            account = new ScrobbleAccount { UserId = user, PluginInstanceId = plugin, Username = linked.Name, Secret = scrobbler.Protect(linked.Secret) };
            db.ScrobbleAccounts.Add(account);
        }
        else
        {
            account.Username = linked.Name;
            account.Secret = scrobbler.Protect(linked.Secret);
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
