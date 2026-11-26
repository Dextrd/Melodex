using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Melodex.Core.Listening;

/// <summary>
/// Songs put in another file for browsers that play them better that way, made when first played and kept a while in
/// Melodex's data folder, the least lately played forgotten first. Two kinds:
/// <list type="bullet">
/// <item><c>as=webm</c>: Opus in an Ogg file, put in WebM for Safari (#93), which gets its time wrong: it stops counting
/// short of the end while the music plays on (WebKit bug 293310). The sound isn't touched, only what it's packed in.</item>
/// <item><c>as=mp4</c>: any song in fragmented MP4, the form an iPhone takes to play a queue as one stream (#109). Its sound
/// is kept where the browser says it takes it in MP4 (<c>keep</c>, such as <c>flac,opus</c>; AAC always is), and
/// otherwise made AAC. It's sent as it's made, the first time: making a song AAC can take a while (#257).</item>
/// </list>
/// </summary>
public sealed class StreamCopies(IOptions<MelodexOptions> options, ILogger<StreamCopies> logger)
{
    /// <summary>The most room each kind of copy takes: two hundred songs or so.</summary>
    public const long CacheLimit = 1024L * 1024 * 1024;

    /// <summary>What a stream URL asks with, <c>as=webm</c>, for a WebM copy if the song needs one.</summary>
    public const string Webm = "webm";

    /// <summary>What a stream URL asks with, <c>as=mp4</c>, for the song in fragmented MP4.</summary>
    public const string Mp4 = "mp4";

    /// <summary>The bitrate songs are made AAC at, when their own sound can't be kept: as good as AAC gets to hear.</summary>
    public const string AacBitrate = "256k";

    // The sound an MP4 copy can keep as it is, and how it's named in a media type.
    private static readonly Dictionary<string, string> Kept = new()
    {
        ["aac"] = "mp4a.40.2",
        ["flac"] = "flac",
        ["opus"] = "opus",
        ["alac"] = "alac",
        ["mp3"] = "mp4a.6B",
    };

    private readonly SemaphoreSlim _running = new(2);
    private readonly ConcurrentDictionary<string, Lazy<Making>> _making = new();
    private readonly SemaphoreSlim _trimming = new(1);

    private string Folder(string kind) => Path.Combine(options.Value.DataPath, kind);

    /// <summary>Whether it's a file Safari gets the time of wrong: Opus in Ogg.</summary>
    public static bool Needs(string path, string? codec = null) => MediaTypes.For(path, codec) == "audio/ogg; codecs=opus";

    /// <summary>
    /// The sound an MP4 copy of a song has: its own if it's one the browser keeps (AAC always), otherwise AAC.
    /// </summary>
    public static string Mp4Codec(string codec, string? keep)
    {
        var keeps = (keep ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Kept.ContainsKey(codec) && (codec == "aac" || keeps.Contains(codec, StringComparer.OrdinalIgnoreCase)) ? codec : "aac";
    }

    /// <summary>The media type of an MP4 copy with this sound, such as <c>audio/mp4; codecs="flac"</c>.</summary>
    public static string Mp4Type(string codec) => $"audio/mp4; codecs=\"{Kept[codec]}\"";

    /// <summary>
    /// The song as it's best sent: the copy asked for if there's one to make (with <c>keep</c>, the sound an MP4 copy
    /// keeps), otherwise itself.
    /// </summary>
    public async Task<IResult> ServeAsync(string path, string? codec, string? asked, string? keep, CancellationToken ct)
    {
        if (asked == Webm && Needs(path, codec) && await GetAsync(path, ct) is { } copy)
            return Results.File(copy, "audio/webm", enableRangeProcessing: true);
        if (asked == Mp4 && (codec ?? MediaTypes.ReadQuality(path).Codec) is { } own
            && await Mp4Async(path, Mp4Codec(own, keep), ct) is { } mp4)
            return mp4;
        return Results.File(path, MediaTypes.Bare(MediaTypes.For(path, codec)), enableRangeProcessing: true);
    }

    /// <summary>The path of the song's WebM copy, made now if there isn't one; null if it can't be made.</summary>
    public Task<string?> GetAsync(string path, CancellationToken ct) =>
        GetAsync(path, Webm, ".webm", ["-c", "copy", "-f", "webm"], false, ct);

    /// <summary>
    /// The path of the song's copy in fragmented MP4 with this sound (see <see cref="Mp4Codec"/>), made now if there isn't
    /// one; null if it can't be made. Its fragments are two seconds or so, each a piece a browser can take on its own.
    /// </summary>
    public Task<string?> GetMp4Async(string path, string sound, CancellationToken ct) =>
        GetAsync(path, Mp4, $".{sound}.mp4", Mp4Arguments(path, sound), true, ct);

    // Fragmented MP4 comes out of ffmpeg from the start, nothing written over later, so it can be sent as it's made: the
    // same bytes as a file it writes.
    private static string[] Mp4Arguments(string path, string sound) =>
    [
        .. sound == "aac" && MediaTypes.ReadQuality(path).Codec != "aac" ? ["-c:a", "aac", "-b:a", AacBitrate] : new[] { "-c:a", "copy" },
        // FLAC and Opus in MP4 were once called experimental.
        "-strict", "-2", "-f", "mp4", "-movflags", "+empty_moov+default_base_moof", "-frag_duration", "2000000",
    ];

    /// <summary>
    /// The song's MP4 copy with this sound: the file if it's made already, otherwise sent as ffmpeg makes it, so the song
    /// starts within a moment rather than once it's all made (#257); null if it can't be made.
    /// </summary>
    private async Task<IResult?> Mp4Async(string path, string sound, CancellationToken ct)
    {
        var type = Mp4Type(sound);
        var (cached, making) = Copy(path, Mp4, $".{sound}.mp4", Mp4Arguments(path, sound), true);
        if (cached is not null)
            return Results.File(cached, type, enableRangeProcessing: true);
        if (making is null)
            return null;
        await Task.WhenAny(making.Begun.Task, making.Done).WaitAsync(ct);
        if (!making.Done.IsCompleted)
        {
            try
            {
                return Results.Stream(new Growing(new FileStream(making.Part, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
                    making.Done), type);
            }
            catch (FileNotFoundException)
            {
                // Made just now, and moved to where it's kept.
            }
        }
        return await making.Done.WaitAsync(ct) is { } made ? Results.File(made, type, enableRangeProcessing: true) : null;
    }

    private async Task<string?> GetAsync(string path, string kind, string extension, string[] arguments, bool piped, CancellationToken ct)
    {
        var (cached, making) = Copy(path, kind, extension, arguments, piped);
        return cached ?? (making is null ? null : await making.Done.WaitAsync(ct));
    }

    // The copy, if it's made already, or its making, started now if it isn't under way; neither if there's no song.
    private (string? Cached, Making? Making) Copy(string path, string kind, string extension, string[] arguments, bool piped)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            return (null, null);
        var cached = Path.Combine(Folder(kind), Key(info) + extension);
        if (File.Exists(cached))
        {
            try
            {
                File.SetLastWriteTimeUtc(cached, DateTime.UtcNow);
                return (cached, null);
            }
            catch (IOException)
            {
                // Made again below.
            }
        }
        return (null, _making.GetOrAdd(cached, _ => new Lazy<Making>(() =>
        {
            var making = new Making(cached + ".part");
            making.Done = MakeAsync(path, cached, kind, arguments, piped, making);
            return making;
        })).Value);
    }

    // A copy being made: where it's written until it's done, and when its first bytes are there to be read.
    private sealed class Making(string part)
    {
        public string Part { get; } = part;
        public TaskCompletionSource Begun { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string?> Done { get; set; } = null!;
    }

    // Not cancelled by whoever asked first: another may be waiting for it too, and it's kept for next time. Piped, ffmpeg
    // writes to its output, and that's written to the file a piece at a time, for it to be read as it grows.
    private async Task<string?> MakeAsync(string path, string cached, string kind, string[] arguments, bool piped, Making making)
    {
        await _running.WaitAsync();
        try
        {
            Directory.CreateDirectory(Folder(kind));
            var part = making.Part;
            var start = new ProcessStartInfo(options.Value.FfmpegPath) { RedirectStandardError = true, RedirectStandardOutput = piped, UseShellExecute = false };
            foreach (var arg in new[] { "-nostdin", "-v", "error", "-y", "-i", path, "-map", "0:a:0" }.Concat(arguments).Append(piped ? "pipe:1" : part))
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new Win32Exception("ffmpeg didn't start.");
            var errors = process.StandardError.ReadToEndAsync();
            if (piped)
            {
                try
                {
                    // Unbuffered, so each piece can be read as soon as it's written.
                    await using var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1);
                    var buffer = new byte[64 * 1024];
                    int read;
                    while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read));
                        making.Begun.TrySetResult();
                    }
                }
                catch
                {
                    process.Kill();
                    throw;
                }
            }
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                File.Delete(part);
                logger.LogWarning("Couldn't put {Path} in {Kind}: {Reason}", path, kind, (await errors).Trim());
                return null;
            }
            File.Move(part, cached, true);
            _ = TrimAsync(kind);
            return cached;
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Couldn't put {Path} in {Kind}: {Reason}", path, kind, e.Message);
            return null;
        }
        finally
        {
            _running.Release();
            _making.TryRemove(cached, out _);
            making.Begun.TrySetResult();
        }
    }

    // A copy being made, read as it's written: when there's nothing more yet, it waits for more, until it's done. If it
    // couldn't be made after all, the response is broken off, and the player plays the song as it is instead.
    private sealed class Growing(FileStream file, Task<string?> done) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            for (;;)
            {
                // Whether it was done before this read: if so, nothing read means there's no more.
                var finished = done.IsCompleted;
                var read = await file.ReadAsync(buffer, ct);
                if (read > 0)
                    return read;
                if (finished)
                    return await done is null ? throw new IOException("The copy couldn't be made.") : 0;
                await Task.WhenAny(done, Task.Delay(50, ct));
                ct.ThrowIfCancellationRequested();
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                file.Dispose();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            file.Dispose();
            return base.DisposeAsync();
        }
    }

    /// <summary>Forgets the least lately played copies of a kind once they take more than the limit.</summary>
    private async Task TrimAsync(string kind)
    {
        if (!await _trimming.WaitAsync(0))
            return;
        try
        {
            await Task.Run(() =>
            {
                var files = new DirectoryInfo(Folder(kind)).GetFiles("*." + kind).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
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
            logger.LogWarning("Couldn't tidy the {Kind} copies: {Reason}", kind, e.Message);
        }
        finally
        {
            _trimming.Release();
        }
    }

    // A changed file is a new key, and its old copy is forgotten in time.
    private static string Key(FileInfo file) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}")))[..32];
}
