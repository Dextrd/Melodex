using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Contracts.Catalogue;
using Melodex.Contracts.Plugins;
using Melodex.Contracts.Plugins.Artwork;
using Melodex.Contracts.Plugins.Connectors;
using Melodex.Contracts.Plugins.Discovery;
using Melodex.Contracts.Plugins.Index;
using Melodex.Contracts.Plugins.Lyrics;
using Melodex.Contracts.Plugins.Remote;
using Melodex.Core.Plugins;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>
/// Plugins of every kind that don't come with Melodex, added by their address and called over the HTTP contract for
/// their kind, just as one loaded in process is.
/// </summary>
public sealed class RemotePluginTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private readonly FakePlugins _plugins = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly MelodexClient _api;

    public RemotePluginTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Melodex:DataPath", _dataPath).UseSetting("Melodex:ScheduledTasks", "false")
            .ConfigureServices(services =>
            {
                services.Configure<PluginHttpOptions>(http =>
                {
                    http.PrimaryHandler = () => _plugins;
                    http.RateLimit = false;
                    http.RetryDelay = TimeSpan.FromMilliseconds(1);
                });
                services.AddHttpClient(RetrievalPlugins.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _plugins);
            }));
        _api = new MelodexClient(_factory.CreateSignedInClient());
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(_dataPath);
    }

    private static PluginManifest Manifest(string id, PluginType type, params string[] capabilities) => new()
    {
        Id = id, Name = id, Version = "1.0.0", Type = type, ContractVersion = ContractVersions.For(type).ToString(), Capabilities = capabilities,
        SettingsSchema = JsonDocument.Parse("""{"type":"object","properties":{"token":{"type":"string","default":"abc"}}}""").RootElement,
    };

    private async Task<T> RunningAsync<T>(Func<IServiceProvider, Task<T>> get)
    {
        using var scope = _factory.Services.CreateScope();
        return await get(scope.ServiceProvider);
    }

    [Fact]
    public async Task An_address_that_isnt_a_plugins_says_what_it_seems_to_be()
    {
        // The service a plugin uses, such as slskd's web page, given where the plugin's own address is wanted.
        _plugins.WebPages.Add("slskd");
        var page = await Assert.ThrowsAsync<MelodexApiException>(() => _api.AddPluginAsync(new AddPlugin(Endpoint: "http://slskd:5030", Type: PluginType.Retrieval)));
        Assert.Contains("not as a Melodex plugin", page.Message);
        Assert.Contains("give the plugin's own address here", page.Message);

        var missing = await Assert.ThrowsAsync<MelodexApiException>(() => _api.AddPluginAsync(new AddPlugin(Endpoint: "http://retriever-nowhere:8080", Type: PluginType.Retrieval)));
        Assert.Contains("Couldn't find retriever-nowhere. Is the plugin's container running", missing.Message);
    }

    [Fact]
    public async Task A_plugin_whose_settings_are_required_is_added_before_theyre_filled_in()
    {
        _plugins.Add("my-strict", Manifest("example.strict", PluginType.Lyrics) with
        {
            SettingsSchema = JsonDocument.Parse("""{"type":"object","required":["token"],"properties":{"token":{"type":"string"},"size":{"type":"integer","minimum":1}}}""").RootElement,
        });
        var added = await _api.AddPluginAsync(new AddPlugin(Endpoint: "http://my-strict:8080", Type: PluginType.Lyrics));
        Assert.Equal("example.strict", added.PluginId);

        // What's given is still checked.
        var wrong = await Assert.ThrowsAsync<MelodexApiException>(() => _api.AddPluginAsync(new AddPlugin(Endpoint: "http://my-strict:8080", Type: PluginType.Lyrics,
            Settings: JsonDocument.Parse("""{"size":0}""").RootElement)));
        Assert.DoesNotContain("Required", wrong.Message);
    }

    [Fact]
    public async Task A_plugin_is_added_by_its_address_only_to_its_own_kind_and_a_contract_this_Melodex_has()
    {
        _plugins.Add("my-lyrics", Manifest("example.lyrics", PluginType.Lyrics));
        var wrong = await Assert.ThrowsAsync<MelodexApiException>(() => _api.AddPluginAsync(new AddPlugin(Endpoint: "http://my-lyrics:8080", Type: PluginType.Artwork)));
        Assert.Contains("is a lyrics plugin, not an artwork plugin", wrong.Message);

        _plugins.Add("my-future", Manifest("example.future", PluginType.Artwork) with { ContractVersion = "2.0" });
        var future = await Assert.ThrowsAsync<MelodexApiException>(() => _api.AddPluginAsync(new AddPlugin(Endpoint: "http://my-future:8080", Type: PluginType.Artwork)));
        Assert.Contains("built for artwork contract 2.0", future.Message);

        var added = await _api.AddPluginAsync(new AddPlugin(Endpoint: "http://my-lyrics:8080/", Type: PluginType.Lyrics));
        Assert.Equal((PluginType.Lyrics, "http://my-lyrics:8080", "example.lyrics"), (added.Type, added.Endpoint, added.PluginId));

        // Testing asks the plugin, with its settings, and picks up what it says about itself now.
        _plugins.Add("my-lyrics", Manifest("example.lyrics", PluginType.Lyrics) with { Version = "1.1.0" });
        var tested = await _api.TestPluginAsync(added.Id);
        Assert.Equal(HealthStatus.Healthy, tested.Health);
        Assert.Equal("1.1.0", tested.Manifest!.Version);
        Assert.Equal("abc", _plugins.Calls.Last(c => c.Path == "/v1/health").Body.GetProperty("settings").GetProperty("token").GetString());
    }

    [Fact]
    public async Task Lyrics_and_artwork_come_from_plugins_at_addresses()
    {
        _plugins.Add("my-lyrics", Manifest("example.lyrics", PluginType.Lyrics, LyricsCapabilities.Synced));
        _plugins.Add("my-art", Manifest("example.art", PluginType.Artwork, ArtworkCapabilities.Albums));
        _plugins.Answer("my-lyrics", "/v1/find", call => call.GetProperty("input").GetProperty("title").GetString() == "Glory Box"
            ? new FoundLyrics("[00:01.00] Glory box", null)
            : null);
        _plugins.Answer("my-art", "/v1/find", _ => new[] { new ArtworkCandidate("cover-1") });
        _plugins.Files["my-art"] = ([1, 2, 3], "image/png");
        await _api.AddPluginAsync(new AddPlugin(Endpoint: "http://my-lyrics:8080", Type: PluginType.Lyrics));
        await _api.AddPluginAsync(new AddPlugin(Endpoint: "http://my-art:8080", Type: PluginType.Artwork));

        var lyrics = await RunningAsync(async s => (await s.GetRequiredService<LyricsPlugins>().GetAsync(default)).Single(p => p.Instance.Endpoint is not null).Plugin);
        Assert.Equal("[00:01.00] Glory box", (await lyrics.FindAsync(new LyricsQuery("Glory Box", "Portishead", null, TimeSpan.FromMinutes(5), []), default))?.Synced);
        Assert.Null(await lyrics.FindAsync(new LyricsQuery("Roads", "Portishead", null, null, []), default));
        // Searching wasn't declared, so it isn't asked.
        Assert.Empty(await lyrics.SearchAsync(new LyricsSearch("glory", null), default));
        Assert.DoesNotContain(_plugins.Calls, c => c.Path == "/v1/search");

        var artwork = await RunningAsync(async s => (await s.GetRequiredService<ArtworkPlugins>().ForAsync(ArtworkSubject.Album, default)).Single(p => p.Instance.Endpoint is not null).Plugin);
        var found = Assert.Single(await artwork.FindAsync(new ArtworkQuery(ArtworkSubject.Album, "Dummy", "Portishead", [], [], []), default));
        var image = await artwork.DownloadAsync(found, default);
        Assert.Equal("image/png", image?.ContentType);
        Assert.Equal([1, 2, 3], image?.Data);
        // It only pictures what it says it can.
        Assert.DoesNotContain(await RunningAsync(s => s.GetRequiredService<ArtworkPlugins>().ForAsync(ArtworkSubject.Artist, default)), p => p.Instance.Endpoint is not null);
    }

    [Fact]
    public async Task Discovery_comes_from_a_plugin_at_an_address()
    {
        _plugins.Add("my-discovery", Manifest("example.discovery", PluginType.Discovery, DiscoveryCapabilities.SimilarArtists));
        _plugins.Answer("my-discovery", "/v1/similar-artists", call =>
        {
            var input = call.GetProperty("input");
            Assert.Equal(("Portishead", 5), (input.GetProperty("artist").GetProperty("name").GetString(), input.GetProperty("count").GetInt32()));
            return new[] { new DiscoveredArtist("Massive Attack", [new ProviderId(Providers.MusicBrainz, "10adbe5e-a2c0-4bf3-8249-2b4cbf6e6ca8")], 0.9) };
        });
        await _api.AddPluginAsync(new AddPlugin(Endpoint: "http://my-discovery:8080", Type: PluginType.Discovery));

        var discovery = await RunningAsync(async s => (await s.GetRequiredService<DiscoveryPlugins>().GetAsync(default)).Single(p => p.Instance.Endpoint is not null).Plugin);
        var similar = Assert.Single(await discovery.SimilarArtistsAsync(new DiscoveryArtist("Portishead", []), 5, default));
        Assert.Equal(("Massive Attack", 0.9), (similar.Name, similar.Score));
        // Only what it declares is asked.
        Assert.Empty(await discovery.RecommendAsync(new DiscoveryListener(new Dictionary<string, string>(), [], []), 5, default));
        Assert.DoesNotContain(_plugins.Calls, c => c.Path == "/v1/recommendations");
    }

    [Fact]
    public async Task A_catalogue_at_an_address_answers_with_its_own_ids_and_can_be_unavailable()
    {
        var manifest = Manifest("example.catalogue", PluginType.Index, IndexCapabilities.RecordingAlbums) with { Provider = "example" };
        _plugins.Add("my-catalogue", manifest);
        var artist = new IndexArtist(new ProviderId("example", "a1"), "Portishead");
        _plugins.Answer("my-catalogue", "/v1/search", call =>
        {
            Assert.Equal(["artist"], call.GetProperty("input").GetProperty("kinds").EnumerateArray().Select(k => k.GetString()));
            return new IndexSearchResults([artist], [], []);
        });
        _plugins.Answer("my-catalogue", "/v1/artist", call => call.GetProperty("input").GetProperty("id").GetString() == "a1" ? artist : null);
        await _api.AddPluginAsync(new AddPlugin(Endpoint: "http://my-catalogue:8080", Type: PluginType.Index));

        var catalogue = await RunningAsync(async s => (await s.GetRequiredService<IndexPlugins>().GetAllAsync(default)).Single(c => c.Instance.Endpoint is not null));
        Assert.Equal("example", catalogue.Provider);
        var results = await catalogue.Plugin.SearchAsync(new IndexSearchQuery("portishead", new HashSet<SearchKind> { SearchKind.Artist }), default);
        Assert.Equal(artist, Assert.Single(results.Artists));
        Assert.Equal("Portishead", (await catalogue.Plugin.GetArtistAsync("a1", default))?.Name);
        Assert.Null(await catalogue.Plugin.GetArtistAsync("a2", default));

        // An ISRC lookup isn't declared, so it finds nothing without asking.
        Assert.Empty(await ((IIsrcLookup)catalogue.Plugin).LookupIsrcAsync("GBAAA0000001", default));
        Assert.DoesNotContain(_plugins.Calls, c => c.Path == "/v1/isrc");

        // A catalogue that's busy is left for later, as one loaded in process is.
        _plugins.Unavailable.Add("my-catalogue");
        await Assert.ThrowsAsync<IndexUnavailableException>(() => catalogue.Plugin.GetAlbumAsync("b1", default));
    }

    [Fact]
    public async Task A_media_server_at_an_address_offers_the_features_it_declares()
    {
        _plugins.Add("my-server", Manifest("example.server", PluginType.Connector, ConnectorCapabilities.UserPlaylists, ConnectorCapabilities.ServerIdentity));
        _plugins.Answer("my-server", "/v1/resolve", call => call.GetProperty("input").GetProperty("path").GetString() == "Portishead/Dummy/01 Mysterons.flac" ? "song-1" : null);
        _plugins.Answer("my-server", "/v1/server", _ => new ServerIdentity("example", "2.0"));
        _plugins.Answer("my-server", "/v1/playlists/list", call => new[] { new UserPlaylist("p1", call.GetProperty("input").GetProperty("login").GetProperty("username").GetString()!, null, 3, null) });
        var added = await _api.AddPluginAsync(new AddPlugin(Endpoint: "http://my-server:8080", Type: PluginType.Connector));

        var connector = await RunningAsync(async s => (await s.GetRequiredService<ConnectorPlugins>().GetActiveAsync(default))!.Value.Connector);
        Assert.Equal("song-1", await connector.ResolveTrackAsync(new TrackLookup(Path: "Portishead/Dummy/01 Mysterons.flac"), default));
        Assert.Null(await connector.ResolveTrackAsync(new TrackLookup(Path: "Portishead/Dummy/02 Sour Times.flac"), default));
        await connector.TriggerScanAsync(["Portishead/Dummy"], default);
        Assert.Equal(["Portishead/Dummy"], _plugins.Calls.Single(c => c.Path == "/v1/scan").Body.GetProperty("input").GetProperty("paths").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(new ServerIdentity("example", "2.0"), await Assert.IsAssignableFrom<IServerIdentity>(connector).GetServerAsync(default));
        var playlists = Assert.IsAssignableFrom<IPlaylistSync>(connector);
        Assert.True(playlists.SyncsPlaylists);
        Assert.Equal("ann", Assert.Single(await playlists.GetPlaylistsAsync(new ServerLogin("ann", "secret"), default)).Name);

        // Without them declared, Melodex doesn't offer them.
        await _api.RemovePluginAsync(added.Id);
        _plugins.Add("plain-server", Manifest("example.plain", PluginType.Connector));
        await _api.AddPluginAsync(new AddPlugin(Endpoint: "http://plain-server:8080", Type: PluginType.Connector));
        var plain = await RunningAsync(async s => (await s.GetRequiredService<ConnectorPlugins>().GetActiveAsync(default))!.Value.Connector);
        Assert.False(plain is IServerIdentity or IPlaylistSync);
    }

    /// <summary>
    /// Plugins at addresses, each a host: they serve their manifest, are healthy, and answer calls as told, or 404.
    /// Every call is kept, to check what was sent.
    /// </summary>
    private sealed class FakePlugins : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, PluginManifest> _manifests = new();
        private readonly ConcurrentDictionary<(string Host, string Path), Func<JsonElement, object?>> _answers = new();

        public ConcurrentQueue<(string Host, string Path, JsonElement Body)> Calls { get; } = new();
        public ConcurrentDictionary<string, (byte[] Data, string ContentType)> Files { get; } = new();
        public ConcurrentBag<string> Unavailable { get; } = [];

        /// <summary>Hosts that answer everything with a web page, as a service's own web interface does.</summary>
        public ConcurrentBag<string> WebPages { get; } = [];

        public void Add(string host, PluginManifest manifest) => _manifests[host] = manifest;

        public void Answer(string host, string path, Func<JsonElement, object?> answer) => _answers[(host, path)] = answer;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            var path = request.RequestUri.AbsolutePath;
            if (WebPages.Contains(host))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<!DOCTYPE html><html></html>", Encoding.UTF8, "text/html") };
            if (!_manifests.TryGetValue(host, out var manifest))
                throw new HttpRequestException(HttpRequestError.NameResolutionError, $"Name or service not known ({host}:{request.RequestUri.Port})");
            if (path == "/v1/manifest")
                return Json(manifest);
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            Calls.Enqueue((host, path, body));
            if (Unavailable.Contains(host))
                return Json(new RemoteError("Too busy", RemoteErrorCodes.Unavailable), HttpStatusCode.BadGateway);
            if (path == "/v1/health")
                return Json(HealthReport.Healthy());
            if (path == "/v1/download" && Files.TryGetValue(host, out var file))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file.Data) { Headers = { ContentType = new(file.ContentType) } } };
            if (path == "/v1/scan")
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            return _answers.TryGetValue((host, path), out var answer) && answer(body) is { } value
                ? Json(value)
                : Json(new RemoteError("Not found"), HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(value, value.GetType(), RemoteJson.Options), Encoding.UTF8, "application/json") };
    }
}
