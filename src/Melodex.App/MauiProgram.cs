using Melodex.Web;

namespace Melodex.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        builder.Services.AddSingleton<Servers>();
        builder.Services.AddSingleton<ServerProxy>();
        builder.Services.AddSingleton<Discovery>();
        builder.Services.AddMelodexWeb();
        builder.Services.AddScoped<AppHost, DeviceHost>();
        // The API, on the server chosen: a page load starts a new scope, so choosing another one reloads the page.
        builder.Services.AddScoped(services =>
        {
            var servers = services.GetRequiredService<Servers>();
            return new HttpClient(new SignInHandler(services.GetRequiredService<AuthState>(), servers.Handler()))
            {
                BaseAddress = servers.Current?.Address ?? new Uri("http://localhost/"),
            };
        });

        return builder.Build();
    }
}
