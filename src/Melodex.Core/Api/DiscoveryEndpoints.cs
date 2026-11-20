using Melodex.Client;
using Melodex.Core.Access;
using Melodex.Core.Listening;

namespace Melodex.Core.Api;

/// <summary>Music related to what someone plays (#3), from the discovery plugins in Settings → Plugins.</summary>
public static class DiscoveryEndpoints
{
    public static RouteGroupBuilder MapDiscoveryEndpoints(this RouteGroupBuilder api)
    {
        var discovery = api.MapGroup("").WithTags("Discovery");

        discovery.MapGet("/discovery", (DiscoveryService service, CancellationToken ct) => service.StatusAsync(ct))
            .RequireScope(Scopes.Read)
            .WithName("GetDiscovery")
            .WithSummary("What the discovery plugins that are on can find: similar artists, similar songs, recommendations.")
            .Produces<DiscoveryStatus>();

        discovery.MapGet("/artists/{id:guid}/similar", async (Guid id, DiscoveryService service, CancellationToken ct, int take = 20) =>
                await service.SimilarArtistsAsync(id, take, ct) is { } similar ? Results.Ok(similar) : Results.NotFound())
            .RequireScope(Scopes.Read)
            .WithName("ListSimilarArtists")
            .WithSummary($"Artists like this one, the closest first, combined from the discovery plugins (at most {DiscoveryService.MaxTake}). Those "
                         + "matched to the catalogue have its ID; the rest only their name.")
            .Produces<IReadOnlyList<SimilarArtist>>().Produces(StatusCodes.Status404NotFound);

        discovery.MapGet("/tracks/{id:guid}/similar", async (Guid id, DiscoveryService service, CancellationToken ct, int take = 20) =>
                await service.SimilarSongsAsync(id, take, ct) is { } similar ? Results.Ok(similar) : Results.NotFound())
            .RequireScope(Scopes.Read)
            .WithName("ListSimilarSongs")
            .WithSummary($"Songs like this one, the closest first, combined from the discovery plugins (at most {DiscoveryService.MaxTake}). Those "
                         + "matched to the catalogue have its ID, and say whether you have them.")
            .Produces<IReadOnlyList<DiscoveredSong>>().Produces(StatusCodes.Status404NotFound);

        discovery.MapGet("/discovery/recommendations", async (HttpContext http, DiscoveryService service, CancellationToken ct, int take = 50) =>
                Caller.Of(http).UserId is { } user
                    ? Results.Ok(await service.RecommendAsync(user, take, ct))
                    : Results.Problem("Recommendations are for a user, and this key doesn't belong to one: make a new one.",
                        statusCode: StatusCodes.Status403Forbidden, title: "No user"))
            .RequireScope(Scopes.Read)
            .WithName("ListRecommendations")
            .WithSummary($"Songs recommended for you, the best first (at most {DiscoveryService.MaxTake}): from what the discovery plugins know of "
                         + "you through your linked ListenBrainz and Last.fm accounts, and what you've played lately.")
            .Produces<IReadOnlyList<DiscoveredSong>>();

        return api;
    }
}
