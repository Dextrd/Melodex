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
/// otherwise made AAC.</item>
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
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _making = new();
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
        if (asked == Mp4 && (codec ?? MediaTypes.ReadQuality(path).Codec) is { } own)
        {
            var sound = Mp4Codec(own, keep);
            if (await GetMp4Async(path, sound, ct) is { } mp4)
                return Results.File(mp4, Mp4Type(sound), enableRangeProcessing: true);
        }
        return Results.File(path, MediaTypes.Bare(MediaTypes.For(path, codec)), enableRangeProcessing: true);
    }

    /// <summary>The path of the song's WebM copy, made now if there isn't one; null if it can't be made.</summary>
    public Task<string?> GetAsync(string path, CancellationToken ct) =>
        GetAsync(path, Webm, ".webm", ["-c", "copy", "-f", "webm"], ct);

    /// <summary>
    /// The path of the song's copy in fragmented MP4 with this sound (see <see cref="Mp4Codec"/>), made now if there isn't
    /// one; null if it can't be made. Its fragments are two seconds or so, each a piece a browser can take on its own.
    /// </summary>
    public Task<string?> GetMp4Async(string path, string sound, CancellationToken ct) =>
        GetAsync(path, Mp4, $".{sound}.mp4",
            [
                .. sound == "aac" && MediaTypes.ReadQuality(path).Codec != "aac" ? ["-c:a", "aac", "-b:a", AacBitrate] : new[] { "-c:a", "copy" },
                // FLAC and Opus in MP4 were once called experimental.
                "-strict", "-2", "-f", "mp4", "-movflags", "+empty_moov+default_base_moof", "-frag_duration", "2000000",
            ], ct);

    private async Task<string?> GetAsync(string path, string kind, string extension, string[] arguments, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            return null;
        var cached = Path.Combine(Folder(kind), Key(info) + extension);
        if (File.Exists(cached))
        {
            try
            {
                File.SetLastWriteTimeUtc(cached, DateTime.UtcNow);
                return cached;
            }
            catch (IOException)
            {
                // Made again below.
            }
        }
        return await _making.GetOrAdd(cached, _ => new Lazy<Task<string?>>(() => MakeAsync(path, cached, kind, arguments))).Value.WaitAsync(ct);
    }

    // Not cancelled by whoever asked first: another may be waiting for it too, and it's kept for next time.
    private async Task<string?> MakeAsync(string path, string cached, string kind, string[] arguments)
    {
        await _running.WaitAsync();
        try
        {
            Directory.CreateDirectory(Folder(kind));
            var part = cached + ".part";
            var start = new ProcessStartInfo(options.Value.FfmpegPath) { RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "-nostdin", "-v", "error", "-y", "-i", path, "-map", "0:a:0" }.Concat(arguments).Append(part))
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new Win32Exception("ffmpeg didn't start.");
            var errors = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                File.Delete(part);
                logger.LogWarning("Couldn't put {Path} in {Kind}: {Reason}", path, kind, errors.Trim());
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
