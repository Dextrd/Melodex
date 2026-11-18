using Microsoft.AspNetCore.Components;
using Melodex.Client;
using Melodex.Web;

namespace Melodex.App;

/// <summary>
/// How the web app works in the Melodex app: signing in makes a key for the device, kept for the server in use, and
/// files are saved through the device's share sheet.
/// </summary>
public sealed class DeviceHost(MelodexClient api, Servers servers, NavigationManager nav, HttpClient http, MainPage page) : AppHost(api, nav)
{
    private readonly MelodexClient _api = api;

    public override bool OnDevice => true;

    public override string? Server => servers.Current?.Name;

    public override async Task SetUpAsync(SetUpLogin login)
    {
        await _api.SetUpLoginAsync(login);
        await SignInAsync(new SignIn(login.Username, login.Password));
    }

    public override async Task SignInAsync(SignIn signIn)
    {
        var key = await _api.SignInDeviceAsync(new DeviceSignIn(signIn.Username, signIn.Password, DeviceName()));
        await servers.SetKeyAsync(key.Key, signIn.Username.Trim());
    }

    public override async Task SignOutAsync()
    {
        try
        {
            await _api.SignOutAsync();
        }
        catch (Exception e) when (e is HttpRequestException or MelodexApiException)
        {
            // Signed out here all the same; the key can be signed out from the account page.
        }
        await servers.SetKeyAsync(null);
    }

    // A new web view rather than a new page, which the web view wouldn't start the app on again.
    public override void Restart() => MainThread.BeginInvokeOnMainThread(page.Restart);

    public override void ChangeServer()
    {
        servers.Leave();
        Restart();
    }

    public override async Task SaveAsync(string url)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var disposition = response.Content.Headers.ContentDisposition;
        var name = Path.GetFileName((disposition?.FileNameStar ?? disposition?.FileName ?? "Melodex export").Trim('"'));
        var path = Path.Combine(FileSystem.CacheDirectory, name);
        await using (var file = File.Create(path))
            await response.Content.CopyToAsync(file);
        await Share.Default.RequestAsync(new ShareFileRequest(name, new ShareFile(path)));
    }

    /// <summary>What the device is called, to tell it apart on the account page.</summary>
    private static string DeviceName() =>
        Microsoft.Maui.Devices.DeviceInfo.Current.Name is { Length: > 0 } name ? name : $"{Microsoft.Maui.Devices.DeviceInfo.Current.Manufacturer} {Microsoft.Maui.Devices.DeviceInfo.Current.Model}".Trim();
}
