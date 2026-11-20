using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Melodex.Core.Access;
using Melodex.Core.Data;

namespace Melodex.Core.Subsonic;

/// <summary>A call's parameters, from its query and, for a form post, its body; a name may come more than once.</summary>
public sealed class SubsonicParams(IReadOnlyDictionary<string, string[]> values)
{
    public static async Task<SubsonicParams> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string name, IEnumerable<string?> items)
        {
            if (!values.TryGetValue(name, out var list))
                values[name] = list = [];
            list.AddRange(items.OfType<string>());
        }
        foreach (var (name, items) in request.Query)
            Add(name, items);
        if (request.HasFormContentType)
            foreach (var (name, items) in await request.ReadFormAsync(ct))
                Add(name, items);
        return new SubsonicParams(values.ToDictionary(v => v.Key, v => v.Value.ToArray()));
    }

    public string? this[string name] => values.TryGetValue(name, out var items) && items.Length > 0 ? items[0] : null;

    public IReadOnlyList<string> All(string name) => values.TryGetValue(name, out var items) ? items : [];

    public int? Int(string name) => int.TryParse(this[name], out var value) ? value : null;

    public long? Long(string name) => long.TryParse(this[name], out var value) ? value : null;

    public bool? Bool(string name) => bool.TryParse(this[name], out var value) ? value : null;

    /// <summary>A count asked for, kept within reason.</summary>
    public int Count(string name, int fallback, int most = 500) => Math.Clamp(Int(name) ?? fallback, 0, most);
}

/// <summary>Subsonic's error codes.</summary>
public static class SubsonicErrors
{
    public const int Generic = 0, Missing = 10, ClientTooOld = 20, ServerTooOld = 30, WrongLogin = 40, TokenUnsupported = 41,
        MechanismUnsupported = 42, ConflictingMechanisms = 43, BadApiKey = 44, NotAllowed = 50, NotFound = 70;
}

/// <summary>
/// Who a Subsonic player is (#7): always an API key with the listen scope. OpenSubsonic's <c>apiKey</c> takes any;
/// players without it sign in with a username and, as the password, one of that person's keys made for Subsonic
/// players, sent as it is (<c>p</c>, perhaps hex-encoded) or as a token, an MD5 of it and a salt (<c>t</c> and
/// <c>s</c>). Their real password never works here.
/// </summary>
public sealed class SubsonicAuth(AccessService access, MelodexDbContext db, IDataProtectionProvider protection)
{
    private IDataProtector Protector => protection.CreateProtector("Melodex.SubsonicKey");

    public string Protect(string key) => Protector.Protect(key);

    public string? Unprotect(string? stored)
    {
        if (stored is null)
            return null;
        try
        {
            return Protector.Unprotect(stored);
        }
        catch (CryptographicException)
        {
            // The keys it was made with are gone: it has to be made again.
            return null;
        }
    }

    /// <summary>The caller, or the error to answer with.</summary>
    public async Task<(Caller? Caller, SubsonicResponse? Error)> IdentifyAsync(SubsonicParams p, string address, CancellationToken ct)
    {
        var apiKey = p["apiKey"];
        var username = p["u"];
        if (apiKey is not null)
        {
            if (username is not null || p["p"] is not null || p["t"] is not null)
                return (null, SubsonicResponse.Error(SubsonicErrors.ConflictingMechanisms, "Send an API key or a username and password, not both."));
            var app = await access.FromKeyAsync(apiKey, ct);
            if (app is null)
                return (null, SubsonicResponse.Error(SubsonicErrors.BadApiKey, "That isn't a Melodex API key."));
            return app.May(Scopes.Listen) && app.UserId is not null
                ? (app, null)
                : (null, SubsonicResponse.Error(SubsonicErrors.NotAllowed, "That API key can't listen: give it the listen scope."));
        }
        if (username is null)
            return (null, SubsonicResponse.Error(SubsonicErrors.Missing, "Sign in with an API key (apiKey), or a username (u) and a key made for Subsonic players as the password."));
        if (access.TooManyFailures(address))
            return (null, SubsonicResponse.Error(SubsonicErrors.WrongLogin, "Too many wrong passwords: wait a few minutes and try again."));

        var normalized = AccessService.Normalize(username);
        var secrets = await db.ApiKeys.AsNoTracking()
            .Where(k => k.SubsonicSecret != null && k.User != null && k.User.NormalizedUsername == normalized)
            .Select(k => k.SubsonicSecret).ToListAsync(ct);
        var key = secrets.Select(Unprotect).FirstOrDefault(k => k is not null && Matches(p, k));
        var caller = key is null ? null : await access.FromKeyAsync(key, ct);
        if (caller is null)
        {
            access.Failed(address);
            return (null, SubsonicResponse.Error(SubsonicErrors.WrongLogin,
                "Wrong username or password. Players sign in with an API key made for Subsonic players, under Settings → API in Melodex, not your own password."));
        }
        access.Succeeded(address);
        return caller.May(Scopes.Listen)
            ? (caller, null)
            : (null, SubsonicResponse.Error(SubsonicErrors.NotAllowed, "That key can't listen: you or it may no longer have the Listen permission."));
    }

    private static bool Matches(SubsonicParams p, string expected)
    {
        if (p["t"] is { } token && p["s"] is { } salt)
        {
            var hash = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(expected + salt)));
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(token.ToLowerInvariant()));
        }
        if (p["p"] is not { } given)
            return false;
        if (given.StartsWith("enc:", StringComparison.Ordinal))
        {
            try
            {
                given = Encoding.UTF8.GetString(Convert.FromHexString(given[4..]));
            }
            catch (FormatException)
            {
                return false;
            }
        }
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));
    }
}
