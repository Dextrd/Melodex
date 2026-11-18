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
    public MainPage(ServerProxy proxy, Servers servers)
    {
        var view = new BlazorWebView { HostPage = "wwwroot/index.html" };
        view.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(Main) });
        view.WebResourceRequested += (_, e) => proxy.Handle(e);
        view.UrlLoading += (_, e) =>
        {
            if (ServerProxy.IsApi(e.Url) && servers.Current is { } server)
            {
                e.UrlLoadingStrategy = UrlLoadingStrategy.CancelLoad;
                _ = Launcher.OpenAsync(server.Resolve(e.Url));
            }
        };
        Content = view;
    }
}
