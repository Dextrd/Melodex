using Melodex.Client;
using Melodex.Contracts.Catalogue;

namespace Melodex.Core.Tests;

public sealed class AlbumStatusTests
{
    private static AlbumSummary Album(AlbumStatus status, int? owned, int? total) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Sea Ranch", "Rami", AlbumType.Album, [], "2017-12-05", status, false, false, owned, total, []);

    [Fact]
    public void A_missing_album_with_some_songs_on_other_albums_is_partly_covered()
    {
        Assert.True(Album(AlbumStatus.Missing, 1, 10).PartlyCovered);
        Assert.False(Album(AlbumStatus.Missing, 0, 10).PartlyCovered);
        Assert.False(Album(AlbumStatus.Missing, null, null).PartlyCovered);
        // Owned or covered albums say so instead.
        Assert.False(Album(AlbumStatus.Partial, 3, 10).PartlyCovered);
        Assert.False(Album(AlbumStatus.Covered, 10, 10).PartlyCovered);
    }
}
