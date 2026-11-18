using System.Text.Json;
using Melodex.Client;

namespace Melodex.App;

/// <summary>A Melodex server the app knows: its address, a name for it, and who last signed in there.</summary>
public sealed record SavedServer(Guid Id, string Name, Uri Address, string? Username = null)
{
    /// <summary>A link from the page, on the app's own origin, at the same place on this server.</summary>
    public Uri Resolve(Uri link) => new(Address, link.PathAndQuery.TrimStart('/'));
}

/// <summary>
/// The servers the app knows and the one in use, kept in the app's preferences, and the key for the device on each,
/// kept in the device's secure storage. Each server keeps its own key, so moving between them needs no signing in.
/// </summary>
public sealed class Servers
{
    private const string ListKey = "servers";
    private const string CurrentKey = "server";

    private List<SavedServer> _all = [];
    private readonly Dictionary<Guid, string?> _keys = [];
    private bool _loaded;

    public IReadOnlyList<SavedServer> All => _all;

    public SavedServer? Current { get; private set; }

    /// <summary>The device's key on the server in use, once signed in there.</summary>
    public string? Key => Current is { } server ? _keys.GetValueOrDefault(server.Id) : null;

    public async Task LoadAsync()
    {
        if (_loaded)
            return;
        try
        {
            _all = JsonSerializer.Deserialize<List<SavedServer>>(Preferences.Default.Get(ListKey, "[]"), MelodexJson.Options) ?? [];
        }
        catch (JsonException)
        {
            _all = [];
        }
        foreach (var server in _all)
            _keys[server.Id] = await ReadKeyAsync(server.Id);
        Current = Guid.TryParse(Preferences.Default.Get(CurrentKey, ""), out var id) ? _all.FirstOrDefault(s => s.Id == id) : null;
        _loaded = true;
    }

    /// <summary>Uses a server from now on, adding it if it's new.</summary>
    public void Use(SavedServer server)
    {
        if (_all.FindIndex(s => s.Id == server.Id) is var at and >= 0)
            _all[at] = server;
        else
            _all.Add(server);
        Current = server;
        Save();
    }

    /// <summary>Back to choosing a server; this one is still known, and still signed in.</summary>
    public void Leave()
    {
        Current = null;
        Save();
    }

    /// <summary>Forgets a server, and the device's key there.</summary>
    public void Remove(SavedServer server)
    {
        _all.RemoveAll(s => s.Id == server.Id);
        _keys.Remove(server.Id);
        SecureStorage.Default.Remove(KeyName(server.Id));
        if (Current?.Id == server.Id)
            Current = null;
        Save();
    }

    public bool SignedIn(SavedServer server) => _keys.GetValueOrDefault(server.Id) is not null;

    /// <summary>Signed in on the server in use, as this user with this key; or signed out, with null.</summary>
    public async Task SetKeyAsync(string? key, string? username = null)
    {
        if (Current is not { } server)
            return;
        _keys[server.Id] = key;
        if (key is null)
            SecureStorage.Default.Remove(KeyName(server.Id));
        else
        {
            await SecureStorage.Default.SetAsync(KeyName(server.Id), key);
            Use(server with { Username = username ?? server.Username });
        }
    }

    /// <summary>Sends the device's key on the server in use with each call. Cookies aren't kept: the key is enough.</summary>
    public HttpMessageHandler Handler() => new KeyHandler(this);

    private void Save()
    {
        Preferences.Default.Set(ListKey, JsonSerializer.Serialize(_all, MelodexJson.Options));
        Preferences.Default.Set(CurrentKey, Current?.Id.ToString() ?? "");
    }

    private static string KeyName(Guid id) => $"key.{id:N}";

    private static async Task<string?> ReadKeyAsync(Guid id)
    {
        try
        {
            return await SecureStorage.Default.GetAsync(KeyName(id));
        }
        catch (Exception)
        {
            // Secure storage can be lost, as when the app is restored on another device: sign in again.
            return null;
        }
    }

    private sealed class KeyHandler(Servers servers) : DelegatingHandler(new HttpClientHandler { UseCookies = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (servers.Key is { } key)
                request.Headers.TryAddWithoutValidation(KeyHeader, key);
            return base.SendAsync(request, ct);
        }
    }

    public const string KeyHeader = "X-Api-Key";
}
