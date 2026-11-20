using Melodex.Client;
using Melodex.Core.Access;
using Melodex.Core.Listening;

namespace Melodex.Core.Api;

/// <summary>Each person's stars and ratings, and their play queue (#7): the same ones Subsonic players see.</summary>
public static class MarkEndpoints
{
    public static RouteGroupBuilder MapMarkEndpoints(this RouteGroupBuilder api)
    {
        var marks = api.MapGroup("/marks").WithTags("Stars and ratings").RequireScope(Scopes.Read);

        marks.MapGet("", async (HttpContext http, MarkService service, CancellationToken ct) =>
                Owner(http) is { } user ? Results.Ok(await service.ListAsync(user, ct)) : NoOwner())
            .WithName("ListMarks")
            .WithSummary("Your stars and ratings on songs, albums and artists, the latest first.")
            .Produces<IReadOnlyList<ItemMark>>();

        marks.MapPut("/{kind}/{id:guid}", async (string kind, Guid id, SetMark body, HttpContext http, MarkService service, CancellationToken ct) =>
            {
                if (Owner(http) is not { } user)
                    return NoOwner();
                if (!Enum.TryParse<MarkKind>(kind, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["kind"] = ["Choose song, album or artist."] });
                if (body.Rating is < 0 or > 5)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["rating"] = ["A rating is 1 to 5, or 0 to take it away."] });
                return await service.SetAsync(user, parsed, id, body, ct) is { } mark ? Results.Ok(mark) : Results.NoContent();
            })
            .WithName("SetMark")
            .WithSummary("Star or unstar a song (by its track ID), album or artist, or rate it 1 to 5 (0 takes the rating away). What's "
                         + "left out stays as it was. No content once it has neither a star nor a rating.")
            .Produces<ItemMark>().Produces(StatusCodes.Status204NoContent).ProducesValidationProblem();

        var queue = api.MapGroup("/player/queue").WithTags("Stars and ratings").RequireScope(Scopes.Listen);

        queue.MapGet("", async (HttpContext http, MarkService service, CancellationToken ct) =>
                Owner(http) is not { } user ? NoOwner() : await service.GetQueueAsync(user, ct) is { } saved ? Results.Ok(saved) : Results.NoContent())
            .WithName("GetPlayQueue")
            .WithSummary("Your play queue, as the web player or a Subsonic player left it; no content if there's none.")
            .Produces<PlayQueue>().Produces(StatusCodes.Status204NoContent);

        queue.MapPut("", async (SavePlayQueue body, HttpContext http, MarkService service, CancellationToken ct) =>
            {
                if (Owner(http) is not { } user)
                    return NoOwner();
                try
                {
                    return await service.SaveQueueAsync(user, body, ct) is { } saved ? Results.Ok(saved) : Results.NoContent();
                }
                catch (ArgumentException e)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["items"] = [e.Message.Split(" (Parameter")[0]] });
                }
            })
            .WithName("SavePlayQueue")
            .WithSummary($"Keep your play queue, in place of the one before, for the web player and Subsonic players to pick up. At most "
                         + $"{MarkService.MaxQueue} songs; an empty queue clears it.")
            .Produces<PlayQueue>().Produces(StatusCodes.Status204NoContent).ProducesValidationProblem();

        return api;
    }

    private static Guid? Owner(HttpContext http) => Caller.Of(http).UserId;

    private static IResult NoOwner() =>
        Results.Problem("Stars, ratings and play queues belong to a user, and this key doesn't: make a new one.",
            statusCode: StatusCodes.Status403Forbidden, title: "No user");
}
