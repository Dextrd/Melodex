namespace Melodex.Client;

/// <summary>What a mix is (#5).</summary>
public enum MixKind
{
    /// <summary>Made each day around a group of artists you play, mostly songs you have.</summary>
    Daily,

    /// <summary>Made each Monday from songs you don't have, like what you play.</summary>
    Weekly,
}

/// <summary>A mix made for you, as the For you tab lists it (#5).</summary>
/// <param name="Name">"Daily mix 1", or "Weekly discoveries".</param>
/// <param name="Artists">Who's in it: the artists it was made around, or most heard in it.</param>
/// <param name="Covers">Up to four covers of its songs' albums, for its tile.</param>
/// <param name="Songs">How many songs it has.</param>
/// <param name="MadeAt">When it was made; daily mixes are remade each day, the weekly one each Monday.</param>
/// <param name="Kept">The playlist you kept it as, if you did and it's still there.</param>
public sealed record MixSummary(Guid Id, MixKind Kind, string Name, IReadOnlyList<string> Artists, IReadOnlyList<string> Covers, int Songs, DateTime MadeAt,
    KeptMix? Kept = null);

/// <summary>The playlist a mix was kept as.</summary>
/// <param name="Changed">Whether the playlist has been changed since it was kept: renamed, or songs added, taken out or moved.</param>
public sealed record KeptMix(Guid PlaylistId, string Name, bool Changed);

/// <summary>A mix with its songs, in order: each says whether you have it, so the rest are fetched to listen to.</summary>
public sealed record MixDetail(MixSummary Mix, IReadOnlyList<DiscoveredSong> Songs);

/// <summary>Body of <c>POST /mixes/{id}/keep</c>: the playlist name to keep it under; by default the mix's name and today's date.</summary>
public sealed record KeepMix(string? Name = null);
