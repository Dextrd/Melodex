using System.Net;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Contracts.Catalogue;
using Melodex.Core.Data;

namespace Melodex.Core.Tests.Support;

/// <summary>
/// A little trip-hop catalogue, and a stand-in ListenBrainz that knows it, for radio (#4) and For you (#5): Portishead's
/// Glory Box, not in the library; Massive Attack's Teardrop and Angel, on Mezzanine, in it; and Tricky's Overcome,
/// which isn't.
/// </summary>
public static class TripHop
{
    public const string Portishead = "8f6bd1e4-fbe1-4f50-aa9b-94c450ec0f11";
    public const string MassiveAttack = "10adbe5e-a2c0-4bf3-8249-2b4cbf6e6ca8";
    public const string Tricky = "9a8b6a4a-4d64-4c31-8f5a-7e1b2a1a1a01";
    public const string GloryBox = "3a1c8f6e-21b5-4d43-9a0c-1a1a1a1a1a01";
    public const string Teardrop = "3a1c8f6e-21b5-4d43-9a0c-1a1a1a1a1a02";
    public const string Overcome = "3a1c8f6e-21b5-4d43-9a0c-1a1a1a1a1a03";

    public sealed record Seeded(Guid Portishead, Guid Massive, Guid GloryBox, Guid Teardrop, Guid Angel, Guid Overcome, Guid Mezzanine);

    /// <summary>
    /// Portishead's Glory Box, not in the library; Massive Attack's Teardrop and Angel, on Mezzanine, in it; and Tricky's
    /// Overcome, which isn't.
    /// </summary>
    public static async Task<Seeded> SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        var portishead = new Artist { Name = "Portishead", InLibrary = false };
        var massive = new Artist { Name = "Massive Attack", InLibrary = true };
        var tricky = new Artist { Name = "Tricky" };
        var gloryBox = new Track { Artist = portishead, Title = "Glory Box", ArtistCredit = "Portishead" };
        var teardrop = new Track { Artist = massive, Title = "Teardrop", ArtistCredit = "Massive Attack" };
        var angel = new Track { Artist = massive, Title = "Angel", ArtistCredit = "Massive Attack" };
        var overcome = new Track { Artist = tricky, Title = "Overcome", ArtistCredit = "Tricky" };
        var mezzanine = new Album { Artist = massive, Title = "Mezzanine", ArtistCredit = "Massive Attack", Type = AlbumType.Album };
        var edition = new Edition { Album = mezzanine, Title = "Mezzanine" };
        db.AddRange(portishead, massive, tricky, gloryBox, teardrop, angel, overcome, mezzanine, edition);
        db.EditionTracks.AddRange(new EditionTrack { Edition = edition, Track = angel, Number = "1", Position = 1, Title = "Angel" }, new EditionTrack { Edition = edition, Track = teardrop, Number = "3", Position = 3, Title = "Teardrop" });
        db.Crosswalk.AddRange(
            new ProviderIdMapping { EntityKind = EntityKind.Artist, EntityId = portishead.Id, Provider = Providers.MusicBrainz, Value = Portishead },
            new ProviderIdMapping { EntityKind = EntityKind.Artist, EntityId = massive.Id, Provider = Providers.MusicBrainz, Value = MassiveAttack },
            new ProviderIdMapping { EntityKind = EntityKind.Artist, EntityId = tricky.Id, Provider = Providers.MusicBrainz, Value = Tricky },
            new ProviderIdMapping { EntityKind = EntityKind.Track, EntityId = gloryBox.Id, Provider = Providers.MusicBrainz, Value = GloryBox },
            new ProviderIdMapping { EntityKind = EntityKind.Track, EntityId = teardrop.Id, Provider = Providers.MusicBrainz, Value = Teardrop },
            new ProviderIdMapping { EntityKind = EntityKind.Track, EntityId = overcome.Id, Provider = Providers.MusicBrainz, Value = Overcome });
        db.LibraryFiles.AddRange(
            new LibraryFile { Path = "Massive Attack/Mezzanine/03 Teardrop.flac", TrackId = teardrop.Id, EditionId = edition.Id },
            new LibraryFile { Path = "Massive Attack/Mezzanine/01 Angel.flac", TrackId = angel.Id, EditionId = edition.Id });
        await db.SaveChangesAsync();
        return new Seeded(portishead.Id, massive.Id, gloryBox.Id, teardrop.Id, angel.Id, overcome.Id, mezzanine.Id);
    }

    /// <summary>
    /// Stands in for ListenBrainz: Teardrop and Overcome are like Glory Box, and Glory Box like anything else; Massive
    /// Attack are like Portishead.
    /// </summary>
    public sealed class FakeListenBrainz : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            var query = HttpUtility.ParseQueryString(url.Query);
            object? answer = (url.Host, url.AbsolutePath) switch
            {
                ("labs.api.listenbrainz.org", "/similar-artists/json") => new object[]
                {
                    new { artist_mbid = MassiveAttack, name = "Massive Attack", score = 11000 },
                },
                ("labs.api.listenbrainz.org", "/similar-recordings/json") when query["recording_mbids"] == GloryBox => new object[]
                {
                    new { recording_mbid = Teardrop, recording_name = "Teardrop", artist_credit_name = "Massive Attack", score = 200 },
                    new { recording_mbid = Overcome, recording_name = "Overcome", artist_credit_name = "Tricky", score = 100 },
                },
                ("labs.api.listenbrainz.org", "/similar-recordings/json") => new object[]
                {
                    new { recording_mbid = GloryBox, recording_name = "Glory Box", artist_credit_name = "Portishead", score = 100 },
                },
                ("labs.api.listenbrainz.org", "/acr-lookup/json") => Array.Empty<object>(),
                ("api.listenbrainz.org", "/1/metadata/recording/") => query["recording_mbids"]!.Split(',').ToDictionary(m => m, m => (object)new
                {
                    artist = new
                    {
                        name = Name(m).Artist,
                        artists = new[] { new { artist_mbid = Name(m).ArtistMbid, name = Name(m).Artist } },
                    },
                    recording = new { name = Name(m).Title, length = 300000 },
                }),
                _ => null,
            };
            return Task.FromResult(answer is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(answer), System.Text.Encoding.UTF8, "application/json"),
                });
        }

        private static (string Title, string Artist, string ArtistMbid) Name(string mbid) => mbid switch
        {
            Teardrop => ("Teardrop", "Massive Attack", MassiveAttack),
            Overcome => ("Overcome", "Tricky", Tricky),
            _ => ("Glory Box", "Portishead", Portishead),
        };
    }
}
