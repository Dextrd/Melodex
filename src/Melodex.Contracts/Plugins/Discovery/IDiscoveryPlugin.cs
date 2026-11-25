using Melodex.Contracts.Catalogue;

namespace Melodex.Contracts.Plugins.Discovery;

/// <summary>
/// Discovery plugin contract (version 1.1): music related to what someone plays. Similar artists for an artist, similar
/// songs for a song, songs to recommend to a listener, and an artist's genres (1.1), each with a score. The core asks the enabled discovery
/// plugins in the user's order, combines what they say, and matches it to the catalogue and the library.
/// </summary>
/// <remarks>
/// A plugin declares which of these it answers with <see cref="DiscoveryCapabilities"/>; the core only asks for
/// those. Return an empty list when the plugin knows nothing for the query, and throw for anything else, such as network
/// failures, so the core leaves the plugin out of that answer instead of remembering that there's nothing. Scores are
/// the plugin's own, higher meaning closer: the core scales each plugin's to its best before combining them.
/// </remarks>
public interface IDiscoveryPlugin : IPlugin
{
    /// <summary>Artists like this one, closest first.</summary>
    Task<IReadOnlyList<DiscoveredArtist>> SimilarArtistsAsync(DiscoveryArtist artist, int count, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DiscoveredArtist>>([]);

    /// <summary>Songs like this one, closest first.</summary>
    Task<IReadOnlyList<DiscoveredRecording>> SimilarRecordingsAsync(DiscoveryRecording recording, int count, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DiscoveredRecording>>([]);

    /// <summary>Songs to recommend to a listener, best first.</summary>
    Task<IReadOnlyList<DiscoveredRecording>> RecommendAsync(DiscoveryListener listener, int count, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DiscoveredRecording>>([]);

    /// <summary>An artist's genres, the most fitting first (contract 1.1).</summary>
    Task<IReadOnlyList<DiscoveredGenre>> ArtistGenresAsync(DiscoveryArtist artist, int count, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DiscoveredGenre>>([]);
}

/// <summary>An artist to find others like, or one the plugin found.</summary>
/// <param name="Name">The artist's name.</param>
/// <param name="Ids">The artist's IDs, such as their MusicBrainz artist ID.</param>
public sealed record DiscoveryArtist(string Name, IReadOnlyList<ProviderId> Ids);

/// <summary>A song to find others like.</summary>
/// <param name="Title">The song's title.</param>
/// <param name="Artist">Its artist credit.</param>
/// <param name="Ids">The recording's IDs, such as its MusicBrainz recording ID and ISRCs.</param>
/// <param name="Artists">The artists credited, with their IDs.</param>
/// <param name="Duration">How long it is, to tell versions apart.</param>
public sealed record DiscoveryRecording(string Title, string Artist, IReadOnlyList<ProviderId> Ids, IReadOnlyList<DiscoveryArtist> Artists,
    TimeSpan? Duration = null);

/// <summary>Someone to recommend music to.</summary>
/// <param name="Accounts">
/// Their names on services they've linked to Melodex, by service: <c>listenbrainz</c> or <c>lastfm</c>. A plugin
/// recommending from what a service knows of someone answers only when they've linked it.
/// </param>
/// <param name="TopArtists">The artists they've played most lately, most first.</param>
/// <param name="TopRecordings">The songs they've played most lately, most first.</param>
public sealed record DiscoveryListener(IReadOnlyDictionary<string, string> Accounts, IReadOnlyList<DiscoveryArtist> TopArtists,
    IReadOnlyList<DiscoveryRecording> TopRecordings);

/// <summary>An artist a plugin found.</summary>
/// <param name="Name">Their name.</param>
/// <param name="Ids">Their IDs, such as a MusicBrainz artist ID, which match them in the catalogue. Name alone matches only when it's clear.</param>
/// <param name="Score">How close, in the plugin's own terms: higher is closer.</param>
/// <param name="Disambiguation">Short text that tells same-named artists apart, if the plugin has it.</param>
public sealed record DiscoveredArtist(string Name, IReadOnlyList<ProviderId> Ids, double Score, string? Disambiguation = null);

/// <summary>A song a plugin found.</summary>
/// <param name="Title">Its title.</param>
/// <param name="Artist">Its artist credit.</param>
/// <param name="Ids">The recording's IDs, such as its MusicBrainz recording ID and ISRCs.</param>
/// <param name="Artists">The artists credited, with their IDs where the plugin has them.</param>
/// <param name="Score">How close, or how good a recommendation, in the plugin's own terms: higher is better.</param>
/// <param name="Album">An album it's on, if the plugin knows one.</param>
/// <param name="Duration">How long it is.</param>
public sealed record DiscoveredRecording(string Title, string Artist, IReadOnlyList<ProviderId> Ids, IReadOnlyList<DiscoveryArtist> Artists,
    double Score, string? Album = null, TimeSpan? Duration = null);

/// <summary>A genre a plugin gives an artist.</summary>
/// <param name="Name">The genre, in lower case as MusicBrainz names them, such as <c>trip hop</c>.</param>
/// <param name="Score">How well it fits, in the plugin's own terms, such as how many people tagged it: higher fits better.</param>
public sealed record DiscoveredGenre(string Name, double Score);

/// <summary>Capability names a discovery plugin declares.</summary>
public static class DiscoveryCapabilities
{
    /// <summary>Artists like an artist.</summary>
    public const string SimilarArtists = "similar-artists";

    /// <summary>Songs like a song.</summary>
    public const string SimilarRecordings = "similar-recordings";

    /// <summary>Songs to recommend to a listener.</summary>
    public const string Recommendations = "recommendations";

    /// <summary>An artist's genres (contract 1.1).</summary>
    public const string ArtistGenres = "artist-genres";
}

/// <summary>
/// Service names in <see cref="DiscoveryListener.Accounts"/>: for each account the listener linked with a scrobbling
/// plugin, the last part of that plugin's ID, so <c>melodex.scrobbling.listenbrainz</c> gives <c>listenbrainz</c>.
/// </summary>
public static class DiscoveryAccounts
{
    /// <summary>A ListenBrainz user name.</summary>
    public const string ListenBrainz = "listenbrainz";

    /// <summary>A Last.fm user name.</summary>
    public const string LastFm = "lastfm";
}
