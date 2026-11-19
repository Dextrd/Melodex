using Melodex.Client;
using Melodex.Core.Access;
using Melodex.Core.Import;
using Melodex.Core.Library;

namespace Melodex.Core.Api;

public static class UpgradeEndpoints
{
    public static RouteGroupBuilder MapUpgradeEndpoints(this RouteGroupBuilder api)
    {
        var upgrades = api.MapGroup("/upgrades").WithTags("Upgrades");

        upgrades.MapGet("", (Upgrades service, CancellationToken ct) => service.StatusAsync(ct))
            .WithName("GetUpgrades")
            .WithSummary("Upgrades' settings, how many songs are below their cutoff, files replaced by better copies that can be put back, and "
                         + "why the media server may lose stars, plays and playlists across an upgrade, if it may.");

        upgrades.MapPut("/settings", async (UpgradeSettings value, SettingsService s, CancellationToken ct) =>
            {
                var errors = new Dictionary<string, string[]>();
                if (value.AtATime is < 1 or > UpgradeSettings.MostAtATime)
                    errors["atATime"] = [$"Must be between 1 and {UpgradeSettings.MostAtATime}."];
                if (value.HoldDays is < 1 or > 365)
                    errors["holdDays"] = ["Must be between 1 and 365."];
                if (errors.Count > 0)
                    return Results.ValidationProblem(errors);
                // When they run is the schedules' now; what was kept from before stays, as it was.
                var kept = await s.GetUpgradesAsync(ct);
                await s.SaveUpgradesAsync(value with
                {
                    Scheduled = kept.Scheduled, Days = kept.Days, From = kept.From, Until = kept.Until, TimeZone = kept.TimeZone, PerRun = null,
                }, ct);
                return Results.NoContent();
            })
            .RequireScope(Scopes.Settings).WithName("SaveUpgradeSettings")
            .WithSummary("How many upgrades a look through the whole library keeps going at a time, and whether replaced files are held or deleted. "
                         + "When looks are scheduled is set with PUT /schedules: Scheduled, Days, From, Until and TimeZone here are read only, and "
                         + "left as they are.");

        upgrades.MapPost("/search", async (FormatScope scope, HttpContext http, Upgrades service, CancellationToken ct) =>
            {
                if (scope.Kind != FormatScopeKind.Library && scope.Id is null)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["id"] = ["An artist, album or song needs its ID."] });
                // The whole library is a look that keeps a few going, as a scheduled one does.
                return Results.Ok(scope.Kind == FormatScopeKind.Library
                    ? await service.StartAsync(Caller.Of(http).Requester, DateTime.UtcNow, ct)
                    : await service.SearchAsync(scope, null, Caller.Of(http).Requester, ct));
            })
            .RequireScope(Scopes.Request).WithName("SearchUpgrades")
            .WithSummary("Ask for upgrades of a song's, an album's or an artist's files below their cutoff. For the whole library, start a look "
                         + "that keeps as many going as the settings say, asking for the next as each finishes, until everything's been tried "
                         + "(or the window upgrades are scheduled in closes, if it started in it); Queued is how many it asked for first. Each is a request on the Activity page.")
            .Produces<UpgradeSearch>();

        upgrades.MapGet("/run", async (Upgrades service, CancellationToken ct) =>
                await service.RunAsync(ct) is { } run ? Results.Ok(run) : Results.NoContent())
            .WithName("GetUpgradeRun").WithSummary("The look through the library for upgrades that's going, or nothing when none is.")
            .Produces<UpgradeRunInfo>().Produces(StatusCodes.Status204NoContent);

        upgrades.MapPost("/stop", async (Upgrades service, CancellationToken ct) =>
                await service.StopAsync(ct) ? Results.NoContent() : Results.NotFound())
            .RequireScope(Scopes.Request).WithName("StopUpgrades")
            .WithSummary("Stop the look through the library for upgrades that's going; those it asked for that haven't started are cancelled.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);

        upgrades.MapGet("/held", (Guid? trackId, Upgrades service, CancellationToken ct) => service.HeldAsync(trackId, ct))
            .WithName("GetHeldFiles").WithSummary("Files replaced by better copies that can be put back, of one song or all, newest first.");

        upgrades.MapPost("/held/{id:guid}/restore", async (Guid id, bool? never, Upgrades service, CancellationToken ct) =>
            {
                try
                {
                    return await service.RestoreAsync(id, never ?? false, ct) ? Results.NoContent() : Results.NotFound();
                }
                catch (Exception e) when (e is ImportException or HttpRequestException)
                {
                    return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict, title: "Couldn't put it back");
                }
            })
            .RequireScope(Scopes.Request).WithName("RestoreHeldFile")
            .WithSummary("Put a replaced file back in place of the copy that replaced it, which is deleted; with never, set its song never to be upgraded.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        upgrades.MapDelete("/held/{id:guid}", async (Guid id, Upgrades service, CancellationToken ct) =>
                await service.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .RequireScope(Scopes.Request).WithName("DeleteHeldFile").WithSummary("Delete a replaced file now, instead of when its time is up.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);

        api.MapPost("/library/files/{id:guid}/replace", async (Guid id, HttpContext http, Upgrades service, Requests.RequestService requests, CancellationToken ct) =>
                await service.AskToReplaceAsync(id, Caller.Of(http).Requester, ct) is { } request
                    ? Results.Accepted(null, await requests.ToInfoAsync(request, ct))
                    : Results.NotFound())
            .WithTags("Library").RequireScope(Scopes.Request).WithName("ReplaceFile")
            .WithSummary("Fetch a new file for a matched song in place of this one, better or not, passing over the one you have: for a song "
                         + "fetched as the wrong recording. The song keeps its place in the library and in playlists, and the old file is held "
                         + "or deleted as upgrades' are. Returns the request, or the one going for the song already.")
            .Produces<RequestInfo>(StatusCodes.Status202Accepted).Produces(StatusCodes.Status404NotFound);

        return api;
    }
}
