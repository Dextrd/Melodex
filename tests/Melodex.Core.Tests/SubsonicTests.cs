using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Core.Listening;
using Melodex.Core.Plugins;
using Melodex.Core.Subsonic;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>Melodex as a music server for Subsonic players (#7): signing in, browsing, playing and playlists.</summary>
public sealed class SubsonicTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly MelodexClient _api;
    private readonly HttpClient _player;
    private readonly FakeCoverArt _coverArt = new(new ReplayHandler(TestLibrary.MusicBrainzFixtures));

    public SubsonicTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Melodex:DataPath", _dataPath).UseSetting("Melodex:ScheduledTasks", "false")
            .UseSetting("Melodex:LibraryPath", TestLibrary.Music)
            .ConfigureServices(services => services.Configure<PluginHttpOptions>(http =>
            {
                http.PrimaryHandler = () => _coverArt;
                http.RateLimit = false;
            })));
        _api = new MelodexClient(_factory.CreateSignedInClient());
        // A player has no session: only what it sends with each call.
        _player = _factory.CreateClient();
    }

    public void Dispose()
    {
        _player.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(_dataPath);
    }

    // ---- Without a library ----

    [Fact]
    public void Answers_are_the_same_in_XML_and_JSON()
    {
        var response = SubsonicResponse.Ok().With("lyrics", new JsonObject { ["artist"] = "A", ["value"] = "La la" })
            .With("genres", new JsonObject { ["genre"] = new JsonArray(new JsonObject { ["songCount"] = 2, ["value"] = "Rock" }) })
            .With("openSubsonicExtensions", new JsonArray(new JsonObject { ["name"] = "formPost", ["versions"] = new JsonArray(1) }));

        var json = JsonNode.Parse(response.ToJson())!["subsonic-response"]!;
        Assert.Equal("ok", (string)json["status"]!);
        Assert.Equal(SubsonicResponse.ApiVersion, (string)json["version"]!);
        Assert.True((bool)json["openSubsonic"]!);
        Assert.Equal("La la", (string)json["lyrics"]!["value"]!);

        var xml = XDocument.Parse(response.ToXml()).Root!;
        XNamespace ns = SubsonicResponse.Namespace;
        Assert.Equal(ns + "subsonic-response", xml.Name);
        Assert.Equal("ok", (string)xml.Attribute("status")!);
        // A value is an attribute, "value" the text, a list an element for each, and so are a list's values.
        Assert.Equal(("A", "La la"), ((string)xml.Element(ns + "lyrics")!.Attribute("artist")!, xml.Element(ns + "lyrics")!.Value));
        var genre = xml.Element(ns + "genres")!.Element(ns + "genre")!;
        Assert.Equal(("2", "Rock"), ((string)genre.Attribute("songCount")!, genre.Value));
        Assert.Equal("1", xml.Element(ns + "openSubsonicExtensions")!.Element(ns + "versions")!.Value);

        var error = XDocument.Parse(SubsonicResponse.Error(40, "Wrong").ToXml()).Root!;
        Assert.Equal(("failed", "40"), ((string)error.Attribute("status")!, (string)error.Element(ns + "error")!.Attribute("code")!));
    }

    [Theory]
    [InlineData("flac", 900, null, null, null, 0)]
    [InlineData("flac", 900, "raw", 128, null, 0)]
    [InlineData("flac", 900, null, 320, "mp3", 320)]
    [InlineData("mp3", 320, "mp3", 320, null, 0)]
    [InlineData("mp3", 320, "mp3", 128, "mp3", 128)]
    [InlineData("flac", 900, "opus", null, "opus", 192)]
    [InlineData("opus", 128, "opus", 0, null, 0)]
    [InlineData("flac", 900, "opus", 512, "opus", 256)]
    [InlineData("flac", 900, "wma", 128, "mp3", 128)]
    public void A_song_is_transcoded_only_when_it_has_to_be(string suffix, int kbps, string? format, int? max, string? to, int toKbps)
    {
        var decided = Transcoder.Decide(suffix, kbps, format, max);
        Assert.Equal(to, decided?.Format.Name);
        if (decided is { } d)
            Assert.Equal(toKbps, d.Kbps);
    }

    [Fact]
    public void A_files_ID_says_when_it_was_found()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var made = SubsonicLibrary.CreatedOf(Guid.CreateVersion7());
        Assert.InRange(made, before, DateTime.UtcNow.AddSeconds(1));
    }

    // ---- With the test library ----

    private async Task<JsonNode> CallAsync(string method, string auth, string query = "")
    {
        using var response = await _player.GetAsync($"rest/{method}?f=json&v=1.16.1&c=tests&{auth}{(query.Length > 0 ? "&" + query : "")}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["subsonic-response"]!;
    }

    private static int? ErrorOf(JsonNode answer) => (string)answer["status"]! == "ok" ? null : (int)answer["error"]!["code"]!;

    private async Task<JsonNode> OkAsync(string method, string auth, string query = "")
    {
        var answer = await CallAsync(method, auth, query);
        Assert.True(ErrorOf(answer) is null, answer.ToJsonString());
        return answer;
    }

    private static string Md5(string text) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public async Task Players_sign_in_with_an_API_key_alone_or_as_the_password_with_the_username()
    {
        // The real password never works.
        var u = $"u={SignedIn.Username}";
        Assert.Equal(40, ErrorOf(await CallAsync("ping", $"{u}&p={Uri.EscapeDataString(SignedIn.Password)}")));
        Assert.Equal(10, ErrorOf(await CallAsync("ping", "")));

        // A key made for Subsonic players is the password, plain, hex-encoded or as a token.
        var made = await _api.CreateApiKeyAsync(new CreateApiKey("Phone", [UserPermissions.Listen], Subsonic: true));
        Assert.True(made.Info.Subsonic);
        Assert.True((await _api.ListApiKeysAsync()).Single(k => k.Id == made.Info.Id).Subsonic);
        var key = made.Key;
        await OkAsync("ping", $"{u}&p={key}");
        await OkAsync("ping", $"u={SignedIn.Username.ToUpperInvariant()}&p=enc:{Convert.ToHexStringLower(Encoding.UTF8.GetBytes(key))}");
        await OkAsync("ping", $"{u}&t={Md5(key + "s4lt")}&s=s4lt");
        Assert.Equal(40, ErrorOf(await CallAsync("ping", $"{u}&t={Md5(key + "s4lt")}&s=other")));
        Assert.Equal(40, ErrorOf(await CallAsync("ping", $"u=someone&p={key}")));
        // Signing in says who's playing: the key's owner.
        Assert.Equal(SignedIn.Username, (string)(await OkAsync("getUser", $"{u}&p={key}"))["user"]!["username"]!);

        // Someone who may only listen makes their own key, which signs in only with their own name: never as the admin.
        await _api.CreateUserAsync(new CreateUser("bob", "bob's password", [UserPermissions.Listen]));
        var bobHttp = _factory.CreateClient();
        (await bobHttp.PostAsJsonAsync("api/v1/auth/login", new SignIn("bob", "bob's password"))).EnsureSuccessStatusCode();
        var bobs = (await new MelodexClient(bobHttp).CreateApiKeyAsync(new CreateApiKey("Bob's phone", [UserPermissions.Listen], Subsonic: true))).Key;
        Assert.Equal(40, ErrorOf(await CallAsync("ping", $"{u}&p={bobs}")));
        Assert.Equal(40, ErrorOf(await CallAsync("ping", $"{u}&t={Md5(bobs + "s4lt")}&s=s4lt")));
        Assert.Equal("bob", (string)(await OkAsync("getUser", $"u=bob&p={bobs}"))["user"]!["username"]!);

        // Another key isn't a password, though it works alone with OpenSubsonic's apiKey if it can listen.
        var listen = (await _api.CreateApiKeyAsync(new CreateApiKey("Player", [UserPermissions.Listen]))).Key;
        var read = (await _api.CreateApiKeyAsync(new CreateApiKey("Reader", [UserPermissions.Read]))).Key;
        Assert.False((await _api.ListApiKeysAsync()).Single(k => k.Name == "Player").Subsonic);
        Assert.Equal(40, ErrorOf(await CallAsync("ping", $"{u}&p={listen}")));
        await OkAsync("ping", $"apiKey={listen}");
        await OkAsync("ping", $"apiKey={key}");
        Assert.Equal(43, ErrorOf(await CallAsync("ping", $"apiKey={listen}&{u}")));
        Assert.Equal(44, ErrorOf(await CallAsync("ping", "apiKey=mdx_nothing")));
        Assert.Equal(50, ErrorOf(await CallAsync("ping", $"apiKey={read}")));

        // A key for players has to listen.
        var refused = await Assert.ThrowsAsync<MelodexApiException>(() => _api.CreateApiKeyAsync(new CreateApiKey("Phone", [UserPermissions.Read], Subsonic: true)));
        Assert.Contains("Listen", refused.Message);

        // Revoked, it signs the player out.
        await _api.RevokeApiKeyAsync(made.Info.Id);
        Assert.Equal(40, ErrorOf(await CallAsync("ping", $"{u}&p={key}")));

        var extensions = (await OkAsync("getOpenSubsonicExtensions", $"apiKey={listen}"))["openSubsonicExtensions"]!.AsArray();
        Assert.Contains("apiKeyAuthentication", extensions.Select(e => (string)e!["name"]!));
        Assert.Equal(70, ErrorOf(await CallAsync("getVideos", $"apiKey={listen}")));

        // XML unless JSON is asked for, and a form post works as well as a query.
        using var xml = await _player.PostAsync("rest/ping.view", new FormUrlEncodedContent(new Dictionary<string, string> { ["apiKey"] = listen, ["v"] = "1.16.1", ["c"] = "tests" }));
        Assert.StartsWith("text/xml", xml.Content.Headers.ContentType!.ToString());
        Assert.Equal("ok", (string)XDocument.Parse(await xml.Content.ReadAsStringAsync()).Root!.Attribute("status")!);
    }

    [TestLibraryFact]
    public async Task Players_browse_search_and_play_the_library()
    {
        await ScanAsync();
        var key = $"apiKey={(await _api.CreateApiKeyAsync(new CreateApiKey("Player", [UserPermissions.Listen]))).Key}";
        var tracks = await _api.GetLibraryTracksAsync();

        // Artists, indexed by letter, then an artist's albums and an album's songs.
        var indexes = (await OkAsync("getArtists", key))["artists"]!["index"]!.AsArray();
        var artists = indexes.SelectMany(i => i!["artist"]!.AsArray()).ToList();
        Assert.All(indexes, i => Assert.Matches("^([A-Z]|#)$", (string)i!["name"]!));
        var artist = (await OkAsync("getArtist", key, $"id={artists[0]!["id"]}"))["artist"]!;
        var albums = artist["album"]!.AsArray();
        Assert.Equal((int)artist["albumCount"]!, albums.Count);
        var album = (await OkAsync("getAlbum", key, $"id={albums[0]!["id"]}"))["album"]!;
        var songs = album["song"]!.AsArray();
        Assert.Equal((int)album["songCount"]!, songs.Count);
        var song = songs[0]!;
        Assert.Equal((string)album["id"]!, (string)song["albumId"]!);
        var track = tracks.Single(t => t.FileId.ToString() == (string)song["id"]!);
        Assert.Equal((track.Title, track.Path), ((string)song["title"]!, (string)song["path"]!));
        Assert.Equal((string)song["title"]!, (string)(await OkAsync("getSong", key, $"id={song["id"]}"))["song"]!["title"]!);
        Assert.Equal(70, ErrorOf(await CallAsync("getAlbum", key, $"id={Guid.NewGuid()}")));

        // The same as folders.
        var top = (await OkAsync("getMusicDirectory", key, "id=1"))["directory"]!["child"]!.AsArray();
        Assert.Equal(artists.Count, top.Count);
        var folder = (await OkAsync("getMusicDirectory", key, $"id={album["id"]}"))["directory"]!;
        Assert.Equal(songs.Count, folder["child"]!.AsArray().Count);

        // An empty search pages through everything, as players copy a library; a word finds what has it.
        var all = new List<JsonNode>();
        for (var offset = 0; ; offset += 30)
        {
            var page = (await OkAsync("search3", key, $"query=%22%22&songCount=30&songOffset={offset}&artistCount=0&albumCount=0"))["searchResult3"]!["song"]!.AsArray();
            all.AddRange(page.Select(s => s!));
            if (page.Count < 30)
                break;
        }
        Assert.Equal(tracks.Count, all.Select(s => (string)s["id"]!).Distinct().Count());
        var word = track.Title.Split(' ')[0];
        var found = (await OkAsync("search3", key, $"query={Uri.EscapeDataString(word.ToUpperInvariant())}"))["searchResult3"]!["song"]!.AsArray();
        Assert.Contains(found, s => (string)s!["id"]! == track.FileId.ToString());

        // Lists of albums, random songs.
        var byName = (await OkAsync("getAlbumList2", key, "type=alphabeticalByName&size=500"))["albumList2"]!["album"]!.AsArray()
            .Select(a => (string)a!["name"]!).ToList();
        Assert.Equal(byName.Order(StringComparer.CurrentCultureIgnoreCase), byName);
        Assert.Equal(5, (await OkAsync("getRandomSongs", key, "size=5"))["randomSongs"]!["song"]!.AsArray().Count);
        await OkAsync("getGenres", key);
        Assert.All((await OkAsync("getStarred2", key))["starred2"]!.AsObject(), list => Assert.Empty(list.Value!.AsArray()));

        // A song plays as it is, from anywhere in it.
        var id = (string)song["id"]!;
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"rest/stream?{key}&id={id}"))
        {
            request.Headers.Range = new RangeHeaderValue(100, 199);
            using var raw = await _player.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, raw.StatusCode);
            Assert.Equal(100, (await raw.Content.ReadAsByteArrayAsync()).Length);
            Assert.Equal((string)song["contentType"]!, raw.Content.Headers.ContentType!.MediaType);
        }
        // Or smaller, in what's asked for.
        using (var mp3 = await _player.GetAsync($"rest/stream?{key}&id={id}&format=mp3&maxBitRate=96"))
        {
            Assert.Equal("audio/mpeg", mp3.Content.Headers.ContentType!.MediaType);
            var bytes = await mp3.Content.ReadAsByteArrayAsync();
            Assert.True(bytes.Length > 1000);
            // An MP3 frame's sync bits, or an ID3 tag.
            Assert.True(bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0 || bytes.AsSpan(0, 3).SequenceEqual("ID3"u8));
        }
        using (var download = await _player.GetAsync($"rest/download?{key}&id={id}"))
            Assert.Equal(new FileInfo(Path.Combine(TestLibrary.Music, track.Path)).Length, (await download.Content.ReadAsByteArrayAsync()).Length);

        // Its picture, by the ID it was given.
        using (var cover = await _player.GetAsync($"rest/getCoverArt?{key}&id={song["coverArt"]}"))
            Assert.True(cover.Content.Headers.ContentType?.MediaType?.StartsWith("image/") == true
                        || ErrorOf(JsonNode.Parse(await cover.Content.ReadAsStringAsync())!["subsonic-response"]!) == 70);
        await OkAsync("getLyricsBySongId", key, $"id={id}");
    }

    [TestLibraryFact]
    public async Task Players_keep_your_playlists()
    {
        await ScanAsync();
        var key = $"apiKey={(await _api.CreateApiKeyAsync(new CreateApiKey("Player", [UserPermissions.Listen]))).Key}";
        var ids = (await _api.GetLibraryTracksAsync()).Take(3).Select(t => t.FileId.ToString()).ToList();

        var made = (await OkAsync("createPlaylist", key, $"name=Road%20trip&songId={ids[0]}&songId={ids[1]}"))["playlist"]!;
        var id = (string)made["id"]!;
        Assert.Equal(ids[..2], made["entry"]!.AsArray().Select(e => (string)e!["id"]!));
        Assert.Equal(SignedIn.Username, (string)made["owner"]!);
        // The same playlist in Melodex.
        var mine = Assert.Single(await _api.ListPlaylistsAsync());
        Assert.Equal(("Road trip", 2), (mine.Name, mine.Count));

        await OkAsync("updatePlaylist", key, $"playlistId={id}&name=Drive&comment=Long&songIndexToRemove=0&songIdToAdd={ids[2]}");
        var changed = (await OkAsync("getPlaylist", key, $"id={id}"))["playlist"]!;
        Assert.Equal(("Drive", "Long"), ((string)changed["name"]!, (string)changed["comment"]!));
        Assert.Equal([ids[1], ids[2]], changed["entry"]!.AsArray().Select(e => (string)e!["id"]!));

        // Made again with just these songs.
        var again = (await OkAsync("createPlaylist", key, $"playlistId={id}&songId={ids[0]}"))["playlist"]!;
        Assert.Equal([ids[0]], again["entry"]!.AsArray().Select(e => (string)e!["id"]!));
        Assert.Single((await OkAsync("getPlaylists", key))["playlists"]!["playlist"]!.AsArray());

        await OkAsync("deletePlaylist", key, $"id={id}");
        Assert.Empty(await _api.ListPlaylistsAsync());
        Assert.Equal(70, ErrorOf(await CallAsync("getPlaylist", key, $"id={id}")));
    }

    [TestLibraryFact]
    public async Task Stars_ratings_and_the_play_queue_are_the_same_in_players_and_the_web_app()
    {
        await ScanAsync();
        var key = $"apiKey={(await _api.CreateApiKeyAsync(new CreateApiKey("Player", [UserPermissions.Listen]))).Key}";
        var album = (await OkAsync("getAlbumList2", key, "type=alphabeticalByName&size=500"))["albumList2"]!["album"]!.AsArray().First(a => (int)a!["songCount"]! >= 2)!;
        var albumId = (string)album["id"]!;
        var songs = (await OkAsync("getAlbum", key, $"id={albumId}"))["album"]!["song"]!.AsArray();
        string first = (string)songs[0]!["id"]!, second = (string)songs[1]!["id"]!;
        Assert.Contains("melodex", (await OkAsync("getOpenSubsonicExtensions", key))["openSubsonicExtensions"]!.AsArray().Select(e => (string)e!["name"]!));
        Assert.NotNull(songs[0]!["melodexTrackId"]);
        Assert.NotNull(album["melodexStatus"]);

        // Starred in a player, it's starred in Melodex, by the song's track; and the other way round.
        await OkAsync("star", key, $"id={first}&albumId={albumId}&artistId={album["artistId"]}");
        var marks = await _api.ListMarksAsync();
        Assert.Equal([MarkKind.Song, MarkKind.Album, MarkKind.Artist], marks.Select(m => m.Kind).Order());
        Assert.Equal(Guid.Parse((string)songs[0]!["melodexTrackId"]!), marks.Single(m => m.Kind == MarkKind.Song).Id);
        var starred = (await OkAsync("getStarred2", key))["starred2"]!;
        Assert.Equal(first, (string)starred["song"]![0]!["id"]!);
        Assert.Equal(albumId, (string)starred["album"]![0]!["id"]!);
        Assert.NotNull((await OkAsync("getSong", key, $"id={first}"))["song"]!["starred"]);
        Assert.Equal(albumId, (string)(await OkAsync("getAlbumList2", key, "type=starred"))["albumList2"]!["album"]![0]!["id"]!);
        await _api.SetMarkAsync(MarkKind.Song, Guid.Parse((string)songs[1]!["melodexTrackId"]!), new SetMark(Starred: true));
        Assert.Equal(2, (await OkAsync("getStarred2", key))["starred2"]!["song"]!.AsArray().Count);
        await OkAsync("unstar", key, $"id={first}&id={second}");
        Assert.Null((await OkAsync("getSong", key, $"id={first}"))["song"]!["starred"]);

        // Ratings, 1 to 5, and 0 takes one away.
        await OkAsync("setRating", key, $"id={albumId}&rating=4");
        Assert.Equal(4, (int)(await OkAsync("getAlbum", key, $"id={albumId}"))["album"]!["userRating"]!);
        Assert.Equal(4, (await _api.ListMarksAsync()).Single(m => m.Kind == MarkKind.Album).Rating);
        Assert.Equal(albumId, (string)(await OkAsync("getAlbumList2", key, "type=highest"))["albumList2"]!["album"]![0]!["id"]!);
        Assert.Equal(0, ErrorOf(await CallAsync("setRating", key, $"id={albumId}&rating=6")));
        await OkAsync("setRating", key, $"id={albumId}&rating=0");
        await OkAsync("unstar", key, $"albumId={albumId}&artistId={album["artistId"]}");
        Assert.Empty(await _api.ListMarksAsync());
        Assert.Equal(70, ErrorOf(await CallAsync("star", key, $"id={Guid.NewGuid()}")));

        // The web player's queue is the player's, less what isn't in the library; where it was, in milliseconds.
        var tracks = await _api.GetLibraryTracksAsync();
        var owned = tracks.Take(3).Select(t => new PlayQueueItem(t.TrackId, t.Title, t.ArtistCredit)).ToList();
        var missing = new PlayQueueItem(Guid.NewGuid(), "Not here", "Someone", Missing: true);
        await _api.SavePlayQueueAsync(new SavePlayQueue([owned[0], missing, owned[1], owned[2]], 2, 12.5));
        var queue = (await OkAsync("getPlayQueue", key))["playQueue"]!;
        Assert.Equal(3, queue["entry"]!.AsArray().Count);
        Assert.Equal(tracks[1].FileId.ToString(), (string)queue["current"]!);
        Assert.Equal(12500, (long)queue["position"]!);
        Assert.Equal("web", (string)queue["changedBy"]!);
        Assert.Equal(1, (int)(await OkAsync("getPlayQueueByIndex", key))["playQueueByIndex"]!["currentIndex"]!);

        // A player's queue is the web player's.
        await OkAsync("savePlayQueue", key, $"id={tracks[2].FileId}&id={tracks[0].FileId}&current={tracks[0].FileId}&position=3000");
        var saved = (await _api.GetPlayQueueAsync())!;
        Assert.Equal([tracks[2].TrackId, tracks[0].TrackId], saved.Items.Select(i => i.TrackId));
        Assert.Equal((1, 3.0, "tests"), (saved.Current, saved.Position, saved.ChangedBy));
        Assert.Equal(tracks[2].Title, saved.Items[0].Title);
        await OkAsync("savePlayQueueByIndex", key, $"id={tracks[1].FileId}&currentIndex=0&position=0");
        Assert.Equal(tracks[1].TrackId, (await _api.GetPlayQueueAsync())!.Items.Single().TrackId);
        Assert.Equal(70, ErrorOf(await CallAsync("savePlayQueue", key, $"id={Guid.NewGuid()}")));

        // Saving none clears it.
        await OkAsync("savePlayQueue", key);
        Assert.Null(await _api.GetPlayQueueAsync());
        Assert.Null((await OkAsync("getPlayQueue", key))["playQueue"]!["entry"]);
    }

    private async Task ScanAsync()
    {
        // The test library is shared, so no lyrics are written into it.
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Melodex.Core.Library.SettingsService>().SaveLibraryAsync(new LibrarySettings { FetchLyrics = false }, default);
        var task = await _api.ScanLibraryAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (task.Active && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
            task = (await _api.GetTaskAsync(task.Id))!;
        }
        Assert.True(task.State == TaskState.Succeeded, task.Error);
        await _api.WaitForCatalogueAsync();
    }
}
