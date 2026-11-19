using Melodex.Client;
using Melodex.Core.Access;
using Melodex.Core.Wishlists;

namespace Melodex.Core.Api;

public static class WishlistEndpoints
{
    public static RouteGroupBuilder MapWishlistEndpoints(this RouteGroupBuilder api)
    {
        var wishlist = api.MapGroup("/wishlist").WithTags("Wishlist").RequireScope(Scopes.Listen);

        wishlist.MapGet("", async (HttpContext http, WishlistService service, CancellationToken ct) =>
                Owner(http) is { } user ? Results.Ok(await service.GetAsync(user, ct)) : NoOwner())
            .WithName("GetWishlist")
            .WithSummary("Your wishlist, newest first: songs and albums you'd like, less what the library has now, and whether anything can fetch them.")
            .Produces<Wishlist>();

        wishlist.MapPost("", async (AddToWishlist body, HttpContext http, WishlistService service, CancellationToken ct) =>
            {
                if (Owner(http) is not { } user)
                    return NoOwner();
                return await service.AddAsync(user, body, ct) is { } problem
                    ? Results.ValidationProblem(new Dictionary<string, string[]> { ["targetId"] = [problem] })
                    : Results.NoContent();
            })
            .WithName("AddToWishlist")
            .WithSummary("Put a song or album on your wishlist. Adding one already there does nothing more.")
            .Produces(StatusCodes.Status204NoContent).ProducesValidationProblem();

        wishlist.MapPost("/get", async (HttpContext http, Melodex.Core.Library.ScheduledTasks scheduled, CancellationToken ct) =>
            {
                if (Owner(http) is not { } user)
                    return NoOwner();
                try
                {
                    return Results.Ok(new AskedAgain(await scheduled.GetWishlistsAsync(user, ct)));
                }
                catch (Melodex.Core.Library.LibraryBusyException e)
                {
                    return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict, title: "The library is busy");
                }
            })
            .RequireScope(Scopes.Request).WithName("GetWishlistItems")
            .WithSummary("Ask for everything on your wishlist, in your name. Those being fetched already are left as they are; Asked says how many "
                         + "were asked for.")
            .Produces<AskedAgain>().Produces(StatusCodes.Status409Conflict);

        wishlist.MapDelete("/{targetId:guid}", async (Guid targetId, HttpContext http, WishlistService service, CancellationToken ct) =>
                Owner(http) is { } user ? await service.RemoveAsync(user, targetId, ct) ? Results.NoContent() : Results.NotFound() : NoOwner())
            .WithName("RemoveFromWishlist")
            .WithSummary("Take a song or album off your wishlist, by its track or album ID.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);

        return api;
    }

    private static Guid? Owner(HttpContext http) => Caller.Of(http).UserId;

    private static IResult NoOwner() =>
        Results.Problem("A wishlist belongs to a user, and this key doesn't: make a new one.", statusCode: StatusCodes.Status403Forbidden, title: "No user");
}
