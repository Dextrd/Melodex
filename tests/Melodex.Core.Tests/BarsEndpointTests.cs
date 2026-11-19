using System.Net;
using Melodex.Client;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>A song's spectrum as the player fetches it: by a song's stream URL, while the visualiser is on (#89, #102).</summary>
[Collection(RequestCollection.Name)]
public sealed class BarsEndpointTests(RequestFixture fixture)
{
    private MelodexClient Api => fixture.Api;

    [TestLibraryFact]
    public async Task A_songs_bars_come_by_its_stream_url_unless_the_visualiser_is_off()
    {
        var track = (await Api.GetLibraryTracksAsync()).First(t => t.Path.EndsWith(".flac"));
        var play = (await Api.GetFilePlayInfoAsync(track.FileId))!;
        var url = MelodexClient.SpectrumUrl(play.StreamUrl);
        Assert.Contains($"library/files/{track.FileId}/spectrum?", url);
        // As an audio element's would be: signed, without a login.
        using var http = fixture.Factory.CreateClient();
        var playback = await Api.GetPlaybackAsync();
        Assert.Equal((VisualiserBars.OnDemand, GaplessPlayback.SameAlbum), (playback.Visualiser, playback.Gapless));

        using (var bars = await http.GetAsync(url))
        {
            Assert.Equal(HttpStatusCode.OK, bars.StatusCode);
            Assert.Contains("gzip", bars.Content.Headers.ContentEncoding);
            var body = await bars.Content.ReadAsByteArrayAsync();
            Assert.True(Melodex.Core.Listening.SpectrumFormat.IsSpectrum(new MemoryStream(body)));
        }
        // With fewer bands, but no number of them that isn't a choice.
        using (var sixteen = await http.GetAsync(url + "&bands=16"))
        {
            Assert.Equal(HttpStatusCode.OK, sixteen.StatusCode);
            var bytes = Melodex.Core.Listening.SpectrumFormat.Frames(await sixteen.Content.ReadAsByteArrayAsync());
            Assert.Equal(0, bytes.Length % 21);
        }
        using (var twelve = await http.GetAsync(url + "&bands=12"))
            Assert.Equal(HttpStatusCode.BadRequest, twelve.StatusCode);
        // Version 1 still comes at /bars, for apps written for it.
        using (var old = await http.GetAsync(MelodexClient.BarsUrl(play.StreamUrl)))
        {
            Assert.Equal(HttpStatusCode.OK, old.StatusCode);
            using var gzip = new System.IO.Compression.GZipStream(await old.Content.ReadAsStreamAsync(), System.IO.Compression.CompressionMode.Decompress);
            var header = new byte[8];
            gzip.ReadExactly(header);
            Assert.Equal("MDXB\x01\x1e\x40\0"u8.ToArray(), header);
        }
        using (var forged = await http.GetAsync(url.Replace("sig=", "sig=x")))
            Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        using (var warmed = await http.GetAsync(url + "&warm=true"))
            Assert.Equal(HttpStatusCode.Accepted, warmed.StatusCode);

        try
        {
            await Api.SavePlaybackSettingsAsync(new SavePlayback(VisualiserBars.Off, Gapless: GaplessPlayback.Always));
            playback = await Api.GetPlaybackAsync();
            Assert.Equal((VisualiserBars.Off, GaplessPlayback.Always), (playback.Visualiser, playback.Gapless));
            using var off = await http.GetAsync(url);
            Assert.Equal(HttpStatusCode.NotFound, off.StatusCode);
            var status = await Api.GetPlaybackSettingsAsync();
            Assert.True(status.Songs > 0);
            Assert.True(status.EstimatedBytes > 0);
            Assert.Equal(0, status.BarsFiles);
        }
        finally
        {
            await Api.SavePlaybackSettingsAsync(new SavePlayback(VisualiserBars.OnDemand));
        }
    }
}
