using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Melodex.Core.Tools;

/// <summary>
/// What a YouTube video is, by its ID: its title and artist, from YouTube's oEmbed answer (no account or key). A YouTube
/// Music song's channel is "Artist - Topic"; a music video's title is usually "Artist - Title (Official Video)".
/// </summary>
public sealed partial class VideoLookup(IHttpClientFactory http, ILogger<VideoLookup> logger)
{
    public const string HttpClientName = "video-lookup";

    // Each video once, while Melodex runs: the same songs turn up in several playlists.
    private readonly ConcurrentDictionary<string, (string Title, string? Artist)?> _known = new();

    /// <summary>The video's title and artist; null if it's gone, private or can't be looked up.</summary>
    public async Task<(string Title, string? Artist)?> LookupAsync(string videoId, CancellationToken ct)
    {
        if (_known.TryGetValue(videoId, out var known))
            return known;
        var url = $"https://www.youtube.com/oembed?format=json&url={Uri.EscapeDataString($"https://www.youtube.com/watch?v={videoId}")}";
        try
        {
            using var response = await http.CreateClient(HttpClientName).GetAsync(url, ct);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
                return _known[videoId] = null;
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            var title = json.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
            var author = json.RootElement.TryGetProperty("author_name", out var a) ? a.GetString() : null;
            return _known[videoId] = title is { Length: > 0 } ? Song(title, author) : null;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Not remembered, so it's tried again next time.
            logger.LogDebug(e, "Couldn't look up YouTube video {Id}", videoId);
            return null;
        }
    }

    /// <summary>The song a video's title and channel say it is.</summary>
    public static (string Title, string? Artist) Song(string title, string? channel)
    {
        title = Noise().Replace(title, "").Trim();
        if (channel is { } topic && topic.EndsWith(" - Topic", StringComparison.Ordinal))
            return (title, topic[..^" - Topic".Length].Trim());
        var (artist, song) = ListParser.SplitArtist(title);
        if (artist is not null)
            return (song, artist);
        return (title, channel is null ? null : ChannelNoise().Replace(channel, "").Trim() is { Length: > 0 } name ? name : null);
    }

    // What music videos' titles carry beside the song: "(Official Video)", "[Official Music Video]", "(Lyrics)" and so on.
    [GeneratedRegex(@"\s*[\(\[](?:official\s*)?(?:music\s*)?(?:video|audio|lyrics?|lyric\s*video|visuali[sz]er|hd|hq|4k)(?:\s*video)?[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    // What artists' channels carry beside their names.
    [GeneratedRegex(@"(?:VEVO|\s+Official|\s*-\s*Official|\s+Music)$", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelNoise();
}
