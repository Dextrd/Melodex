namespace Melodex.Client;

/// <summary>What kind of list is being imported.</summary>
public enum ImportFormat
{
    /// <summary>An M3U or M3U8 playlist.</summary>
    M3u,

    /// <summary>A CSV file, its columns chosen when it's imported.</summary>
    Csv,

    /// <summary>Lines of "Artist – Title".</summary>
    Text,
}

/// <summary>What a CSV column holds.</summary>
public enum ImportColumn
{
    /// <summary>Nothing to use.</summary>
    Ignore,
    Artist,
    Title,
    Album,

    /// <summary>The song's length: minutes and seconds (3:45), seconds, or milliseconds.</summary>
    Length,
    Isrc,

    /// <summary>The MusicBrainz recording ID.</summary>
    MusicBrainzId,
}

/// <summary>How far an import has got.</summary>
public enum ImportState
{
    /// <summary>Its songs are being matched.</summary>
    Matching,

    /// <summary>Matched, waiting to be checked and applied.</summary>
    Ready,

    /// <summary>Applied.</summary>
    Applied,
}

/// <summary>What one of an import's songs turned out to be.</summary>
public enum ImportOutcome
{
    /// <summary>Not looked at yet.</summary>
    Pending,

    /// <summary>In your library already.</summary>
    Owned,

    /// <summary>In the catalogue, to be requested.</summary>
    Found,

    /// <summary>The closest the catalogue has, but not close enough to be sure of: check it.</summary>
    Unsure,

    /// <summary>Nothing looks like it.</summary>
    NotFound,

    /// <summary>Left out.</summary>
    Dropped,
}

/// <summary>Body of <c>POST /imports/peek</c>: a list to look at before importing it.</summary>
/// <param name="Text">The file's text, or what was pasted.</param>
/// <param name="FileName">The file's name, which says what kind it is; none for pasted text.</param>
public sealed record PeekImport(string Text, string? FileName = null);

/// <summary>What a list looks like, from <c>POST /imports/peek</c>, to choose a CSV's columns.</summary>
/// <param name="Columns">A CSV's column names, from its first row or numbered; none for other kinds.</param>
/// <param name="Guess">What each column looks to hold.</param>
/// <param name="HasHeader">Whether the first row looks to be the columns' names.</param>
/// <param name="Sample">The first few songs, as they'd be read with the guessed columns.</param>
/// <param name="Rows">A CSV's rows, the first few, cell by cell, to show beside the columns.</param>
public sealed record ImportPeek(
    ImportFormat Format,
    int Count,
    IReadOnlyList<string> Columns,
    IReadOnlyList<ImportColumn> Guess,
    bool HasHeader,
    IReadOnlyList<ImportSong> Sample,
    IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>A song as a list gives it.</summary>
public sealed record ImportSong(string Title, string? Artist, string? Album, double? DurationSeconds, string? Isrc = null, string? MusicBrainzId = null, string? Path = null);

/// <summary>Body of <c>POST /imports</c>: a list to import. Its songs are matched in the background, as a task in Activity.</summary>
/// <param name="Format">What kind of list it is; worked out from the file's name and text when not given.</param>
/// <param name="Columns">For a CSV, what each column holds; guessed when not given.</param>
/// <param name="HasHeader">For a CSV, whether the first row is the columns' names; guessed when not given.</param>
/// <param name="Name">What to call it, and the playlist it makes; the file's name when not given.</param>
public sealed record CreateImport(
    string Text,
    string? FileName = null,
    ImportFormat? Format = null,
    IReadOnlyList<ImportColumn>? Columns = null,
    bool? HasHeader = null,
    string? Name = null);

/// <summary>An import, as listed.</summary>
/// <param name="Counts">How many songs came out each way.</param>
/// <param name="PlaylistName">The playlist it made or added to, once applied.</param>
/// <param name="TaskId">The task matching its songs, to watch in Activity.</param>
/// <param name="InReview">Songs not found when it was applied, still waiting in Review.</param>
public sealed record ImportSummary(
    Guid Id,
    string Name,
    ImportFormat Format,
    ImportState State,
    DateTime CreatedAt,
    DateTime? AppliedAt,
    int Total,
    IReadOnlyDictionary<ImportOutcome, int> Counts,
    Guid? PlaylistId,
    string? PlaylistName,
    Guid? TaskId,
    int InReview = 0);

/// <summary>What an import's song was matched to.</summary>
/// <param name="TrackId">The catalogue track; null for a file of your own.</param>
/// <param name="FileId">A file of your own, for one no catalogue knows.</param>
public sealed record ImportMatch(
    Guid? TrackId,
    Guid? FileId,
    string Title,
    string Artist,
    Guid? ArtistId,
    Guid? AlbumId,
    string? Album,
    string? CoverUrl,
    double? DurationSeconds);

/// <summary>One of an import's songs, as given, and what it was matched to.</summary>
/// <param name="Chosen">Its match was chosen by hand.</param>
/// <param name="InReview">Not found when the import was applied: waiting in Review.</param>
public sealed record ImportLineInfo(
    Guid Id,
    int Position,
    ImportSong Song,
    ImportOutcome Outcome,
    ImportMatch? Match,
    double? Confidence,
    bool Chosen,
    bool InReview);

/// <summary>An import and its songs, from <c>GET /imports/{id}</c>.</summary>
public sealed record ImportDetail(ImportSummary Import, IReadOnlyList<ImportLineInfo> Lines);

/// <summary>Body of <c>PUT /imports/{id}/lines/{lineId}</c>: choose the song a line is, or leave it out or put it back.</summary>
/// <param name="TrackId">The catalogue track it is.</param>
/// <param name="Dropped">True to leave it out; false to put it back as it was matched.</param>
public sealed record ChangeImportLine(Guid? TrackId = null, bool? Dropped = null);

/// <summary>Where an import's songs go.</summary>
public enum ImportPlaylistMode
{
    /// <summary>A new playlist.</summary>
    New,

    /// <summary>The end of a playlist you have.</summary>
    Existing,

    /// <summary>No playlist: only request what's missing.</summary>
    None,
}

/// <summary>Body of <c>POST /imports/{id}/apply</c>.</summary>
/// <param name="Name">The new playlist's name, folders and all; the import's name when not given.</param>
/// <param name="PlaylistId">The playlist to add to.</param>
/// <param name="RequestMissing">Request every song found that you don't own.</param>
/// <param name="NotFoundToReview">Send songs not found to Review, to match there; otherwise they're left out.</param>
public sealed record ApplyImport(
    ImportPlaylistMode Playlist = ImportPlaylistMode.New,
    string? Name = null,
    Guid? PlaylistId = null,
    bool RequestMissing = true,
    bool NotFoundToReview = true);

/// <summary>What applying an import did.</summary>
/// <param name="Added">Songs put in the playlist, owned or waiting for their files.</param>
/// <param name="Requested">Songs asked for.</param>
/// <param name="ToReview">Songs sent to Review.</param>
public sealed record ImportApplied(Guid? PlaylistId, int Added, int Requested, int ToReview);

/// <summary>An imported song nothing was found for, waiting in Review, from <c>GET /imports/unmatched</c>.</summary>
public sealed record UnmatchedImportSong(
    Guid LineId,
    Guid ImportId,
    string ImportName,
    Guid? PlaylistId,
    string? PlaylistName,
    ImportSong Song,
    DateTime ImportedAt);

/// <summary>Body of <c>POST /imports/unmatched/{lineId}/match</c>: the song it is.</summary>
public sealed record MatchImportSong(Guid TrackId);
