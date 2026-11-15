using System.Net;
using Melodex.Client;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>The wishlist, through the client contract: added to, listed, taken off, and left by what the library gets.</summary>
[Collection(RequestCollection.Name)]
public sealed class WishlistTests(RequestFixture fixture) : IAsyncLifetime
{
    // Albums other tests fetch or listen to, so these stay out of their way: these come from the other end of the list.
    private static readonly string[] Taken = ["Human After All", "Third", "Homework", "Dummy", "Portishead", "Roseland NYC Live", "Magic Doors", "Random Access Memories"];

    private MelodexClient Api => fixture.Api;

    public async Task InitializeAsync()
    {
        if (!TestLibrary.Exists)
            return;
        await fixture.SettleRequestsAsync();
        await fixture.RemoveSourcesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [TestLibraryFact]
    public async Task Songs_and_albums_go_on_it_once_come_off_it_and_leave_once_the_library_has_them()
    {
        var albums = fixture.MissingAlbums.Where(a => !Taken.Contains(a.Title) && a.TotalTracks >= 3).Reverse().Take(2).ToList();
        var song = (await Api.GetAlbumAsync(albums[0].Id))!.Tracks.First(t => t.Status == TrackStatus.Missing);
        var album = albums[1];
        try
        {
            // Nothing can fetch, so the app offers it in place of Listen now.
            Assert.False((await Api.GetWishlistAsync()).CanFetch);

            await Api.AddToWishlistAsync(new AddToWishlist(RequestKind.Track, song.TrackId));
            await Api.AddToWishlistAsync(new AddToWishlist(RequestKind.Album, album.Id));
            await Api.AddToWishlistAsync(new AddToWishlist(RequestKind.Album, album.Id));
            var wished = (await Api.GetWishlistAsync()).Items;
            Assert.Equal([album.Id, song.TrackId], wished.Select(i => i.TargetId));
            Assert.Equal((album.Title, RequestKind.Album), (wished[0].Title, wished[0].Kind));
            Assert.Equal((albums[0].Id, albums[0].Title), (wished[1].AlbumId!.Value, wished[1].AlbumTitle));
            Assert.NotNull(wished[1].CoverUrl);

            // Only what the catalogue knows, and only songs and albums.
            await Assert.ThrowsAsync<MelodexApiException>(() => Api.AddToWishlistAsync(new AddToWishlist(RequestKind.Track, Guid.NewGuid())));
            await Assert.ThrowsAsync<MelodexApiException>(() => Api.AddToWishlistAsync(new AddToWishlist(RequestKind.Artist, album.ArtistId)));

            // Taken off, once.
            await Api.RemoveFromWishlistAsync(album.Id);
            Assert.Equal([song.TrackId], (await Api.GetWishlistAsync()).Items.Select(i => i.TargetId));
            var again = await Assert.ThrowsAsync<MelodexApiException>(() => Api.RemoveFromWishlistAsync(album.Id));
            Assert.Equal(HttpStatusCode.NotFound, again.Status);

            // What the library has leaves it by itself: here a song it had all along, so nothing's fetched.
            var owned = (await Api.GetLibraryTracksAsync()).First(t => !t.Local);
            await Api.AddToWishlistAsync(new AddToWishlist(RequestKind.Track, owned.TrackId));
            Assert.Equal([song.TrackId], (await Api.GetWishlistAsync()).Items.Select(i => i.TargetId));

            // Once something can fetch, the app offers Listen now and Get again.
            await fixture.AddSourceAsync(new FakeSource("wishes"));
            Assert.True((await Api.GetWishlistAsync()).CanFetch);
        }
        finally
        {
            foreach (var id in new[] { song.TrackId, album.Id })
                try { await Api.RemoveFromWishlistAsync(id); } catch (MelodexApiException) { }
        }
    }
}
