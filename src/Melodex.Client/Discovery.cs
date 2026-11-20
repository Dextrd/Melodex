namespace Melodex.Client;

/// <summary>An artist like another, from the discovery plugins (#3).</summary>
/// <param name="Id">The artist in the catalogue, when they're matched to one; otherwise only their name is known.</param>
/// <param name="Name">Their name.</param>
/// <param name="Disambiguation">Short text that tells same-named artists apart.</param>
/// <param name="InLibrary">You have music of theirs.</param>
/// <param name="ImageUrl">Their picture, when they're in the catalogue.</param>
/// <param name="Score">How close, from 0 to 1, combined across the plugins that found them.</param>
/// <param name="From">The plugins that found them.</param>
public sealed record SimilarArtist(Guid? Id, string Name, string? Disambiguation, bool InLibrary, string? ImageUrl, double Score, IReadOnlyList<string> From);

/// <summary>A song the discovery plugins found: like another, or recommended for you (#3).</summary>
/// <param name="TrackId">The song in the catalogue, when it's matched to one.</param>
/// <param name="Title">Its title.</param>
/// <param name="ArtistCredit">Its artist credit.</param>
/// <param name="ArtistId">Its artist in the catalogue, when they're matched to one.</param>
/// <param name="AlbumId">An album it's on in the catalogue, for its cover.</param>
/// <param name="AlbumTitle">An album it's on.</param>
/// <param name="CoverUrl">That album's cover.</param>
/// <param name="DurationSeconds">How long it is.</param>
/// <param name="Owned">It's in your library.</param>
/// <param name="Score">How close, or how good a recommendation, from 0 to 1, combined across the plugins that found it.</param>
/// <param name="From">The plugins that found it.</param>
public sealed record DiscoveredSong(Guid? TrackId, string Title, string ArtistCredit, Guid? ArtistId, Guid? AlbumId, string? AlbumTitle, string? CoverUrl,
    double? DurationSeconds, bool Owned, double Score, IReadOnlyList<string> From);

/// <summary>What the discovery plugins that are on can find.</summary>
/// <param name="SimilarArtists">Artists like an artist.</param>
/// <param name="SimilarSongs">Songs like a song.</param>
/// <param name="Recommendations">Songs recommended for someone.</param>
public sealed record DiscoveryStatus(bool SimilarArtists, bool SimilarSongs, bool Recommendations);
