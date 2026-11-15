namespace Melodex.Core.Data;

/// <summary>Where an import's songs came from.</summary>
public enum ImportSource
{
    /// <summary>An M3U or M3U8 playlist.</summary>
    M3u,

    /// <summary>A CSV file, its columns chosen by whoever imported it.</summary>
    Csv,

    /// <summary>Lines of "Artist – Title", pasted.</summary>
    Text,

    /// <summary>A playlist, liked songs or saved albums from Spotify's export of your data.</summary>
    Spotify,
}

/// <summary>How far an import has got.</summary>
public enum ImportStatus
{
    /// <summary>Its songs are being matched to the catalogue.</summary>
    Matching,

    /// <summary>Matched, waiting to be checked and applied.</summary>
    Ready,

    /// <summary>Applied: its playlist made and its songs requested, as chosen.</summary>
    Applied,
}

/// <summary>What one of an import's songs turned out to be.</summary>
public enum ImportOutcome
{
    /// <summary>Not looked at yet.</summary>
    Pending,

    /// <summary>In the library already.</summary>
    Owned,

    /// <summary>In the catalogue, to be requested.</summary>
    Found,

    /// <summary>The closest the catalogue has, but not close enough to be sure of.</summary>
    Unsure,

    /// <summary>Nothing in the library or the catalogue looks like it.</summary>
    NotFound,

    /// <summary>Left out by whoever imported it.</summary>
    Dropped,
}

/// <summary>
/// Songs brought in from somewhere else: a playlist file, a CSV or a pasted list. Each is matched to the library and the
/// catalogue first, then checked and applied, making or adding to a playlist and requesting what isn't owned.
/// </summary>
public class ListImport
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }

    /// <summary>What it's called: the file's name without its extension, or what it was given.</summary>
    public required string Name { get; set; }

    public ImportSource Source { get; set; }
    public ImportStatus Status { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AppliedAt { get; set; }

    /// <summary>The task matching its songs.</summary>
    public Guid? TaskId { get; set; }

    /// <summary>The playlist it made or added to, once applied.</summary>
    public Guid? PlaylistId { get; set; }

    /// <summary>Songs not owned were requested when it was applied, as are those matched later in Review.</summary>
    public bool RequestMissing { get; set; }

    /// <summary>The imports made together from one service's export, a list each.</summary>
    public Guid? BatchId { get; set; }

    /// <summary>Which of the service's lists it is, such as "liked" or "playlist:Road trip", to know it when it's imported again.</summary>
    public string? ServiceList { get; set; }

    /// <summary>Its lines are albums, such as saved albums, to request rather than put in a playlist.</summary>
    public bool Albums { get; set; }

    /// <summary>
    /// A playlist an earlier import of the same list made: applying this one adds to it what's new since, rather than
    /// making another.
    /// </summary>
    public Guid? UpdatesPlaylistId { get; set; }

    public List<ImportLine> Lines { get; set; } = [];
}

/// <summary>One song of an import, as it was given, and what it was matched to.</summary>
public class ImportLine
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ImportId { get; set; }
    public ListImport Import { get; set; } = null!;

    /// <summary>Its place in the list, from 0.</summary>
    public int Position { get; set; }

    public string Title { get; set; } = "";
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public double? DurationSeconds { get; set; }
    public string? Isrc { get; set; }
    public string? MusicBrainzId { get; set; }

    /// <summary>The path a playlist file gave, which may be a file in the library.</summary>
    public string? Path { get; set; }

    /// <summary>The service's own ID for it, such as a Spotify URI, so importing again brings only what's new.</summary>
    public string? ExternalId { get; set; }

    public ImportOutcome Outcome { get; set; }

    /// <summary>The catalogue track it was matched to.</summary>
    public Guid? TrackId { get; set; }

    /// <summary>A file of your own no catalogue knows, when that's what it is.</summary>
    public Guid? FileId { get; set; }

    /// <summary>For an import of albums, the catalogue album it was matched to.</summary>
    public Guid? AlbumId { get; set; }

    /// <summary>How sure the match is, from 0 to 1.</summary>
    public double? Confidence { get; set; }

    /// <summary>Its match was chosen by hand, so matching again leaves it be.</summary>
    public bool Chosen { get; set; }

    /// <summary>Not found when the import was applied, so it waits in Review to be matched or dismissed.</summary>
    public bool InReview { get; set; }

    /// <summary>The playlist entry it became.</summary>
    public Guid? EntryId { get; set; }
}
