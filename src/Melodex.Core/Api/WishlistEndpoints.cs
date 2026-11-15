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
