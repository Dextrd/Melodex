using Melodex.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Melodex.Web;

public static class WebServices
{
    /// <summary>
    /// The app's own services, the same in every host. The host adds the <see cref="HttpClient"/> that reaches the API,
    /// with <see cref="SignInHandler"/> in its chain, and its own <see cref="AppHost"/> if it isn't a browser.
    /// </summary>
    public static IServiceCollection AddMelodexWeb(this IServiceCollection services)
    {
        services.AddScoped<AuthState>();
        services.AddScoped<AppHost>();
        services.AddScoped<Theme>();
        services.AddScoped<MelodexClient>();
        services.AddScoped<Preferences>();
        services.AddScoped<ServerEvents>();
        services.AddScoped<RequestTracker>();
        services.AddScoped<WishlistStore>();
        services.AddScoped<Player>();
        services.AddScoped<LibraryStore>();
        services.AddScoped<SearchMemory>();
        services.AddScoped<PageCache>();
        services.AddScoped<ToolsMemory>();
        services.AddScoped<Glimpses>();
        services.AddScoped<NavSections>();
        services.AddScoped<ViewTransitions>();
        return services;
    }
}
