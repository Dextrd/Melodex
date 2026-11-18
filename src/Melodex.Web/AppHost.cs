using Melodex.Client;
using Microsoft.AspNetCore.Components;

namespace Melodex.Web;

/// <summary>
/// What the app does its own way, depending on where it runs. This is how it works in a browser, on the server's own
/// page; the Melodex app for phones (Melodex.App) has its own, which signs in with a key for the device.
/// </summary>
public class AppHost(MelodexClient api, NavigationManager nav)
{
    /// <summary>The Melodex app on a device, rather than a browser.</summary>
    public virtual bool OnDevice => false;

    /// <summary>The server the app is connected to, for the account menu. Null in a browser, which is on it.</summary>
    public virtual string? Server => null;

    public virtual Task SetUpAsync(SetUpLogin login) => api.SetUpLoginAsync(login);

    public virtual Task SignInAsync(SignIn signIn) => api.SignInAsync(signIn);

    public virtual Task SignOutAsync() => api.SignOutAsync();

    /// <summary>Starts the app afresh, as on a new page: after a factory reset, or on another server.</summary>
    public virtual void Restart() => nav.NavigateTo("", forceLoad: true);

    /// <summary>Back to the list of servers, to connect to another. Only on a device.</summary>
    public virtual void ChangeServer()
    {
    }

    /// <summary>
    /// Saves what an API link leads to, such as an export, where the user chooses. Only on a device: in a browser, the
    /// link downloads it.
    /// </summary>
    public virtual Task SaveAsync(string url) => Task.CompletedTask;
}
