using System.Net;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Discovery;
using Melodex.Core.Data;
using Melodex.Core.Listening;
using Melodex.Core.Plugins;
using Melodex.Core.Tests.Support;
using Melodex.Plugins.LastFm;
using Melodex.Plugins.ListenBrainz;

namespace Melodex.Core.Tests;

/// <summary>Discovery plugins (#3): similar artists and songs, and recommendations, from ListenBrainz and Last.fm.</summary>
public sealed class DiscoveryTests : IDisposable
{
    // MusicBrainz IDs, as ListenBrainz and Last.fm know them.
    private const string Portishead = "8f6bd1e4-fbe1-4f50-aa9b-94c450ec0f11";
    private const string MassiveAttack = "10adbe5e-a2c0-4bf3-8249-2b4cbf6e6ca8";
    private const string Tricky = "9a8b6a4a-4d64-4c31-8f5a-7e1b2a1a1a01";
    private const string GloryBox = "3a1c8f6e-21b5-4d43-9a0c-1a1a1a1a1a01";
    private const string Teardrop = "3a1c8f6e-21b5-4d43-9a0c-1a1a1a1a1a02";
    private const string Overcome = "3a1c8f6e-21b5-4d43-9a0c-1a1a1a1a1a03";
    // Roads as the catalogue has it, on a single, and the recording ListenBrainz keeps its listens on.
    private const string RoadsSingle = "3a1c8f6e-21b5-4d43-9a0c-1a1a1a1a1a04";
    private const string RoadsMain = "3a1c8f6e-21b5-4d43-9a0c-1a1a1a1a1a05";

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly MelodexClient _api;
    private readonly FakeDiscovery _services = new();

    public DiscoveryTests()
    {
        DiscoveryCache.Clear();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Melodex:DataPath", _dataPath).UseSetting("Melodex:ScheduledTasks", "false")
            .ConfigureServices(services => services.Configure<PluginHttpOptions>(http =>
            {
                http.PrimaryHandler = () => _services;
                http.RateLimit = false;
            })));
        _api = new MelodexClient(_factory.CreateSignedInClient());
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(_dataPath);
        DiscoveryCache.Clear();
    }

    [Fact]
    public void ListenBrainzs_answers_are_read_as_artists_and_songs()
    {
        var artists = ListenBrainz.ReadArtists(JsonDocument.Parse($$"""
            [
              { "artist_mbid": "{{Portishead}}", "name": "Portishead", "comment": "", "score": 20000 },
              { "artist_mbid": "{{MassiveAttack}}", "name": "Massive Attack", "comment": "UK trip hop group", "score": 11156 },
              { "artist_mbid": null, "name": "Nobody" }
            ]
            """).RootElement, Portishead, 10);
        Assert.Equal([("Massive Attack", "UK trip hop group", 11156.0)], artists.Select(a => (a.Name, a.Disambiguation, a.Score)));
        Assert.Equal([new ProviderId(Providers.MusicBrainz, MassiveAttack)], artists[0].Ids);

        var songs = ListenBrainz.ReadRecordings(JsonDocument.Parse($$"""
            [
              { "recording_mbid": "{{Teardrop}}", "recording_name": "Teardrop", "artist_credit_name": "Massive Attack", "release_name": "Mezzanine", "score": 176 },
              { "recording_mbid": "{{Teardrop}}", "recording_name": "Teardrop", "artist_credit_name": "Massive Attack", "score": 12 },
              { "recording_mbid": "{{GloryBox}}", "recording_name": "Glory Box", "artist_credit_name": "Portishead", "score": 500 }
            ]
            """).RootElement, GloryBox, 10);
        Assert.Equal([("Teardrop", "Massive Attack", "Mezzanine", 176.0)], songs.Select(s => (s.Title, s.Artist, s.Album, s.Score)));

        var metadata = ListenBrainz.ReadMetadata(JsonDocument.Parse($$"""
            {
              "{{Teardrop}}": {
                "artist": { "name": "Massive Attack feat. Elizabeth Fraser", "artists": [
                  { "artist_mbid": "{{MassiveAttack}}", "name": "Massive Attack", "join_phrase": " feat. " },
                  { "artist_mbid": null, "name": "Elizabeth Fraser" } ] },
                "recording": { "name": "Teardrop", "length": 330000, "isrcs": ["GBAAA9800001"] },
                "release": { "name": "Mezzanine" }
              },
              "{{Overcome}}": { "recording": { "name": "No artist" } }
            }
            """).RootElement);
        var teardrop = Assert.Single(metadata).Value;
        Assert.Equal(("Teardrop", "Massive Attack feat. Elizabeth Fraser", "Mezzanine", TimeSpan.FromSeconds(330)),
            (teardrop.Title, teardrop.Artist, teardrop.Album, teardrop.Duration));
        Assert.Equal([new ProviderId(Providers.MusicBrainz, Teardrop), new ProviderId(Providers.Isrc, "GBAAA9800001")], teardrop.Ids);
        Assert.Equal(["Massive Attack", "Elizabeth Fraser"], teardrop.Artists.Select(a => a.Name));
        Assert.Empty(teardrop.Artists[1].Ids);
    }

    [Fact]
    public void Lastfms_answers_are_read_whether_one_or_many()
    {
        var artists = LastFm.ReadArtists(JsonDocument.Parse($$"""
            { "artist": [ { "name": "Massive Attack", "mbid": "{{MassiveAttack}}", "match": "1" }, { "name": "Beth Gibbons", "mbid": "", "match": "0.52" } ] }
            """).RootElement, 10);
        Assert.Equal([("Massive Attack", 1.0, 1), ("Beth Gibbons", 0.52, 0)], artists.Select(a => (a.Name, a.Score, a.Ids.Count)));

        // One item comes as an object rather than a list.
        var tracks = LastFm.ReadTracks(JsonDocument.Parse($$"""
            { "track": { "name": "Teardrop", "mbid": "{{Teardrop}}", "match": 0.83, "duration": 330, "artist": { "name": "Massive Attack", "mbid": "{{MassiveAttack}}" } } }
            """).RootElement, 10);
        var teardrop = Assert.Single(tracks);
        Assert.Equal(("Teardrop", "Massive Attack", 0.83, TimeSpan.FromSeconds(330)), (teardrop.Title, teardrop.Artist, teardrop.Score, teardrop.Duration));
        Assert.Equal([new ProviderId(Providers.MusicBrainz, MassiveAttack)], teardrop.Artists[0].Ids);
    }

    [Fact]
    public void Answers_combine_putting_first_what_the_plugins_agree_on()
    {
        static DiscoveredArtist Artist(string name, double score, string? mbid = null) =>
            new(name, mbid is null ? [] : [new ProviderId(Providers.MusicBrainz, mbid)], score);
        IReadOnlyList<(string, IReadOnlyList<DiscoveredArtist>)> answers =
        [
            ("ListenBrainz", [Artist("Massive Attack", 200, MassiveAttack), Artist("Tricky", 180, Tricky), Artist("Björk", 20)]),
            ("Last.fm", [Artist("Beth Gibbons", 1), Artist("Massive Attack", 0.9, MassiveAttack), Artist("bjork", 0.5)]),
            ("Broken", []),
        ];
        var combined = DiscoveryService.Combine(answers, a => a.Score, a => [.. a.Ids.Select(i => i.Value), a.Name.ToLowerInvariant().Replace("ö", "o")]);

        // Both found Massive Attack, so it's first. What both found is shown as the one that scored it higher found it.
        Assert.Equal(["Massive Attack", "Tricky", "Beth Gibbons", "bjork"], combined.Select(c => c.Best.Name));
        Assert.Equal(["ListenBrainz", "Last.fm"], combined[0].From);
        // Scores are scaled to each plugin's best, the second plugin's counting three quarters, then to at most one.
        Assert.Equal(Math.Round((1 + 0.9 * 0.75) / 1.75, 4), combined[0].Score);
        Assert.Equal(Math.Round(0.9 / 1.75, 4), combined[1].Score);
        Assert.Equal(Math.Round(0.75 / 1.75, 4), combined[2].Score);
        Assert.All(combined, c => Assert.InRange(c.Score, 0, 1));
    }

    [Fact]
    public async Task Similar_artists_and_songs_are_matched_to_the_catalogue_and_combined()
    {
        var (portishead, massive, gloryBox, teardrop) = await SeedAsync();

        // ListenBrainz is set up from the start; Last.fm needs a key.
        var plugins = await _api.ListPluginsAsync();
        Assert.Equal([ListenBrainzFactory.PluginId], plugins.Where(p => p.Type == PluginType.Discovery).Select(p => p.PluginId));
        Assert.Equal(new DiscoveryStatus(true, true, true), await _api.GetDiscoveryAsync());

        var similar = await _api.ListSimilarArtistsAsync(portishead);
        Assert.Equal(["Massive Attack", "Tricky"], similar.Select(a => a.Name));
        Assert.Equal((massive, true), (similar[0].Id, similar[0].InLibrary));
        // Tricky wasn't in the catalogue yet: they are now, by their MusicBrainz ID, as a search would add them.
        Assert.NotNull(similar[1].Id);
        Assert.False(similar[1].InLibrary);
        Assert.Equal(similar[1].Id, (await _api.ListSimilarArtistsAsync(portishead)).Single(a => a.Name == "Tricky").Id);

        await _api.AddPluginAsync(new AddPlugin(LastFmFactory.PluginId, Settings: JsonDocument.Parse("""{ "apiKey": "test-key" }""").RootElement));
        DiscoveryCache.Clear();
        similar = await _api.ListSimilarArtistsAsync(portishead);
        Assert.Equal(["Massive Attack", "Tricky", "Beth Gibbons"], similar.Select(a => a.Name));
        Assert.Equal(["ListenBrainz", "Last.fm"], similar[0].From);
        Assert.Null(similar[2].Id);
        // Last.fm was asked by Portishead's MusicBrainz ID.
        Assert.Contains(_services.Asked, u => u.Contains("artist.getsimilar") && u.Contains(Portishead));

        var songs = await _api.ListSimilarSongsAsync(gloryBox);
        var song = songs[0];
        Assert.Equal(("Teardrop", teardrop, massive, true), (song.Title, song.TrackId, song.ArtistId, song.Owned));
        Assert.Equal(["ListenBrainz", "Last.fm"], song.From);
        Assert.DoesNotContain(songs, s => s.TrackId == gloryBox);
        // A song the catalogue doesn't have isn't listed.
        Assert.DoesNotContain(songs, s => s.Title == "Overcome");

        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            (await Assert.ThrowsAsync<HttpRequestException>(() => _api.ListSimilarArtistsAsync(Guid.NewGuid()))).StatusCode);
    }

    [Fact]
    public async Task A_song_listenbrainz_knows_by_another_recording_finds_similar_songs_through_it()
    {
        Assert.Equal(RoadsMain, ListenBrainz.ReadLookup(JsonDocument.Parse($$"""[{ "recording_mbid": "{{RoadsMain}}", "recording_name": "Roads" }]""").RootElement));
        Assert.Null(ListenBrainz.ReadLookup(JsonDocument.Parse("[]").RootElement));

        await SeedAsync();
        Guid roads;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            var portishead = db.Artists.Single(a => a.Name == "Portishead");
            var track = new Track { ArtistId = portishead.Id, Title = "Roads", ArtistCredit = "Portishead" };
            db.Tracks.Add(track);
            db.Crosswalk.Add(new ProviderIdMapping { EntityKind = EntityKind.Track, EntityId = track.Id, Provider = Providers.MusicBrainz, Value = RoadsSingle });
            await db.SaveChangesAsync();
            roads = track.Id;
        }

        var songs = await _api.ListSimilarSongsAsync(roads);
        Assert.Equal(["Teardrop"], songs.Select(s => s.Title));
        Assert.Contains(_services.Asked, u => u.Contains("acr-lookup") && u.Contains("recording_name=Roads"));
        Assert.Contains(_services.Asked, u => u.Contains("similar-recordings") && u.Contains(RoadsMain));
    }

    [Fact]
    public async Task Recommendations_come_from_a_linked_listenbrainz_account()
    {
        var (_, _, _, teardrop) = await SeedAsync();
        Assert.Empty(await _api.ListRecommendationsAsync());
        Assert.DoesNotContain(_services.Asked, u => u.Contains("cf/recommendation"));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            db.ScrobbleAccounts.Add(new ScrobbleAccount { UserId = db.Users.Single().Id, Service = ScrobbleService.ListenBrainz, Username = "someone", Secret = "x" });
            await db.SaveChangesAsync();
        }
        DiscoveryCache.Clear();
        var recommended = await _api.ListRecommendationsAsync();
        Assert.Equal([("Teardrop", teardrop)], recommended.Select(r => (r.Title, r.TrackId)));
        Assert.Equal(1, recommended[0].Score);
        Assert.Contains(_services.Asked, u => u.Contains("cf/recommendation/user/someone/recording"));
    }

    /// <summary>Portishead and Massive Attack in the catalogue, one song of each, and Teardrop in the library.</summary>
    private async Task<(Guid Portishead, Guid Massive, Guid GloryBox, Guid Teardrop)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        var portishead = new Artist { Name = "Portishead", InLibrary = true };
        var massive = new Artist { Name = "Massive Attack", InLibrary = true };
        var gloryBox = new Track { Artist = portishead, Title = "Glory Box", ArtistCredit = "Portishead" };
        var teardrop = new Track { Artist = massive, Title = "Teardrop", ArtistCredit = "Massive Attack" };
        db.AddRange(portishead, massive, gloryBox, teardrop);
        db.Crosswalk.AddRange(
            new ProviderIdMapping { EntityKind = EntityKind.Artist, EntityId = portishead.Id, Provider = Providers.MusicBrainz, Value = Portishead },
            new ProviderIdMapping { EntityKind = EntityKind.Artist, EntityId = massive.Id, Provider = Providers.MusicBrainz, Value = MassiveAttack },
            new ProviderIdMapping { EntityKind = EntityKind.Track, EntityId = gloryBox.Id, Provider = Providers.MusicBrainz, Value = GloryBox },
            new ProviderIdMapping { EntityKind = EntityKind.Track, EntityId = teardrop.Id, Provider = Providers.MusicBrainz, Value = Teardrop });
        db.LibraryFiles.Add(new LibraryFile { Path = "Massive Attack/Teardrop.flac", TrackId = teardrop.Id });
        await db.SaveChangesAsync();
        return (portishead.Id, massive.Id, gloryBox.Id, teardrop.Id);
    }

    /// <summary>Stands in for ListenBrainz and Last.fm, noting what they're asked.</summary>
    private sealed class FakeDiscovery : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            lock (Asked)
                Asked.Add(url.ToString());
            var query = HttpUtility.ParseQueryString(url.Query);
            object? answer = (url.Host, url.AbsolutePath) switch
            {
                ("labs.api.listenbrainz.org", "/similar-artists/json") => new object[]
                {
                    new { artist_mbid = Portishead, name = "Portishead", score = 30000 },
                    new { artist_mbid = MassiveAttack, name = "Massive Attack", comment = "UK trip hop group", score = 11000 },
                    new { artist_mbid = Tricky, name = "Tricky", comment = "", score = 9000 },
                },
                ("labs.api.listenbrainz.org", "/acr-lookup/json") => new object[] { new { recording_mbid = RoadsMain, recording_name = "Roads" } },
                ("labs.api.listenbrainz.org", "/similar-recordings/json") when query["recording_mbids"] == RoadsSingle => Array.Empty<object>(),
                ("labs.api.listenbrainz.org", "/similar-recordings/json") => new object[]
                {
                    new { recording_mbid = Teardrop, recording_name = "Teardrop", artist_credit_name = "Massive Attack", score = 200 },
                    new { recording_mbid = Overcome, recording_name = "Overcome", artist_credit_name = "Tricky", score = 100 },
                },
                ("api.listenbrainz.org", "/1/cf/recommendation/user/someone/recording") => new
                {
                    payload = new { mbids = new object[] { new { recording_mbid = Teardrop, score = 0.9 }, new { recording_mbid = Overcome, score = 0.3 } } },
                },
                ("api.listenbrainz.org", "/1/metadata/recording/") => query["recording_mbids"]!.Split(',').ToDictionary(m => m, m => (object)new
                {
                    artist = new { name = m == Teardrop ? "Massive Attack" : "Tricky",
                        artists = new[] { new { artist_mbid = m == Teardrop ? MassiveAttack : Tricky, name = m == Teardrop ? "Massive Attack" : "Tricky" } } },
                    recording = new { name = m == Teardrop ? "Teardrop" : "Overcome", length = 300000 },
                }),
                ("ws.audioscrobbler.com", _) when query["method"] == "artist.getsimilar" => new
                {
                    similarartists = new
                    {
                        artist = new object[]
                        {
                            new { name = "Massive Attack", mbid = MassiveAttack, match = "1" },
                            new { name = "Beth Gibbons", mbid = "", match = "0.6" },
                        },
                    },
                },
                ("ws.audioscrobbler.com", _) when query["method"] == "track.getsimilar" => new
                {
                    similartracks = new { track = new object[] { new { name = "Teardrop", mbid = "", match = 0.9, artist = new { name = "Massive Attack", mbid = MassiveAttack } } } },
                },
                _ => null,
            };
            return Task.FromResult(answer is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(answer), System.Text.Encoding.UTF8, "application/json"),
                });
        }
    }
}
