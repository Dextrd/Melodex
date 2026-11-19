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
/// Who a Subsonic player is (#7). OpenSubsonic's <c>apiKey</c> takes a Melodex API key with the listen scope; players
/// without it sign in with a username and the person's app password, kept apart from their real one, sent as it is
/// (<c>p</c>, perhaps hex-encoded) or as a token, an MD5 of it and a salt (<c>t</c> and <c>s</c>).
/// </summary>
public sealed class SubsonicAuth(AccessService access, MelodexDbContext db, IDataProtectionProvider protection)
{
    private IDataProtector Protector => protection.CreateProtector("Melodex.SubsonicPassword");

    public string Protect(string password) => Protector.Protect(password);

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

    /// <summary>A new app password: long enough to be safe, in letters and digits a phone keyboard types easily.</summary>
    public static string NewPassword()
    {
        const string letters = "abcdefghjkmnpqrstuvwxyz23456789";
        var chars = new char[20];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = letters[RandomNumberGenerator.GetInt32(letters.Length)];
        // In fours, to read out and type.
        return string.Join('-', chars.Chunk(5).Select(c => new string(c)));
    }

    /// <summary>The caller, or the error to answer with.</summary>
    public async Task<(Caller? Caller, SubsonicResponse? Error)> IdentifyAsync(SubsonicParams p, string address, CancellationToken ct)
    {
        var key = p["apiKey"];
        var username = p["u"];
        if (key is not null)
        {
            if (username is not null || p["p"] is not null || p["t"] is not null)
                return (null, SubsonicResponse.Error(SubsonicErrors.ConflictingMechanisms, "Send an API key or a username and password, not both."));
            var caller = await access.FromKeyAsync(key, ct);
            if (caller is null)
                return (null, SubsonicResponse.Error(SubsonicErrors.BadApiKey, "That isn't a Melodex API key."));
            return caller.May(Scopes.Listen) && caller.UserId is not null
                ? (caller, null)
                : (null, SubsonicResponse.Error(SubsonicErrors.NotAllowed, "That API key can't listen: give it the listen scope."));
        }
        if (username is null)
            return (null, SubsonicResponse.Error(SubsonicErrors.Missing, "Sign in with an API key (apiKey) or a username (u) and app password."));
        if (access.TooManyFailures(address))
            return (null, SubsonicResponse.Error(SubsonicErrors.WrongLogin, "Too many wrong passwords: wait a few minutes and try again."));

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedUsername == AccessService.Normalize(username), ct);
        var expected = Unprotect(user?.SubsonicPassword);
        if (user is null || expected is null || !Matches(p, expected))
        {
            access.Failed(address);
            return (null, SubsonicResponse.Error(SubsonicErrors.WrongLogin,
                "Wrong username or password. Players sign in with your app password, from Your account in Melodex, not your own password."));
        }
        access.Succeeded(address);
        var scopes = AccessService.PermissionsOf(user);
        if (!user.IsAdmin && !scopes.Contains(Scopes.Listen))
            return (null, SubsonicResponse.Error(SubsonicErrors.NotAllowed, "You can't listen in Melodex. The admin can let you, under Settings → Users."));
        return (new Caller(CallerKind.User, user.Username, scopes, user.Id, IsAdmin: user.IsAdmin), null);
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
