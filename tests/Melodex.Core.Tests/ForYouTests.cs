using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Melodex.Client;
using Melodex.Core.Data;
using Melodex.Core.Listening;
using Melodex.Core.Plugins;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>For you (#5): mixes made from what you play, kept as playlists, and artists and albums to go to next.</summary>
public sealed class ForYouTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly MelodexClient _api;

    public ForYouTests()
    {
        DiscoveryCache.Clear();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("Melodex:DataPath", _dataPath).UseSetting("Melodex:ScheduledTasks", "false")
            .ConfigureServices(services => services.Configure<PluginHttpOptions>(http =>
            {
                http.PrimaryHandler = () => new TripHop.FakeListenBrainz();
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
    public async Task Mixes_are_made_from_what_you_play_remade_each_day_and_kept_as_playlists()
    {
        var s = await SeedAsync();

        var mixes = await _api.ListMixesAsync();
        var daily = Assert.Single(mixes, m => m.Kind == MixKind.Daily);
        Assert.Equal("Daily mix 1", daily.Name);
        Assert.Equal(["Massive Attack"], daily.Artists);
        var weekly = Assert.Single(mixes, m => m.Kind == MixKind.Weekly);
        var discoveries = (await _api.GetMixAsync(weekly.Id))!;
        Assert.Contains(discoveries.Songs, x => x.TrackId == s.GloryBox);
        Assert.All(discoveries.Songs, x => Assert.False(x.Owned));

        // Asked again the same day, they're the same mixes.
        Assert.Equal(mixes.Select(m => m.Id), (await _api.ListMixesAsync()).Select(m => m.Id));

        var detail = (await _api.GetMixAsync(daily.Id))!;
        Assert.Equal(daily.Songs, detail.Songs.Count);
        var kept = await _api.KeepMixAsync(daily.Id, new KeepMix());
        Assert.StartsWith("Daily mix 1, ", kept.Playlist.Name);
        Assert.Equal(detail.Songs.Select(x => x.TrackId), kept.Songs.Select(x => x.TrackId!.Value));

        Assert.Contains(kept.Songs, x => x.State == PlaylistSongState.Awaiting);

        // A radio carries on from it.
        var radio = (await _api.NextRadioAsync(new RadioNext(daily.Id, detail.Songs.Select(x => x.TrackId).ToList())))!;
        Assert.Equal(("Daily mix 1", RadioSeedKind.Mix), (radio.Name, radio.Kind));

        // The next day the daily mix is made again.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
            await scope.ServiceProvider.GetRequiredService<MixService>().EnsureAsync(db.Users.Single().Id, DateTime.UtcNow.AddDays(1), default);
        }
        Assert.DoesNotContain(daily.Id, (await _api.ListMixesAsync()).Select(m => m.Id));
        Assert.Null(await _api.GetMixAsync(daily.Id));
    }

    [Fact]
    public async Task Albums_of_your_own_you_havent_played_in_a_while_come_back()
    {
        var s = await SeedAsync();

        Assert.Contains(await _api.ListYourAlbumsToGoBackToAsync(), a => a.Id == s.Mezzanine);
        // Massive Attack are the only artist like them, and you have them already.
        Assert.Empty(await _api.ListArtistsForYouAsync());
        Assert.Null(await _api.GetMixAsync(Guid.NewGuid()));
    }

    /// <summary>The trip-hop catalogue, with Teardrop and Angel played three months ago.</summary>
    private async Task<TripHop.Seeded> SeedAsync()
    {
        var s = await TripHop.SeedAsync(_factory.Services);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        var user = db.Users.Single().Id;
        var then = DateTime.UtcNow.AddDays(-90);
        foreach (var (track, title, i) in new[] { (s.Teardrop, "Teardrop", 0), (s.Angel, "Angel", 1), (s.Teardrop, "Teardrop", 2) })
            db.Plays.Add(new Play
            {
                UserId = user, TrackId = track, Title = title, ArtistCredit = "Massive Attack", ArtistId = s.Massive, AlbumId = s.Mezzanine,
                AlbumTitle = "Mezzanine", PlayedAt = then.AddMinutes(i * 5),
            });
        await db.SaveChangesAsync();
        return s;
    }
}
