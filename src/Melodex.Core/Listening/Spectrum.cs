using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Melodex.Core.Listening;

/// <summary>
/// A song's spectrum (#102), which the visualiser's bars are drawn from (#89) and any app can use: how loud each of
/// <see cref="Bands"/> bands of pitch is, <see cref="Fps"/> times a second, unsmoothed, with each moment's loudness,
/// peak, waveform and beats, worked out once from the song so the player needn't decode it a second time.
/// </summary>
/// <remarks>
/// A spectrum file is gzipped: a <see cref="HeaderSize"/>-byte header, then each frame. The header is <c>MDXB</c>, the
/// version, the frames a second, the bands a frame, the header's length, the lowest and highest pitch in hertz
/// (16-bit, little-endian), the quietest and loudest decibels the levels span (signed bytes, full scale being 0) and a
/// frame's length (16-bit). A frame is each band's level, bass first, then the loudness and the peak, on the same
/// scale; the waveform's lowest and highest sample (signed bytes, -127 to 127 being full scale); and the beat, 0 for
/// none, else how strong. Readers skip any header or frame bytes past those they know, which later versions may add.
/// Version 1, still served at <c>/bars</c>, had smoothed levels only.
/// </remarks>
public static class SpectrumFormat
{
    public const int Rate = 44100, Size = 4096, Fps = 30, Bands = 64, HeaderSize = 16;
    public const int LowestHz = 40, HighestHz = 16000;
    public const int FloorDb = -96, CeilingDb = 0;
    /// <summary>What follows the bands in a frame: loudness, peak, the waveform's lowest and highest, and the beat.</summary>
    public const int Extras = 5;
    public const int FrameSize = Bands + Extras;
    public const byte Version = 2;
    public const string Extension = ".bars";
    public static ReadOnlySpan<byte> Magic => "MDXB"u8;

    /// <summary>The band counts a spectrum can be asked for in: each a whole fraction of <see cref="Bands"/>.</summary>
    public static readonly int[] BandChoices = [8, 16, 32, 64];

    /// <summary>About how many bytes a spectrum takes for each second of a song, gzipped.</summary>
    public const int BytesPerSecond = 1900;

    /// <summary>Where a song's spectrum goes: beside it, with the same name.</summary>
    public static string SidecarOf(string path) => Path.ChangeExtension(path, Extension);

    /// <summary>A level, 0 to 255, for so many decibels.</summary>
    public static byte Level(double db) => (byte)Math.Clamp(Math.Round(255 * (db - FloorDb) / (CeilingDb - FloorDb)), 0, 255);

    /// <summary>The decibels a level stands for.</summary>
    public static double Decibels(byte level) => FloorDb + level * (CeilingDb - FloorDb) / 255.0;

    /// <summary>A spectrum file of the frames, gzipped, with <paramref name="bands"/> bands a frame.</summary>
    public static byte[] Write(byte[] frames, int bands = Bands)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            gzip.Write(Header(bands));
            gzip.Write(frames);
        }
        return output.ToArray();
    }

    private static byte[] Header(int bands)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header);
        header[4] = Version;
        header[5] = Fps;
        header[6] = (byte)bands;
        header[7] = HeaderSize;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), LowestHz);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), HighestHz);
        header[12] = unchecked((byte)(sbyte)FloorDb);
        header[13] = unchecked((byte)(sbyte)CeilingDb);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), (ushort)(bands + Extras));
        return header;
    }

    /// <summary>Whether the file is a spectrum this version of Melodex makes; false if it's anything else, an older one, or can't be read.</summary>
    public static bool IsSpectrum(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            return IsSpectrum(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsSpectrum(Stream stream)
    {
        try
        {
            using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
            Span<byte> header = stackalloc byte[HeaderSize];
            gzip.ReadExactly(header);
            return header.SequenceEqual(Header(Bands));
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IOException)
        {
            return false;
        }
    }

    /// <summary>The frames of a spectrum file this version makes.</summary>
    public static byte[] Frames(byte[] file)
    {
        using var gzip = new GZipStream(new MemoryStream(file), CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray()[HeaderSize..];
    }

    /// <summary>
    /// The spectrum with fewer bands, each the loudest of those it covers, the rest of each frame as it was.
    /// </summary>
    public static byte[] WithBands(byte[] file, int bands)
    {
        if (bands == Bands)
            return file;
        if (!BandChoices.Contains(bands))
            throw new ArgumentOutOfRangeException(nameof(bands));
        var frames = Frames(file);
        var each = Bands / bands;
        var count = frames.Length / FrameSize;
        var fewer = new byte[count * (bands + Extras)];
        for (var f = 0; f < count; f++)
        {
            var from = frames.AsSpan(f * FrameSize, FrameSize);
            var to = fewer.AsSpan(f * (bands + Extras), bands + Extras);
            for (var b = 0; b < bands; b++)
            {
                byte peak = 0;
                foreach (var level in from.Slice(b * each, each))
                    peak = Math.Max(peak, level);
                to[b] = peak;
            }
            from[Bands..].CopyTo(to[bands..]);
        }
        return Write(fewer, bands);
    }

    /// <summary>
    /// The spectrum as version 1 had it, for apps that read <c>/bars</c>: 64 bars from 40 Hz to 10 kHz, smoothed over
    /// time as a Web Audio analyser does, each a byte from -100 to -30 decibels of the analyser's own scale.
    /// </summary>
    public static byte[] AsVersion1(byte[] file)
    {
        const int count = 64, low = 40, high = 10000;
        const double smoothing = 0.64, minDb = -100, maxDb = -30;
        // The analyser measured a full-scale sine at 20·log10(0.42/2) decibels, where a spectrum's is at 0.
        var offset = 20 * Math.Log10(0.21);
        var frames = Frames(file);
        var frameCount = frames.Length / FrameSize;
        // Each old bar takes the band its middle pitch falls in.
        var from = new int[count];
        for (var i = 0; i < count; i++)
        {
            var hz = low * Math.Pow((double)high / low, (i + 0.5) / count);
            from[i] = Math.Clamp((int)(Bands * Math.Log(hz / LowestHz) / Math.Log((double)HighestHz / LowestHz)), 0, Bands - 1);
        }
        var smoothed = new double[count];
        var old = new byte[frameCount * count];
        for (var f = 0; f < frameCount; f++)
            for (var i = 0; i < count; i++)
            {
                var level = frames[f * FrameSize + from[i]];
                var amplitude = level == 0 ? 0 : Math.Pow(10, Decibels(level) / 20);
                smoothed[i] = smoothing * smoothed[i] + (1 - smoothing) * amplitude;
                var db = 20 * Math.Log10(Math.Max(smoothed[i], 1e-12)) + offset;
                old[f * count + i] = (byte)Math.Clamp(255 * (db - minDb) / (maxDb - minDb), 0, 255);
            }
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            gzip.Write("MDXB"u8);
            gzip.Write([1, Fps, count, 0]);
            gzip.Write(old);
        }
        return output.ToArray();
    }
}

/// <summary>
/// Works out a spectrum from a song's samples, one channel at <see cref="SpectrumFormat.Rate"/>, fed in as they're
/// decoded. Each frame is the spectrum of the <see cref="SpectrumFormat.Size"/> samples up to its moment, in decibels of
/// full scale, each band the loudest of the frequencies it covers, on a logarithmic scale, as we hear pitch; then the
/// loudness (root mean square) and peak, and the lowest and highest sample, of the samples since the last frame. Beats
/// are found at the end, where the bass and the rest rise more suddenly than they have around them.
/// </summary>
public sealed class SpectrumAnalyser
{
    private const int Hop = SpectrumFormat.Rate / SpectrumFormat.Fps, Size = SpectrumFormat.Size, Half = Size / 2;

    // Beats are looked for over a second either side, and no closer together than about 220 a minute.
    private const int Around = SpectrumFormat.Fps, Apart = 8;

    private static readonly double[] Window = new double[Size];
    private static readonly double[] Cos = new double[Half], Sin = new double[Half];
    private static readonly int[] Reversed = new int[Size];
    private static readonly (int From, int To)[] BandBins = new (int, int)[SpectrumFormat.Bands];
    // A full-scale sine's height, so that it measures 0 decibels.
    private static readonly double FullScale;

    static SpectrumAnalyser()
    {
        double sum = 0;
        for (var i = 0; i < Size; i++)
            sum += Window[i] = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / Size) + 0.08 * Math.Cos(4 * Math.PI * i / Size);
        FullScale = sum / 2;
        for (var k = 0; k < Half; k++)
        {
            Cos[k] = Math.Cos(-2 * Math.PI * k / Size);
            Sin[k] = Math.Sin(-2 * Math.PI * k / Size);
        }
        var bits = (int)Math.Log2(Size);
        for (var i = 0; i < Size; i++)
        {
            int r = 0, x = i;
            for (var b = 0; b < bits; b++, x >>= 1)
                r = (r << 1) | (x & 1);
            Reversed[i] = r;
        }
        const double hz = (double)SpectrumFormat.Rate / Size;
        const double low = SpectrumFormat.LowestHz / hz, high = SpectrumFormat.HighestHz / hz;
        for (var i = 0; i < SpectrumFormat.Bands; i++)
        {
            var from = (int)Math.Round(low * Math.Pow(high / low, (double)i / SpectrumFormat.Bands));
            var to = Math.Max(from + 1, (int)Math.Round(low * Math.Pow(high / low, (double)(i + 1) / SpectrumFormat.Bands)));
            BandBins[i] = (from, to);
        }
    }

    // The samples so far, from the start of the song: frames look back a window's length from their moment.
    private readonly List<float> _samples = [];
    private readonly MemoryStream _frames = new();
    // How sudden each frame's rise is, for finding beats at the end.
    private readonly List<double> _rises = [];
    private readonly double[] _re = new double[Size], _im = new double[Size];
    private readonly byte[] _last = new byte[SpectrumFormat.Bands];
    // How many samples have been forgotten from the start, and the next frame to work out.
    private int _offset, _next;

    /// <summary>Takes the next samples, and works out every frame they complete.</summary>
    public void Add(ReadOnlySpan<float> samples)
    {
        foreach (var s in samples)
            _samples.Add(s);
        while (_next * Hop <= _offset + _samples.Count)
            Frame(_next++ * Hop);
        // Forget what no frame looks back to.
        var drop = _next * Hop - Size - _offset;
        if (drop > 1 << 16)
        {
            _samples.RemoveRange(0, drop);
            _offset += drop;
        }
    }

    /// <summary>Every frame, the last one at the song's end, with the beats marked.</summary>
    public byte[] Finish()
    {
        if ((_next - 1) * Hop < _offset + _samples.Count)
            Frame(_next++ * Hop);
        var frames = _frames.ToArray();
        MarkBeats(frames);
        return frames;
    }

    private void Frame(int end)
    {
        var start = end - Size - _offset;
        for (var i = 0; i < Size; i++)
        {
            var j = start + i;
            var r = Reversed[i];
            _re[r] = j >= 0 && j < _samples.Count ? _samples[j] * Window[i] : 0;
            _im[r] = 0;
        }
        for (var length = 2; length <= Size; length <<= 1)
        {
            var half = length / 2;
            var step = Size / length;
            for (var i = 0; i < Size; i += length)
                for (var k = 0; k < half; k++)
                {
                    double wr = Cos[k * step], wi = Sin[k * step];
                    int a = i + k, b = a + half;
                    double tr = _re[b] * wr - _im[b] * wi, ti = _re[b] * wi + _im[b] * wr;
                    _re[b] = _re[a] - tr;
                    _im[b] = _im[a] - ti;
                    _re[a] += tr;
                    _im[a] += ti;
                }
        }
        // Rises in the bass count double: it's where most beats are.
        double rise = 0;
        for (var band = 0; band < SpectrumFormat.Bands; band++)
        {
            var (from, to) = BandBins[band];
            var peak = 0.0;
            for (var k = from; k < to; k++)
                peak = Math.Max(peak, _re[k] * _re[k] + _im[k] * _im[k]);
            var level = SpectrumFormat.Level(10 * Math.Log10(Math.Max(peak, 1e-24)) - 20 * Math.Log10(FullScale));
            _frames.WriteByte(level);
            rise += Math.Max(0, level - _last[band]) * (band < SpectrumFormat.Bands / 4 ? 2 : 1);
            _last[band] = level;
        }
        _rises.Add(rise);

        // The samples since the last frame.
        double squares = 0, highest = 0;
        float min = 0, max = 0;
        var since = 0;
        for (var j = Math.Max(0, end - Hop - _offset); j < Math.Min(end - _offset, _samples.Count); j++, since++)
        {
            var s = _samples[j];
            squares += s * s;
            highest = Math.Max(highest, Math.Abs(s));
            min = Math.Min(min, s);
            max = Math.Max(max, s);
        }
        _frames.WriteByte(since == 0 ? (byte)0 : SpectrumFormat.Level(10 * Math.Log10(Math.Max(squares / since, 1e-24))));
        _frames.WriteByte(SpectrumFormat.Level(20 * Math.Log10(Math.Max(highest, 1e-12))));
        _frames.WriteByte(unchecked((byte)(sbyte)Math.Clamp(Math.Round(min * 127), -127, 127)));
        _frames.WriteByte(unchecked((byte)(sbyte)Math.Clamp(Math.Round(max * 127), -127, 127)));
        // The beat, marked at the end.
        _frames.WriteByte(0);
    }

    /// <summary>
    /// Marks frames that rise much more than those around them, and more than any nearby, as beats; how strong, by how
    /// far above the usual rise there they are.
    /// </summary>
    private void MarkBeats(byte[] frames)
    {
        var count = _rises.Count;
        // A running sum, for each frame's usual rise around it.
        var sums = new double[count + 1];
        for (var i = 0; i < count; i++)
            sums[i + 1] = sums[i] + _rises[i];
        var lastBeat = -Apart;
        for (var i = 1; i < count; i++)
        {
            int from = Math.Max(0, i - Around), to = Math.Min(count, i + Around + 1);
            var usual = (sums[to] - sums[from]) / (to - from);
            var threshold = usual * 1.5 + 20;
            if (_rises[i] < threshold || i - lastBeat < Apart)
                continue;
            var biggest = true;
            for (var j = Math.Max(0, i - 3); j <= Math.Min(count - 1, i + 3) && biggest; j++)
                biggest = j == i || _rises[j] < _rises[i] || (_rises[j] == _rises[i] && j > i);
            if (!biggest)
                continue;
            frames[i * SpectrumFormat.FrameSize + SpectrumFormat.FrameSize - 1] = (byte)Math.Clamp(Math.Round(255 * (1 - threshold / _rises[i]) * 2), 1, 255);
            lastBeat = i;
        }
    }
}

/// <summary>A spectrum couldn't be made: ffmpeg is missing, or the song can't be decoded.</summary>
public sealed class SpectrumException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Makes songs' spectrums with ffmpeg, a couple at a time, and keeps those made on demand in the data folder, the least
/// lately played forgotten first once they take more than <see cref="CacheLimit"/>.
/// </summary>
public sealed class SpectrumMaker(IOptions<MelodexOptions> options, ILogger<SpectrumMaker> logger)
{
    /// <summary>The most room spectrums made on demand take: a thousand songs or so.</summary>
    public const long CacheLimit = 512L * 1024 * 1024;

    private readonly SemaphoreSlim _running = new(2);
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> _making = new();
    private readonly SemaphoreSlim _trimming = new(1);

    private string CacheFolder => Path.Combine(options.Value.DataPath, "bars");

    /// <summary>
    /// The song's spectrum, as a spectrum file: from <paramref name="sidecar"/> when that's one of this version, else
    /// from the cache, else made now and cached.
    /// </summary>
    /// <exception cref="SpectrumException">It couldn't be made.</exception>
    public async Task<byte[]> GetAsync(string path, string? sidecar, CancellationToken ct)
    {
        if (sidecar is not null && File.Exists(sidecar) && SpectrumFormat.IsSpectrum(sidecar))
        {
            try
            {
                return await File.ReadAllBytesAsync(sidecar, ct);
            }
            catch (IOException)
            {
                // Being written, perhaps: made afresh instead.
            }
        }
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new SpectrumException("The song isn't there.");
        var cached = Path.Combine(CacheFolder, Key(info) + SpectrumFormat.Extension);
        if (File.Exists(cached))
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(cached, ct);
                File.SetLastWriteTimeUtc(cached, DateTime.UtcNow);
                return bytes;
            }
            catch (IOException)
            {
                // Made again below.
            }
        }
        return await _making.GetOrAdd(cached, _ => new Lazy<Task<byte[]>>(() => MakeAndCacheAsync(path, cached))).Value.WaitAsync(ct);
    }

    // Not cancelled by whoever asked first: another may be waiting for it too, and it's kept for next time.
    private async Task<byte[]> MakeAndCacheAsync(string path, string cached)
    {
        try
        {
            var spectrum = await MakeAsync(path, CancellationToken.None);
            try
            {
                Directory.CreateDirectory(CacheFolder);
                var part = cached + ".part";
                await File.WriteAllBytesAsync(part, spectrum);
                File.Move(part, cached, true);
                _ = TrimAsync();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Couldn't keep the spectrum for {Path}: {Reason}", path, e.Message);
            }
            return spectrum;
        }
        finally
        {
            _making.TryRemove(cached, out _);
        }
    }

    /// <summary>Makes the song's spectrum, as a spectrum file.</summary>
    /// <exception cref="SpectrumException">ffmpeg is missing, or couldn't decode it.</exception>
    public async Task<byte[]> MakeAsync(string path, CancellationToken ct)
    {
        await _running.WaitAsync(ct);
        try
        {
            var watch = Stopwatch.StartNew();
            var start = new ProcessStartInfo(options.Value.FfmpegPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in new[] { "-nostdin", "-v", "error", "-threads", "1", "-i", path, "-vn", "-sn", "-dn", "-ac", "1", "-ar", SpectrumFormat.Rate.ToString(), "-f", "f32le", "-" })
                start.ArgumentList.Add(arg);
            Process process;
            try
            {
                process = Process.Start(start) ?? throw new SpectrumException("ffmpeg didn't start.");
            }
            catch (Win32Exception e)
            {
                throw new SpectrumException($"There's no ffmpeg at {options.Value.FfmpegPath}.", e);
            }
            using (process)
            {
                var errors = process.StandardError.ReadToEndAsync(ct);
                var analyser = new SpectrumAnalyser();
                try
                {
                    var stream = process.StandardOutput.BaseStream;
                    var buffer = new byte[64 * 1024];
                    var held = 0;
                    var samples = new float[buffer.Length / 4];
                    int read;
                    while ((read = await stream.ReadAsync(buffer.AsMemory(held), ct)) > 0)
                    {
                        held += read;
                        var whole = held / 4;
                        for (var i = 0; i < whole; i++)
                            samples[i] = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(i * 4));
                        analyser.Add(samples.AsSpan(0, whole));
                        // A sample split between reads waits for the rest.
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
                    throw new SpectrumException($"ffmpeg couldn't decode it: {(await errors).Trim()}");
                var frames = analyser.Finish();
                logger.LogDebug("Made the spectrum for {Path} in {Elapsed} ms", path, watch.ElapsedMilliseconds);
                return SpectrumFormat.Write(frames);
            }
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>Forgets the least lately played spectrums once they take more than the limit.</summary>
    private async Task TrimAsync()
    {
        if (!await _trimming.WaitAsync(0))
            return;
        try
        {
            await Task.Run(() =>
            {
                var files = new DirectoryInfo(CacheFolder).GetFiles("*" + SpectrumFormat.Extension).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
                long total = 0;
                foreach (var file in files)
                {
                    total += file.Length;
                    if (total > CacheLimit)
                        file.Delete();
                }
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Couldn't tidy the kept spectrums: {Reason}", e.Message);
        }
        finally
        {
            _trimming.Release();
        }
    }

    // A changed file, or a new version, is a new key, and the old one is forgotten in time.
    private static string Key(FileInfo file) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|v{SpectrumFormat.Version}")))[..32];
}
