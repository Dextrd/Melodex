using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Melodex.Core.Library;

/// <summary>
/// How loud a song is, as ITU-R BS.1770 measures it, which ReplayGain 2 goes by (#23): its blocks' loudness, kept so an
/// album's can be worked out from all of its songs together, and its highest sample.
/// </summary>
/// <param name="Blocks">Each 400 ms block's mean square, K-weighted and summed over the channels, every 100 ms.</param>
/// <param name="Peak">The highest sample, 1 being full scale.</param>
public sealed record Loudness(IReadOnlyList<double> Blocks, double Peak)
{
    /// <summary>The loudness, in LUFS, of the given blocks, gated as BS.1770 says; null when they're all silence.</summary>
    public static double? Of(IEnumerable<double> blocks)
    {
        // Blocks under -70 LUFS are silence; of the rest, those 10 LU under their own loudness are quiet passages.
        var heard = blocks.Where(b => Lufs(b) > -70).ToList();
        if (heard.Count == 0)
            return null;
        var relative = Lufs(heard.Average()) - 10;
        var counted = heard.Where(b => Lufs(b) > relative).ToList();
        return counted.Count == 0 ? null : Lufs(counted.Average());
    }

    /// <summary>The song's own loudness, in LUFS.</summary>
    public double? Integrated => Of(Blocks);

    private static double Lufs(double meanSquare) => -0.691 + 10 * Math.Log10(meanSquare);
}

/// <summary>Measures songs' loudness, from their audio decoded by ffmpeg at 48 kHz.</summary>
public sealed class LoudnessMeter(IOptions<MelodexOptions> options)
{
    public const int Rate = 48000;

    /// <exception cref="LoudnessException">ffmpeg is missing, or couldn't decode it.</exception>
    public async Task<Loudness> MeasureAsync(string path, CancellationToken ct)
    {
        // Mono's heard once; anything with more channels than stereo is taken as stereo.
        var channels = Channels(path) == 1 ? 1 : 2;
        var start = new ProcessStartInfo(options.Value.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "-nostdin", "-v", "error", "-threads", "1", "-i", path, "-vn", "-sn", "-dn", "-ac", channels.ToString(), "-ar", Rate.ToString(), "-f", "f32le", "-" })
            start.ArgumentList.Add(arg);
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new LoudnessException("ffmpeg didn't start.");
        }
        catch (Win32Exception e)
        {
            throw new LoudnessException($"There's no ffmpeg at {options.Value.FfmpegPath}.", e);
        }
        using (process)
        {
            var errors = process.StandardError.ReadToEndAsync(ct);
            var meter = new LoudnessCounter(channels);
            try
            {
                var stream = process.StandardOutput.BaseStream;
                var buffer = new byte[64 * 1024];
                var samples = new float[buffer.Length / 4];
                var held = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(held), ct)) > 0)
                {
                    held += read;
                    // Whole frames, every channel's sample of each moment; a frame split between reads waits for the rest.
                    var whole = held / (4 * channels) * channels;
                    for (var i = 0; i < whole; i++)
                        samples[i] = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(i * 4));
                    meter.Add(samples.AsSpan(0, whole));
                    var left = held - whole * 4;
                    buffer.AsSpan(whole * 4, left).CopyTo(buffer);
                    held = left;
                }
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                throw;
            }
            if (process.ExitCode != 0)
                throw new LoudnessException($"ffmpeg couldn't decode it: {(await errors).Trim()}");
            return meter.Result();
        }
    }

    private static int Channels(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            return file.Properties?.AudioChannels ?? 2;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return 2;
        }
    }
}

/// <summary>Adds up decoded audio, 48 kHz, interleaved, as BS.1770 says: K-weighted, in 400 ms blocks every 100 ms.</summary>
public sealed class LoudnessCounter(int channels)
{
    private const int Step = LoudnessMeter.Rate / 10;

    // The K-weighting at 48 kHz: a shelf lifting the highs, then a high-pass, each a biquad, for every channel.
    private readonly Biquad[] _shelf = Enumerable.Range(0, channels)
        .Select(_ => new Biquad(1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, 0.73248077421585)).ToArray();
    private readonly Biquad[] _highPass = Enumerable.Range(0, channels)
        .Select(_ => new Biquad(1, -2, 1, -1.99004745483398, 0.99007225036621)).ToArray();

    // Each 100 ms's sum of squares, over the channels; a block is four of them.
    private readonly List<double> _steps = [];
    private double _step;
    private int _inStep;
    private double _peak;

    public void Add(ReadOnlySpan<float> interleaved)
    {
        for (var i = 0; i + channels <= interleaved.Length; i += channels)
        {
            for (var c = 0; c < channels; c++)
            {
                var sample = interleaved[i + c];
                _peak = Math.Max(_peak, Math.Abs(sample));
                var weighted = _highPass[c].Next(_shelf[c].Next(sample));
                _step += weighted * weighted;
            }
            if (++_inStep == Step)
            {
                _steps.Add(_step);
                _step = 0;
                _inStep = 0;
            }
        }
    }

    public Loudness Result()
    {
        var blocks = new List<double>();
        for (var i = 3; i < _steps.Count; i++)
            blocks.Add((_steps[i - 3] + _steps[i - 2] + _steps[i - 1] + _steps[i]) / (4.0 * Step));
        return new Loudness(blocks, _peak);
    }

    private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double _x1, _x2, _y1, _y2;

        public double Next(double x)
        {
            var y = b0 * x + b1 * _x1 + b2 * _x2 - a1 * _y1 - a2 * _y2;
            (_x2, _x1, _y2, _y1) = (_x1, x, _y1, y);
            return y;
        }
    }
}

/// <summary>A file's ReplayGain: gains in dB to -18 LUFS, peaks with 1 as full scale; null where it has none.</summary>
public sealed record FileGains(double? TrackGain, double? TrackPeak, double? AlbumGain, double? AlbumPeak)
{
    // An Opus file's R128 gains are to -23 LUFS, 5 dB under ReplayGain's.
    private const double R128Offset = 5;

    /// <summary>As the file's tags have it: ReplayGain's, or an Opus file's R128 ones.</summary>
    public static FileGains Of(TagLib.File file)
    {
        if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph && file.MimeType.Contains("opus", StringComparison.OrdinalIgnoreCase))
        {
            double? R128(string field) => int.TryParse(xiph.GetFirstField(field), out var q) ? q / 256.0 + R128Offset : null;
            return new FileGains(R128("R128_TRACK_GAIN"), null, R128("R128_ALBUM_GAIN"), null);
        }
        var tag = file.Tag;
        static double? Known(double value) => double.IsNaN(value) ? null : value;
        return new FileGains(Known(tag.ReplayGainTrackGain), Known(tag.ReplayGainTrackPeak), Known(tag.ReplayGainAlbumGain), Known(tag.ReplayGainAlbumPeak));
    }
}

public sealed class LoudnessException(string message, Exception? inner = null) : Exception(message, inner);
