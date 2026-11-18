using Microsoft.Maui.Controls;

namespace Melodex.App;

/// <summary>
/// Sends what the page asks of the API itself, rather than through <see cref="HttpClient"/>, to the server in use with
/// the device's key: covers, songs and their bars, events and exports. The page is on the app's own origin, where the
/// browser would have had them from the server's.
/// </summary>
public sealed class ServerProxy(Servers servers)
{
    // What the page asks with that matters to the answer. Not If-None-Match and the like: a web view won't take a 304.
    private static readonly string[] Forwarded = ["Accept", "Range"];

    // What the answer carries that the page needs, for playing from anywhere in a song, caching and saving.
    private static readonly string[] Returned =
        ["Content-Type", "Content-Length", "Content-Range", "Accept-Ranges", "Cache-Control", "ETag", "Last-Modified", "Content-Disposition"];

    private readonly HttpClient _http = new(new HttpClientHandler { UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>A link on the app's own origin to the API, rather than to one of the app's pages.</summary>
    public static bool IsApi(Uri url) => url.Host == "0.0.0.1" && url.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal);

    public void Handle(WebViewWebResourceRequestedEventArgs e)
    {
        if (!IsApi(e.Uri) || servers.Current is not { } server)
            return;
        e.Handled = true;

        var request = new HttpRequestMessage(new HttpMethod(e.Method), server.Resolve(e.Uri));
        foreach (var (name, value) in e.Headers)
            if (Forwarded.Contains(name, StringComparer.OrdinalIgnoreCase))
                request.Headers.TryAddWithoutValidation(name, value);
        if (servers.Key is { } key)
            request.Headers.TryAddWithoutValidation(Servers.KeyHeader, key);

        // Android asks on a thread of its own, so it can wait for the answer and pass on its status and headers. Asked
        // on the main thread, it mustn't wait: the body follows when it comes, and the status is taken to be fine.
        if (MainThread.IsMainThread)
        {
            e.SetResponse(200, "OK", new Dictionary<string, string>(), BodyAsync(request));
            return;
        }
        try
        {
            var response = _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
                if (Returned.Contains(name, StringComparer.OrdinalIgnoreCase))
                    headers[name] = string.Join(", ", values);
            var reason = string.IsNullOrEmpty(response.ReasonPhrase) ? "OK" : response.ReasonPhrase;
            e.SetResponse((int)response.StatusCode, reason, headers, response.Content.ReadAsStreamAsync().GetAwaiter().GetResult());
        }
        catch (HttpRequestException)
        {
            e.SetResponse(502, "Bad Gateway");
        }
    }

    private async Task<Stream?> BodyAsync(HttpRequestMessage request)
    {
        try
        {
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            return await response.Content.ReadAsStreamAsync();
        }
        catch (HttpRequestException)
        {
            return Stream.Null;
        }
    }
}
