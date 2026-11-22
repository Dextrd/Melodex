using Melodex.Client;
using Melodex.Core.Access;
using Melodex.Core.Listening;

namespace Melodex.Core.Api;

/// <summary>For you (#5): mixes made for each person, and suggestions from what they play.</summary>
public static class ForYouEndpoints
{
    public static RouteGroupBuilder MapForYouEndpoints(this RouteGroupBuilder api)
    {
        var mixes = api.MapGroup("/mixes").WithTags("For you");

        mixes.MapGet("", async (HttpContext http, MixService service, CancellationToken ct) =>
                Owner(http) is { } user ? Results.Ok(await service.ListAsync(user, ct)) : NoOwner())
            .RequireScope(Scopes.Listen)
            .WithName("ListMixes")
            .WithSummary($"Your mixes: up to {MixService.Dailies} daily ones around groups of artists you play, and a weekly one of songs you don't have. "
                         + "Made when first asked for each day, and each week from Monday, so the first ask then takes a while.")
            .Produces<IReadOnlyList<MixSummary>>();

        mixes.MapGet("/{id:guid}", async (Guid id, HttpContext http, MixService service, CancellationToken ct) =>
                Owner(http) is not { } user ? NoOwner()
                : await service.GetAsync(user, id, ct) is { } mix ? Results.Ok(mix)
                : Results.NotFound())
            .RequireScope(Scopes.Listen)
            .WithName("GetMix")
            .WithSummary("One of your mixes with its songs, in order. Those you don't have are to fetch to listen to.")
            .Produces<MixDetail>().Produces(StatusCodes.Status404NotFound);

        mixes.MapPost("/{id:guid}/keep", async (Guid id, KeepMix body, HttpContext http, MixService service, CancellationToken ct) =>
                Owner(http) is not { } user ? NoOwner()
                : await service.KeepAsync(user, id, body, ct) is { } playlist ? Results.Ok(playlist)
                : Results.NotFound())
            .RequireScope(Scopes.Listen)
            .WithName("KeepMix")
            .WithSummary("Copies a mix into a new playlist of yours, by the name given, or the mix's name and the day it was made. "
                         + "It's an ordinary playlist, synced to the media server like the others; while the mix lasts, it's the mix's `kept`, "
                         + "and keeping it again gives the same playlist.")
            .Produces<PlaylistDetail>().Produces(StatusCodes.Status404NotFound);

        mixes.MapDelete("/{id:guid}/keep", async (Guid id, HttpContext http, MixService service, CancellationToken ct) =>
                Owner(http) is not { } user ? NoOwner()
                : await service.UnkeepAsync(user, id, ct) ? Results.NoContent()
                : Results.NotFound())
            .RequireScope(Scopes.Listen)
            .WithName("UnkeepMix")
            .WithSummary("Deletes the playlist a mix was kept as, from the media server too, changed since or not. 404 when it wasn't kept, or the mix is gone.")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);

        var forYou = api.MapGroup("/for-you").WithTags("For you");

        forYou.MapGet("/artists", async (HttpContext http, ForYouService service, CancellationToken ct) =>
                Owner(http) is { } user ? Results.Ok(await service.ArtistsAsync(user, ct)) : NoOwner())
            .RequireScope(Scopes.Listen)
            .WithName("ListArtistsForYou")
            .WithSummary("Artists like those you play most that you have nothing of, closest first.")
            .Produces<IReadOnlyList<SimilarArtist>>();

        forYou.MapGet("/albums", async (HttpContext http, ForYouService service, CancellationToken ct) =>
                Owner(http) is { } user ? Results.Ok(await service.AlbumsAsync(user, ct)) : NoOwner())
            .RequireScope(Scopes.Listen)
            .WithName("ListAlbumsForYou")
            .WithSummary("Albums like those you play most that you don't have, closest first.")
            .Produces<IReadOnlyList<SimilarAlbum>>();

        forYou.MapGet("/your-albums", async (HttpContext http, ForYouService service, CancellationToken ct) =>
                Owner(http) is { } user ? Results.Ok(await service.YourAlbumsAsync(user, ct)) : NoOwner())
            .RequireScope(Scopes.Listen)
            .WithName("ListYourAlbumsToGoBackTo")
            .WithSummary("Albums of your own you haven't played in two months, the most played first; and, while those are few, some you've never played.")
            .Produces<IReadOnlyList<AlbumSummary>>();

        return api;
    }

    private static Guid? Owner(HttpContext http) => Caller.Of(http).UserId;

    private static IResult NoOwner() =>
        Results.Problem("For you is for a user, and this key doesn't belong to one: make a new one.",
            statusCode: StatusCodes.Status403Forbidden, title: "No user");
}
