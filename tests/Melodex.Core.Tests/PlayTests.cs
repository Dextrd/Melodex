using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Core.Data;
using Melodex.Core.Listening;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>Listening history (#2): plays kept for each person, most played, and sent on to ListenBrainz and Last.fm.</summary>
public sealed class PlayTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly MelodexClient _api;
    private readonly FakeScrobbling _services = new();

    public PlayTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Melodex:DataPath", _dataPath).UseSetting("Melodex:ScheduledTasks", "false")
            .ConfigureServices(services => services.AddHttpClient(Scrobbler.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _services)));
        _api = new MelodexClient(_factory.CreateSignedInClient());
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(_dataPath);
    }

    private static RecordPlay Song(Guid track, string title, Guid? album = null, DateTime? at = null, Guid? artist = null) =>
        new(track, Title: title, ArtistCredit: "Portishead", ArtistId: artist, AlbumId: album, AlbumTitle: album is null ? null : "Dummy",
            DurationSeconds: 240, PlayedAt: at, Source: album is null ? null : new PlaySource(PlaySourceKind.Album, album, "Dummy"));

    [Fact]
    public async Task Plays_are_kept_for_each_person_and_counted_in_most_played()
    {
        var (roads, glory, album, artist) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var start = DateTime.UtcNow.AddHours(-1);
        var first = await _api.RecordPlayAsync(Song(roads, "Roads", album, start, artist));
        Assert.Equal(("Roads", "Portishead", "web"), (first.Title, first.ArtistCredit, first.Client));
        Assert.Equal(new PlaySource(PlaySourceKind.Album, album, "Dummy"), first.Source);
        // Told twice, as some players do, it's the same play.
        Assert.Equal(first.Id, (await _api.RecordPlayAsync(Song(roads, "Roads", album, start.AddSeconds(10), artist))).Id);
        await _api.RecordPlayAsync(Song(roads, "Roads", album, start.AddMinutes(10), artist));
        await _api.RecordPlayAsync(Song(glory, "Glory Box", album, start.AddMinutes(20), artist));
        var old = await _api.RecordPlayAsync(Song(glory, "Glory Box", null, DateTime.UtcNow.AddDays(-40), artist));

        var page = await _api.ListPlaysAsync();
        Assert.Equal(4, page.Total);
        Assert.Equal(["Glory Box", "Roads", "Roads", "Glory Box"], page.Items.Select(p => p.Title));
        Assert.Equal(["Glory Box"], (await _api.ListPlaysAsync(skip: 3)).Items.Select(p => p.Title));

        var songs = await _api.ListMostPlayedAsync(TopKind.Song);
        // Level, the one played latest comes first.
        Assert.Equal([(glory, 2), (roads, 2)], songs.Select(s => (s.Id, s.Plays)));
        Assert.Equal([(roads, 2), (glory, 1)], (await _api.ListMostPlayedAsync(TopKind.Song, PlayPeriod.Month)).Select(s => (s.Id, s.Plays)));
        Assert.Equal([(album, 3)], (await _api.ListMostPlayedAsync(TopKind.Album)).Select(a => (a.Id, a.Plays)));
        Assert.Equal([(artist, "Portishead", 4)], (await _api.ListMostPlayedAsync(TopKind.Artist)).Select(a => (a.Id, a.Name, a.Plays)));

        await _api.DeletePlayAsync(old.Id);
        Assert.Equal(3, (await _api.ListPlaysAsync()).Total);
        Assert.Equal(HttpStatusCode.NotFound, (await Assert.ThrowsAsync<MelodexApiException>(() => _api.DeletePlayAsync(old.Id))).Status);
        // A song not in the library needs its title and artist; a play can't be yet to come.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Assert.ThrowsAsync<MelodexApiException>(() => _api.RecordPlayAsync(new RecordPlay(Guid.NewGuid())))).Status);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Assert.ThrowsAsync<MelodexApiException>(() => _api.RecordPlayAsync(Song(roads, "Roads", at: DateTime.UtcNow.AddHours(1))))).Status);

        await _api.CreateUserAsync(new CreateUser("bob", "bob's password", [UserPermissions.Read, UserPermissions.Listen]));
        var http = _factory.CreateClient();
        (await http.PostAsJsonAsync("api/v1/auth/login", new SignIn("bob", "bob's password"))).EnsureSuccessStatusCode();
        var bob = new MelodexClient(http);
        Assert.Equal(0, (await bob.ListPlaysAsync()).Total);
        Assert.Equal(HttpStatusCode.NotFound, (await Assert.ThrowsAsync<MelodexApiException>(() => bob.DeletePlayAsync(first.Id))).Status);
        await bob.RecordPlayAsync(Song(roads, "Roads"));
        Assert.Equal(3, (await _api.ListPlaysAsync()).Total);
    }

    [Fact]
    public async Task Plays_go_to_ListenBrainz_and_wait_while_it_is_down()
    {
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Assert.ThrowsAsync<MelodexApiException>(() => _api.LinkListenBrainzAsync("wrong"))).Status);
        var link = await _api.LinkListenBrainzAsync(FakeScrobbling.Token);
        Assert.Equal((ScrobbleService.ListenBrainz, "listener"), (link.Service, link.Username));

        await _api.RecordPlayAsync(Song(Guid.NewGuid(), "Roads", Guid.NewGuid()));
        var sent = await WaitAsync(() => _services.Listens.Count > 0);
        Assert.True(sent);
        var listen = _services.Listens.Single();
        Assert.Equal(("single", "Roads", "Portishead", "Dummy"), ((string)listen["listen_type"]!, (string)listen["payload"]![0]!["track_metadata"]!["track_name"]!,
            (string)listen["payload"]![0]!["track_metadata"]!["artist_name"]!, (string)listen["payload"]![0]!["track_metadata"]!["release_name"]!));
        Assert.Equal(240000, (long)listen["payload"]![0]!["track_metadata"]!["additional_info"]!["duration_ms"]!);
        Assert.True(await WaitAsync(async () => (await _api.GetScrobblingAsync()).Links.Single().LastSentAt is not null));

        // While it's down, plays wait and are tried again.
        _services.Down = true;
        await _api.RecordPlayAsync(Song(Guid.NewGuid(), "Glory Box"));
        Assert.True(await WaitAsync(async () => (await _api.GetScrobblingAsync()).Links.Single().Error is not null));
        Assert.Equal(1, (await _api.GetScrobblingAsync()).Links.Single().Waiting);
        _services.Down = false;
        await DueNowAsync();
        Assert.Equal(0, (await _api.GetScrobblingAsync()).Links.Single().Waiting);
        Assert.Equal(2, _services.Listens.Count);

        // A token it no longer takes stops sending until it's linked again, and then what waited goes.
        _services.Refuse = true;
        await _api.RecordPlayAsync(Song(Guid.NewGuid(), "Sour Times"));
        Assert.True(await WaitAsync(async () => (await _api.GetScrobblingAsync()).Links.Single().Error?.Contains("link it again") == true));
        _services.Refuse = false;
        await DueNowAsync();
        Assert.Equal(2, _services.Listens.Count);
        await _api.LinkListenBrainzAsync(FakeScrobbling.Token);
        Assert.True(await WaitAsync(() => _services.Listens.Count == 3));

        await _api.UnlinkScrobblingAsync(ScrobbleService.ListenBrainz);
        Assert.Empty((await _api.GetScrobblingAsync()).Links);
    }

    [Fact]
    public async Task Plays_go_to_Last_fm_with_the_admins_API_key()
    {
        Assert.Null((await _api.GetScrobblingAsync()).LastFmApiKey);
        // Without the admin's key, Last.fm can't be linked.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Assert.ThrowsAsync<MelodexApiException>(() => _api.LinkLastFmAsync(FakeScrobbling.Token))).Status);
        await _api.SaveScrobblingSettingsAsync(new SaveScrobbleSettings(FakeScrobbling.ApiKey, FakeScrobbling.Secret));
        Assert.Equal(new ScrobbleSettingsInfo(FakeScrobbling.ApiKey, true), await _api.GetScrobblingSettingsAsync());
        // The secret's kept when it's left out.
        await _api.SaveScrobblingSettingsAsync(new SaveScrobbleSettings(FakeScrobbling.ApiKey));
        Assert.Equal(FakeScrobbling.ApiKey, (await _api.GetScrobblingAsync()).LastFmApiKey);

        var link = await _api.LinkLastFmAsync(FakeScrobbling.Token);
        Assert.Equal((ScrobbleService.LastFm, "lastfm-listener"), (link.Service, link.Username));
        await _api.SetNowPlayingAsync(Song(Guid.NewGuid(), "Roads"));
        Assert.True(await WaitAsync(() => _services.LastFm.Any(c => c["method"] == "track.updateNowPlaying")));
        await _api.RecordPlayAsync(Song(Guid.NewGuid(), "Roads", Guid.NewGuid()));
        Assert.True(await WaitAsync(() => _services.LastFm.Any(c => c["method"] == "track.scrobble")));
        var scrobble = _services.LastFm.Single(c => c["method"] == "track.scrobble");
        Assert.Equal(("Roads", "Portishead", "Dummy", FakeScrobbling.Session), (scrobble["track[0]"], scrobble["artist[0]"], scrobble["album[0]"], scrobble["sk"]));
        Assert.Equal(0, (await _api.GetScrobblingAsync()).Links.Single().Waiting);
    }

    /// <summary>Has the scrobbler try what's waiting now, rather than after its wait.</summary>
    private async Task DueNowAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        await db.ScrobbleSends.ExecuteUpdateAsync(u => u.SetProperty(s => s.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));
        await _factory.Services.GetRequiredService<Scrobbler>().SendDueAsync(default);
    }

    private static Task<bool> WaitAsync(Func<bool> done) => WaitAsync(() => Task.FromResult(done()));

    private static async Task<bool> WaitAsync(Func<Task<bool>> done)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await done())
                return true;
            await Task.Delay(100);
        }
        return false;
    }

    /// <summary>Stands in for ListenBrainz and Last.fm, checking tokens and Last.fm's signatures.</summary>
    private sealed class FakeScrobbling : HttpMessageHandler
    {
        public const string Token = "good-token";
        public const string ApiKey = "the-api-key";
        public const string Secret = "the-secret";
        public const string Session = "the-session";

        public ConcurrentQueue<JsonNode> Listens { get; } = new();
        public ConcurrentQueue<Dictionary<string, string>> LastFm { get; } = new();
        public volatile bool Down;
        public volatile bool Refuse;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Down)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("""{"error":"Down for a while"}""") };
            var uri = request.RequestUri!;
            if (uri.Host == "api.listenbrainz.org")
            {
                var good = !Refuse && request.Headers.Authorization?.Parameter == Token;
                if (uri.AbsolutePath.EndsWith("validate-token"))
                    return Json(good ? new JsonObject { ["valid"] = true, ["user_name"] = "listener" } : new JsonObject { ["valid"] = false });
                if (!good)
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":"Invalid token"}""") };
                var listen = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                if ((string)listen["listen_type"]! != "playing_now")
                    Listens.Enqueue(listen);
                return Json(new JsonObject { ["status"] = "ok" });
            }
            if (uri.Host == "ws.audioscrobbler.com")
            {
                var query = HttpUtility.ParseQueryString(request.Method == HttpMethod.Post ? await request.Content!.ReadAsStringAsync(cancellationToken) : uri.Query);
                var parameters = query.AllKeys.ToDictionary(k => k!, k => query[k]!);
                var signed = new Dictionary<string, string>(parameters);
                signed.Remove("api_sig");
                if (parameters.GetValueOrDefault("api_key") != ApiKey || parameters.GetValueOrDefault("api_sig") != Scrobbler.LastFmSignature(signed, Secret))
                    return Json(new JsonObject { ["error"] = 13, ["message"] = "Invalid method signature supplied" });
                LastFm.Enqueue(parameters);
                return parameters["method"] switch
                {
                    "auth.getSession" when parameters["token"] == Token => Json(new JsonObject
                    {
                        ["session"] = new JsonObject { ["name"] = "lastfm-listener", ["key"] = Session, ["subscriber"] = 0 },
                    }),
                    "auth.getSession" => Json(new JsonObject { ["error"] = 4, ["message"] = "Unauthorized Token" }),
                    _ when parameters.GetValueOrDefault("sk") != Session => Json(new JsonObject { ["error"] = 9, ["message"] = "Invalid session key" }),
                    _ => Json(new JsonObject { ["scrobbles"] = new JsonObject { ["@attr"] = new JsonObject { ["accepted"] = 1, ["ignored"] = 0 } } }),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(JsonNode body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
    }
}
