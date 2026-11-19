using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Melodex.Client;
using Melodex.Core.Data;
using Melodex.Core.Library;
using Melodex.Core.Listening;
using Melodex.Core.Tasks;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>The spectrum the visualiser's bars are drawn from (#89, #102): worked out from each song, kept on demand or beside it.</summary>
public sealed class BarsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "melodex-tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly MelodexDbContext _db;
    private readonly MelodexOptions _options;

    public BarsTests()
    {
        _options = new MelodexOptions { LibraryPath = Path.Combine(_root, "music"), DataPath = Path.Combine(_root, "data") };
        Directory.CreateDirectory(_options.LibraryPath);
        _connection.Open();
        _db = new MelodexDbContext(new DbContextOptionsBuilder<MelodexDbContext>().UseSqlite(_connection, MelodexDbContext.Sqlite).Options);
        _db.Database.Migrate();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private static float[] Tone(double hz, double seconds) =>
        Enumerable.Range(0, (int)(seconds * SpectrumFormat.Rate)).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / SpectrumFormat.Rate))).ToArray();

    private static byte[] Frames(byte[] file) => SpectrumFormat.Frames(file);

    private static byte[] Unzipped(byte[] file)
    {
        using var gzip = new GZipStream(new MemoryStream(file), CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Frame(byte[] frames, int index) => frames.AsSpan(index * SpectrumFormat.FrameSize, SpectrumFormat.FrameSize).ToArray();

    [Fact]
    public void A_tone_lights_the_band_its_pitch_is_in_unsmoothed_and_silence_none()
    {
        var analyser = new SpectrumAnalyser();
        // Fed in pieces, as ffmpeg's output comes, split mid-frame.
        var tone = Tone(1000, 2);
        foreach (var piece in tone.Chunk(1000))
            analyser.Add(piece);
        var frames = analyser.Finish();
        // A frame for each moment, from the start to the end.
        Assert.Equal(0, frames.Length % SpectrumFormat.FrameSize);
        Assert.InRange(frames.Length / SpectrumFormat.FrameSize, 2 * SpectrumFormat.Fps, 2 * SpectrumFormat.Fps + 2);
        // Nothing has sounded at the very start.
        Assert.All(Frame(frames, 0), b => Assert.Equal(0, b));
        // A second in, 1 kHz is loudest, at about its height less the window's spread, and the bass and highs are quiet.
        var second = Frame(frames, SpectrumFormat.Fps)[..SpectrumFormat.Bands];
        var loudest = Array.IndexOf(second, second.Max());
        Assert.InRange(loudest, 32, 37);
        Assert.InRange(SpectrumFormat.Decibels(second[loudest]), -8, -5);
        Assert.True(second[0] < second[loudest] / 2 && second[^1] < second[loudest] / 2);
        // Unsmoothed: already at its height a frame after it's fully in the window, as it is a second in.
        var settled = (SpectrumFormat.Size + SpectrumFormat.Rate / SpectrumFormat.Fps - 1) / (SpectrumFormat.Rate / SpectrumFormat.Fps);
        Assert.InRange(Frame(frames, settled)[loudest], second[loudest] - 2, second[loudest] + 2);
        // A sine at half height: -9 decibels' loudness, -6 at its peak, and a waveform from -64 to 64.
        var extras = Frame(frames, SpectrumFormat.Fps)[SpectrumFormat.Bands..];
        Assert.InRange(SpectrumFormat.Decibels(extras[0]), -9.5, -8.5);
        Assert.InRange(SpectrumFormat.Decibels(extras[1]), -6.5, -5.5);
        Assert.InRange((sbyte)extras[2], -64, -62);
        Assert.InRange((sbyte)extras[3], 62, 64);
        // Its start is a beat, but holding steady it has no more.
        Assert.All(Enumerable.Range(4, frames.Length / SpectrumFormat.FrameSize - 4), f => Assert.Equal(0, Frame(frames, f)[^1]));

        var silence = new SpectrumAnalyser();
        silence.Add(new float[SpectrumFormat.Rate]);
        Assert.All(silence.Finish(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Beats_are_marked_where_a_kick_lands()
    {
        // A kick twice a second, 120 a minute, over a quiet hum: a burst of 60 Hz dying away.
        var rate = SpectrumFormat.Rate;
        var samples = new float[rate * 6];
        for (var i = 0; i < samples.Length; i++)
        {
            var since = i % (rate / 2);
            samples[i] = (float)(0.05 * Math.Sin(2 * Math.PI * 3000 * i / rate) + 0.8 * Math.Exp(-since / (0.05 * rate)) * Math.Sin(2 * Math.PI * 60 * since / rate));
        }
        var analyser = new SpectrumAnalyser();
        analyser.Add(samples);
        var frames = analyser.Finish();
        var beats = Enumerable.Range(0, frames.Length / SpectrumFormat.FrameSize).Where(f => Frame(frames, f)[^1] > 0).ToList();
        // One for each kick after the first, close after it lands: 15 frames apart.
        Assert.InRange(beats.Count, 10, 12);
        Assert.All(beats, f => Assert.InRange((f * (rate / SpectrumFormat.Fps)) % (rate / 2), 0, rate / 5));
    }

    [Fact]
    public void A_spectrum_says_what_it_is_and_other_files_dont_pass_for_one()
    {
        var path = Path.Combine(_root, "a.bars");
        File.WriteAllBytes(path, SpectrumFormat.Write(new byte[SpectrumFormat.FrameSize * 3]));
        Assert.True(SpectrumFormat.IsSpectrum(path));
        Assert.Equal(SpectrumFormat.FrameSize * 3, Frames(File.ReadAllBytes(path)).Length);
        // The header says how to read it.
        var header = Unzipped(File.ReadAllBytes(path))[..SpectrumFormat.HeaderSize];
        Assert.Equal("MDXB"u8.ToArray().Concat(new byte[] { 2, 30, 64, 16, 40, 0, 0x80, 0x3e, 0xa0, 0, 69, 0 }), header);

        File.WriteAllBytes(path, "not bars"u8.ToArray());
        Assert.False(SpectrumFormat.IsSpectrum(path));
        // Version 1, beside songs from before, is made again.
        using (var gzip = new GZipStream(File.Create(path), CompressionLevel.Fastest))
        {
            gzip.Write("MDXB\x01\x1e\x40\0"u8);
            gzip.Write(new byte[64]);
        }
        Assert.False(SpectrumFormat.IsSpectrum(path));
        Assert.False(SpectrumFormat.IsSpectrum(Path.Combine(_root, "missing.bars")));
    }

    [Fact]
    public void A_spectrum_comes_with_fewer_bands_or_as_version_1()
    {
        var analyser = new SpectrumAnalyser();
        // Quiet enough for version 1's narrower range.
        analyser.Add(Tone(1000, 1).Select(s => s / 50).ToArray());
        var file = SpectrumFormat.Write(analyser.Finish());
        var count = Frames(file).Length / SpectrumFormat.FrameSize;

        var eight = Unzipped(SpectrumFormat.WithBands(file, 8));
        Assert.Equal((8, 13), (eight[6], (int)eight[14]));
        Assert.Equal(count * 13, eight.Length - SpectrumFormat.HeaderSize);
        // Each the loudest of the eight it covers, the rest of the frame as it was.
        var full = Frame(Frames(file), SpectrumFormat.Fps / 2);
        var fewer = eight.AsSpan(SpectrumFormat.HeaderSize + SpectrumFormat.Fps / 2 * 13, 13).ToArray();
        Assert.Equal(full[32..40].Max(), fewer[4]);
        Assert.Equal(full[SpectrumFormat.Bands..], fewer[8..]);
        Assert.Throws<ArgumentOutOfRangeException>(() => SpectrumFormat.WithBands(file, 12));

        var old = Unzipped(SpectrumFormat.AsVersion1(file));
        Assert.Equal("MDXB\x01\x1e\x40\0"u8.ToArray(), old[..8]);
        Assert.Equal(count * 64, old.Length - 8);
        // 1 kHz falls about where it did, smoothed up to its height.
        var bars = old.AsSpan(8 + (count - 1) * 64, 64).ToArray();
        Assert.InRange(Array.IndexOf(bars, bars.Max()), 36, 44);
        Assert.True(old[8 + 64 * 2 + Array.IndexOf(bars, bars.Max())] < bars.Max());
    }

    private async Task<string> SongAsync(string name, double seconds = 3)
    {
        var path = Path.Combine(_options.LibraryPath, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}", path])
               { RedirectStandardError = true })!)
            await ffmpeg.WaitForExitAsync();
        Assert.True(File.Exists(path));
        _db.LibraryFiles.Add(new LibraryFile { Path = name, Size = new FileInfo(path).Length, Duration = TimeSpan.FromSeconds(seconds) });
        await _db.SaveChangesAsync();
        return path;
    }

    private SpectrumMaker Maker() => new(Options.Create(_options), NullLogger<SpectrumMaker>.Instance);

    [FfmpegFact]
    public async Task Bars_made_on_demand_are_kept_and_made_once()
    {
        var song = await SongAsync("tone.flac");
        var maker = Maker();
        var made = await Task.WhenAll(maker.GetAsync(song, null, default), maker.GetAsync(song, null, default));
        Assert.Equal(made[0], made[1]);
        Assert.InRange(Frames(made[0]).Length / SpectrumFormat.FrameSize, 3 * SpectrumFormat.Fps, 3 * SpectrumFormat.Fps + 2);
        // Kept in the data folder, once.
        var kept = Assert.Single(Directory.GetFiles(Path.Combine(_options.DataPath, "bars")));
        Assert.True(SpectrumFormat.IsSpectrum(kept));

        // Bars beside the song are used when there are some.
        var sidecar = SpectrumFormat.SidecarOf(song);
        var own = SpectrumFormat.Write(new byte[SpectrumFormat.FrameSize]);
        File.WriteAllBytes(sidecar, own);
        Assert.Equal(own, await maker.GetAsync(song, sidecar, default));

        // A file that isn't a song can't be.
        var notSong = Path.Combine(_options.LibraryPath, "notes.flac");
        File.WriteAllText(notSong, "hello");
        await Assert.ThrowsAsync<SpectrumException>(() => maker.GetAsync(notSong, null, default));
        // Nor can any without ffmpeg.
        var without = new SpectrumMaker(Options.Create(new MelodexOptions { DataPath = _options.DataPath, FfmpegPath = "/nowhere/ffmpeg" }), NullLogger<SpectrumMaker>.Instance);
        var error = await Assert.ThrowsAsync<SpectrumException>(() => without.MakeAsync(song, default));
        Assert.Contains("no ffmpeg", error.Message);
    }

    [FfmpegFact]
    public async Task Made_when_matching_every_song_gets_bars_beside_it_until_theyre_deleted()
    {
        var songs = new[] { await SongAsync("A/one.flac"), await SongAsync("A/two.mp3"), await SongAsync("B/three.m4a") };
        var settings = new SettingsService(_db);
        var importer = new FakeImporter(Path.Combine(_root, "staging"), _options.LibraryPath);
        var task = new BarsTask(_db, settings, Maker(), importer, Options.Create(_options), NullLogger<BarsTask>.Instance);
        var progress = new Progress<string>();

        // Only while they're made when matching.
        await task.RunAsync(new BackgroundTask { Kind = BarsTask.TaskKind }, progress, default);
        Assert.All(songs, s => Assert.False(File.Exists(SpectrumFormat.SidecarOf(s))));

        await settings.SavePlaybackAsync(new PlaybackSettings { Visualiser = VisualiserBars.WhenMatching }, default);
        await task.RunAsync(new BackgroundTask { Kind = BarsTask.TaskKind }, progress, default);
        Assert.All(songs, s => Assert.True(SpectrumFormat.IsSpectrum(SpectrumFormat.SidecarOf(s))));
        var status = await BarsTask.StatusAsync(_db, settings, _options.LibraryPath, default);
        Assert.Equal((3, 3, VisualiserBars.WhenMatching), (status.Songs, status.BarsFiles, status.Settings.Visualiser));
        Assert.Equal(songs.Sum(s => new FileInfo(SpectrumFormat.SidecarOf(s)).Length), status.BarsBytes);
        Assert.Equal(9 * SpectrumFormat.BytesPerSecond, status.EstimatedBytes);

        await task.RunAsync(new BackgroundTask { Kind = BarsTask.TaskKind, PayloadJson = BarsTask.Delete }, progress, default);
        Assert.All(songs, s => Assert.False(File.Exists(SpectrumFormat.SidecarOf(s))));
        Assert.Equal(0, (await BarsTask.StatusAsync(_db, settings, _options.LibraryPath, default)).BarsFiles);
    }
}

/// <summary>A test that needs ffmpeg, to make songs and decode them.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        var path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        if (!path.Any(p => File.Exists(Path.Combine(p, "ffmpeg"))))
            Skip = "ffmpeg isn't installed.";
    }
}
