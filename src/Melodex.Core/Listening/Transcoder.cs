using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Melodex.Core.Listening;

/// <summary>A format songs can be turned into for a player that asks for one (#7).</summary>
/// <param name="Name">As players ask for it: mp3, opus or aac.</param>
public sealed record TranscodeFormat(string Name, string Codec, string Muxer, string ContentType, int MinKbps, int MaxKbps)
{
    public static readonly TranscodeFormat Mp3 = new("mp3", "libmp3lame", "mp3", "audio/mpeg", 32, 320);
    public static readonly TranscodeFormat Opus = new("opus", "libopus", "ogg", "audio/ogg", 16, 256);
    public static readonly TranscodeFormat Aac = new("aac", "aac", "adts", "audio/aac", 32, 320);

    public static readonly IReadOnlyList<TranscodeFormat> All = [Mp3, Opus, Aac];

    /// <summary>What a player gets when it asks for a lower bitrate without saying in what.</summary>
    public static TranscodeFormat Default => Mp3;

    /// <summary>The bitrate used when none is asked for.</summary>
    public const int DefaultKbps = 192;

    public static TranscodeFormat? Named(string? name) =>
        All.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? (string.Equals(name, "ogg", StringComparison.OrdinalIgnoreCase) ? Opus
            : string.Equals(name, "m4a", StringComparison.OrdinalIgnoreCase) ? Aac : null);
}

/// <summary>
/// Turns a song into another format, or a lower bitrate, with ffmpeg as it's sent (#7), for players on slow
/// connections or that can't play the file as it is. Nothing is kept: each play transcodes afresh, from where it starts.
/// </summary>
public sealed class Transcoder(IOptions<MelodexOptions> options, ILogger<Transcoder> logger)
{
    /// <summary>
    /// What a player asking for <paramref name="format"/> at most <paramref name="maxKbps"/> gets: null for the file as it is,
    /// which is what it gets if it asks for <c>raw</c>, for nothing, or for what the file is already, small enough.
    /// </summary>
    public static (TranscodeFormat Format, int Kbps)? Decide(string suffix, int? fileKbps, string? format, int? maxKbps)
    {
        if (string.Equals(format, "raw", StringComparison.OrdinalIgnoreCase))
            return null;
        var limit = maxKbps is > 0 ? maxKbps.Value : (int?)null;
        var asked = TranscodeFormat.Named(format);
        var same = asked is null || string.Equals(asked.Name, suffix, StringComparison.OrdinalIgnoreCase)
                   || (asked == TranscodeFormat.Opus && suffix is "opus" or "ogg") || (asked == TranscodeFormat.Aac && suffix is "m4a" && fileKbps < 400);
        if (same && (limit is null || fileKbps is { } kbps && kbps <= limit))
            return null;
        var to = asked ?? TranscodeFormat.Default;
        return (to, Math.Clamp(limit ?? TranscodeFormat.DefaultKbps, to.MinKbps, to.MaxKbps));
    }

    /// <summary>Sends the song transcoded, from <paramref name="offsetSeconds"/> in, until it ends or the player stops listening.</summary>
    public async Task SendAsync(HttpContext http, string path, TranscodeFormat format, int kbps, double offsetSeconds, CancellationToken ct)
    {
        var start = new ProcessStartInfo(options.Value.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var args = new List<string> { "-nostdin", "-v", "error" };
        if (offsetSeconds > 0)
            args.AddRange(["-ss", offsetSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", path, "-map", "0:a:0", "-vn", "-sn", "-dn", "-map_metadata", "-1", "-c:a", format.Codec, "-b:a", $"{kbps}k"]);
        // Opus is made at 48 kHz; MP3 can't go above 48, so the others are made at CD's 44.1.
        args.AddRange(["-ar", format == TranscodeFormat.Opus ? "48000" : "44100", "-f", format.Muxer, "-"]);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg didn't start.");
        }
        catch (Win32Exception e)
        {
            throw new InvalidOperationException($"There's no ffmpeg at {options.Value.FfmpegPath}.", e);
        }
        using (process)
        {
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            http.Response.ContentType = format.ContentType;
            http.Response.Headers.AcceptRanges = "none";
            try
            {
                await process.StandardOutput.BaseStream.CopyToAsync(http.Response.Body, 64 * 1024, ct);
                await process.WaitForExitAsync(ct);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException)
            {
                // The player stopped listening.
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                    // Finished already.
                }
                return;
            }
            if (process.ExitCode != 0)
                logger.LogWarning("Couldn't transcode {Path} to {Format}: {Reason}", path, format.Name, (await errors).Trim());
        }
    }
}
