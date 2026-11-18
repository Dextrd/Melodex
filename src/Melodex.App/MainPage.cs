using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Melodex.App.Components;

namespace Melodex.App;

/// <summary>
/// The whole app: the web app in a web view. What it asks of the API from the page itself, covers, songs, events and
/// files, goes through <see cref="ServerProxy"/> to the server chosen; links to the API's own pages open in the browser.
/// </summary>
public sealed class MainPage : ContentPage
{
    private readonly ServerProxy _proxy;
    private readonly Servers _servers;

    public MainPage(ServerProxy proxy, Servers servers)
    {
        _proxy = proxy;
        _servers = servers;
        Content = NewView();
        // Clear of the phone's status and navigation bars, in the app's colour so their light icons show on it.
        SafeAreaEdges = SafeAreaEdges.All;
        BackgroundColor = Color.FromArgb("#b8296a");
    }

    /// <summary>Starts the app afresh in a new web view, with new services: after a factory reset, or on another server.</summary>
    public void Restart()
    {
        var old = Content;
        var web = old?.Handler?.PlatformView;
        Content = NewView();
        old?.Handler?.DisconnectHandler();
        // Gone for good, not just out of sight, so its songs and events stop with it.
#if ANDROID
        (web as Android.Webkit.WebView)?.Destroy();
#elif IOS
        (web as WebKit.WKWebView)?.LoadHtmlString("", null);
#endif
    }

    private BlazorWebView NewView()
    {
        var view = new BlazorWebView { HostPage = "wwwroot/index.html" };
        view.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(Main) });
        view.WebResourceRequested += (_, e) => _proxy.Handle(e);
        // Songs play as in a browser, where the player can start the next one, or one asked for before its link came,
        // without a fresh tap.
#if ANDROID
        view.BlazorWebViewInitialized += (_, e) => e.WebView.Settings.MediaPlaybackRequiresUserGesture = false;
#elif IOS
        view.BlazorWebViewInitializing += (_, e) =>
        {
            e.Configuration.MediaTypesRequiringUserActionForPlayback = WebKit.WKAudiovisualMediaTypes.None;
            e.Configuration.AllowsInlineMediaPlayback = true;
        };
#endif
        view.UrlLoading += (_, e) =>
        {
            if (ServerProxy.IsApi(e.Url) && _servers.Current is { } server)
            {
                e.UrlLoadingStrategy = UrlLoadingStrategy.CancelLoad;
                _ = Launcher.OpenAsync(server.Resolve(e.Url));
            }
        };
        return view;
    }
}
