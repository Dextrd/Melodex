namespace Melodex.Contracts.Plugins.Scrobbling;

/// <summary>
/// Scrobbling plugin contract (version 1.0): sends what people play to a listening service, such as ListenBrainz or
/// Last.fm, on accounts they link themselves. Every enabled scrobbling plugin someone has linked gets their plays.
/// </summary>
/// <remarks>
/// The core keeps each person's linked account, with its secret encrypted, the plays still to send, and when to try
/// again; the plugin links accounts and talks to its service. A plugin links accounts one of two ways, which its
/// <see cref="ScrobblingCapabilities"/> say: a token the person pastes (<see cref="ScrobblingCapabilities.LinkToken"/>), or
/// the service's own page, which sends them back with one (<see cref="ScrobblingCapabilities.LinkWeb"/>).
/// <para>
/// Throw <see cref="ScrobbleLinkRefusedException"/> when the service no longer takes an account's link, and
/// <see cref="ScrobbleRejectedException"/> when it turns the plays themselves down; anything else, such as the service
/// being down, has the plays tried again later.
/// </para>
/// </remarks>
public interface IScrobblingPlugin : IPlugin
{
    /// <summary>
    /// Where to send someone to allow Melodex on the service, for <see cref="ScrobblingCapabilities.LinkWeb"/>: the
    /// service sends them back to <paramref name="callback"/> with a token in the address, as <c>token</c>.
    /// </summary>
    Task<Uri> LinkPageAsync(Uri callback, CancellationToken cancellationToken) =>
        throw new NotSupportedException("It's linked with a token.");

    /// <summary>
    /// Links an account from the token someone pasted, or the one the service sent them back with: the account's
    /// name, and the secret to send plays with.
    /// </summary>
    /// <exception cref="ScrobbleLinkException">The service doesn't take the token; its message is shown to them.</exception>
    Task<LinkedAccount> LinkAsync(string token, CancellationToken cancellationToken);

    /// <summary>Sends plays, oldest first, at most <see cref="ScrobblingLimits.Batch"/> at a time.</summary>
    Task ScrobbleAsync(string secret, IReadOnlyList<Listen> listens, CancellationToken cancellationToken);

    /// <summary>Tells the service what's playing now, for <see cref="ScrobblingCapabilities.NowPlaying"/>. Only a nicety: failures are passed over.</summary>
    Task NowPlayingAsync(string secret, Listen listen, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>An account linked on a service.</summary>
/// <param name="Name">Its name on the service, shown to the person.</param>
/// <param name="Secret">What plays are sent with, such as a user token or session key. The core keeps it encrypted.</param>
public sealed record LinkedAccount(string Name, string Secret);

/// <summary>A song someone played.</summary>
/// <param name="Title">The song's title.</param>
/// <param name="Artist">Its artist credit.</param>
/// <param name="PlayedAt">When it started.</param>
/// <param name="Album">The album it was played from, if any.</param>
/// <param name="Duration">How long it is.</param>
/// <param name="RecordingMbid">Its MusicBrainz recording ID, if known.</param>
/// <param name="ReleaseMbid">Its MusicBrainz release ID, if known.</param>
/// <param name="Player">What played it: <c>Melodex</c> for the web player, or a Subsonic player's name.</param>
public sealed record Listen(
    string Title,
    string Artist,
    DateTimeOffset PlayedAt,
    string? Album = null,
    TimeSpan? Duration = null,
    string? RecordingMbid = null,
    string? ReleaseMbid = null,
    string Player = "Melodex");

/// <summary>The token isn't one the service takes, with why, in words to show.</summary>
public class ScrobbleLinkException(string message) : Exception(message);

/// <summary>The service no longer takes the account's link: nothing more is sent until it's linked again.</summary>
public class ScrobbleLinkRefusedException(string message) : Exception(message);

/// <summary>The service turned these plays down, so they aren't tried again.</summary>
public class ScrobbleRejectedException(string message) : Exception(message);

/// <summary>What a scrobbling plugin offers.</summary>
public static class ScrobblingCapabilities
{
    /// <summary>Linked with a token the person pastes, such as a ListenBrainz user token.</summary>
    public const string LinkToken = "link-token";

    /// <summary>Linked on the service's own page, which sends the person back with a token.</summary>
    public const string LinkWeb = "link-web";

    /// <summary>Tells the service what's playing now.</summary>
    public const string NowPlaying = "now-playing";
}

/// <summary>Limits the core keeps to.</summary>
public static class ScrobblingLimits
{
    /// <summary>The most plays sent in one call.</summary>
    public const int Batch = 50;
}
