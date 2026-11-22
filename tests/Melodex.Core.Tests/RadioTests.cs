using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Core.Data;
using Melodex.Core.Listening;
using Melodex.Core.Plugins;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>Radio (#4): songs like a song, album, artist or playlist, without end, from the discovery plugins and the library.</summary>
public sealed class RadioTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;
    private readonly MelodexClient _api;

    public RadioTests()
    {
        DiscoveryCache.Clear();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Melodex:DataPath", _dataPath).UseSetting("Melodex:ScheduledTasks", "false")
            .ConfigureServices(services => services.Configure<PluginHttpOptions>(http =>
            {
                http.PrimaryHandler = () => new TripHop.FakeListenBrainz();
                http.RateLimit = false;
            })));
        _http = _factory.CreateSignedInClient();
        _api = new MelodexClient(_http);
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(_dataPath);
        DiscoveryCache.Clear();
    }

    [Fact]
    public async Task A_radio_from_a_song_plays_it_then_songs_like_it_with_your_share_of_ones_you_dont_have()
    {
        var s = await SeedAsync();
        Assert.Equal(new RadioSettings(25, true), await _api.GetRadioSettingsAsync());

        // Only songs you have: Glory Box first, as it was started from, then Massive Attack's, found like it or by an artist like Portishead.
        await ShareAsync(0);
        var batch = (await _api.NextRadioAsync(new RadioNext(s.GloryBox)))!;
        Assert.Equal(("Glory Box", RadioSeedKind.Song), (batch.Name, batch.Kind));
        Assert.Equal(s.GloryBox, batch.Songs[0].TrackId);
        Assert.Equal(new[] { s.Angel, s.Teardrop }.Order(), batch.Songs.Skip(1).Select(x => x.TrackId).Order());
        Assert.All(batch.Songs.Skip(1), x => Assert.True(x.Owned));

        // All songs you don't have: Overcome, then, with nothing else like it, songs you have.
        await ShareAsync(100);
        batch = (await _api.NextRadioAsync(new RadioNext(s.GloryBox, [s.GloryBox])))!;
        Assert.Equal(s.Overcome, batch.Songs[0].TrackId);
        Assert.False(batch.Songs[0].Owned);
        Assert.DoesNotContain(batch.Songs, x => x.TrackId == s.GloryBox);
        // Unless only those you have are asked for, as Subsonic players do.
        batch = (await _api.NextRadioAsync(new RadioNext(s.GloryBox, [s.GloryBox], OwnedOnly: true)))!;
        Assert.DoesNotContain(batch.Songs, x => x.TrackId == s.Overcome);

        // What it's queued doesn't come again.
        batch = (await _api.NextRadioAsync(new RadioNext(s.GloryBox, [s.GloryBox, s.Overcome, s.Teardrop])))!;
        Assert.Equal([s.Angel], batch.Songs.Select(x => x.TrackId));

        Assert.Null(await _api.NextRadioAsync(new RadioNext(Guid.NewGuid())));
        var invalid = await _http.PutAsJsonAsync("api/v1/settings/fetch", await _api.GetFetchSettingsAsync() with { RadioMissingPercent = 150 },
            MelodexJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        await _api.SaveFetchSettingsAsync(await _api.GetFetchSettingsAsync() with { RadioMissingPercent = 10, RadioAutoplay = false });
        Assert.Equal(new RadioSettings(10, false), await _api.GetRadioSettingsAsync());
    }

    [Fact]
    public async Task The_share_is_a_target_over_the_whole_radio()
    {
        var s = await SeedAsync();
        await ShareAsync(50);

        // Two songs played, both ones you have: the next one is one you don't, to make up for them.
        var batch = (await _api.NextRadioAsync(new RadioNext(s.GloryBox, [s.GloryBox, s.Teardrop, s.Angel], Take: 1)))!;
        Assert.Equal([s.Overcome], batch.Songs.Select(x => x.TrackId));
    }

    [Fact]
    public async Task A_song_skipped_quickly_isnt_played_again_and_its_artist_comes_up_less()
    {
        var s = await SeedAsync();
        await ShareAsync(0);
        await _api.SkipRadioSongAsync(s.Teardrop);

        var batch = (await _api.NextRadioAsync(new RadioNext(s.GloryBox, [s.GloryBox])))!;
        Assert.Equal([s.Angel], batch.Songs.Select(x => x.TrackId));
        using var scope = _factory.Services.CreateScope();
        var skip = scope.ServiceProvider.GetRequiredService<MelodexDbContext>().RadioSkips.Single();
        Assert.Equal((s.Teardrop, s.Massive), (skip.TrackId, skip.ArtistId));
    }

    [Fact]
    public async Task Radios_start_from_albums_artists_and_your_playlists_and_subsonic_players_mix_from_them()
    {
        var s = await SeedAsync();
        await ShareAsync(0);

        var album = (await _api.NextRadioAsync(new RadioNext(s.Mezzanine)))!;
        Assert.Equal(("Mezzanine", RadioSeedKind.Album), (album.Name, album.Kind));
        Assert.NotEmpty(album.Songs);
        var artist = (await _api.NextRadioAsync(new RadioNext(s.Massive)))!;
        Assert.Equal(("Massive Attack", RadioSeedKind.Artist), (artist.Name, artist.Kind));
        Assert.NotEmpty(artist.Songs);

        var playlist = await _api.CreatePlaylistAsync(new CreatePlaylist("Night", Songs: [new PlaylistItem(s.Teardrop)]));
        var fromPlaylist = (await _api.NextRadioAsync(new RadioNext(playlist.Playlist.Id)))!;
        Assert.Equal(("Night", RadioSeedKind.Playlist), (fromPlaylist.Name, fromPlaylist.Kind));
        Assert.Contains(fromPlaylist.Songs, x => x.TrackId == s.Angel);

        // A Subsonic player's instant mix, from a song by its file: songs you have only, not the song itself.
        var file = await FileOfAsync(s.Teardrop);
        var key = (await _api.CreateApiKeyAsync(new CreateApiKey("Player", [UserPermissions.Listen]))).Key;
        var answer = JsonNode.Parse(await _factory.CreateClient().GetStringAsync($"rest/getSimilarSongs?id={file}&count=10&f=json&v=1.16.1&c=test&apiKey={key}"))!;
        var songs = answer["subsonic-response"]!["similarSongs"]!["song"]!.AsArray().Select(n => n!["title"]!.GetValue<string>()).ToList();
        Assert.Contains("Angel", songs);
        Assert.DoesNotContain("Teardrop", songs);
        Assert.DoesNotContain("Overcome", songs);
    }

    [Fact]
    public void The_share_of_songs_you_dont_have_comes_out_right_over_time()
    {
        Assert.Equal(1, RadioService.Share(4, 0.25));
        Assert.Equal(0, RadioService.Share(5, 0));
        Assert.Equal(5, RadioService.Share(5, 1));
        var total = Enumerable.Range(0, 2000).Sum(_ => RadioService.Share(5, 0.25));
        Assert.InRange(total / 2000.0, 1.15, 1.35);
    }

    private async Task ShareAsync(int percent) =>
        await _api.SaveFetchSettingsAsync(await _api.GetFetchSettingsAsync() with { RadioMissingPercent = percent });

    private Task<TripHop.Seeded> SeedAsync() => TripHop.SeedAsync(_factory.Services);

    private async Task<Guid> FileOfAsync(Guid track)
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<MelodexDbContext>().LibraryFiles.Single(f => f.TrackId == track).Id;
    }
}
