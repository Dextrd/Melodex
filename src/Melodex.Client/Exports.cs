namespace Melodex.Client;

/// <summary>What playlists are exported as.</summary>
public enum PlaylistExportFormat
{
    /// <summary>An M3U8 playlist, with each song's length, artist and title, and its file's path.</summary>
    M3u8,

    /// <summary>A CSV of each song's artist, title, album, length, ISRC and MusicBrainz ID, as other services' importers take.</summary>
    Csv,
}

/// <summary>What the library is exported as: a row for each song you have, or for each album.</summary>
public enum LibraryExportRows
{
    Songs,
    Albums,
}

/// <summary>A column of the library's CSV. Some are only for songs, some only for albums.</summary>
public enum LibraryExportColumn
{
    Artist,
    Album,
    Title,
    Year,
    Disc,
    Track,
    Length,
    Owned,
    Path,
    Format,
    Quality,
    Isrc,
    MusicBrainzId,
}

public static class LibraryExportColumns
{
    /// <summary>The columns each kind of row has, in order.</summary>
    public static IReadOnlyList<LibraryExportColumn> For(LibraryExportRows rows) => rows == LibraryExportRows.Songs
        ?
        [
            LibraryExportColumn.Artist, LibraryExportColumn.Album, LibraryExportColumn.Title, LibraryExportColumn.Year, LibraryExportColumn.Disc,
            LibraryExportColumn.Track, LibraryExportColumn.Length, LibraryExportColumn.Path, LibraryExportColumn.Format, LibraryExportColumn.Quality,
            LibraryExportColumn.Isrc, LibraryExportColumn.MusicBrainzId,
        ]
        :
        [
            LibraryExportColumn.Artist, LibraryExportColumn.Album, LibraryExportColumn.Year, LibraryExportColumn.Owned, LibraryExportColumn.Format,
            LibraryExportColumn.Quality, LibraryExportColumn.MusicBrainzId,
        ];

    /// <summary>Each column's name, as its header says it.</summary>
    public static string Name(LibraryExportColumn column) => column switch
    {
        LibraryExportColumn.Track => "Track number",
        LibraryExportColumn.Isrc => "ISRC",
        LibraryExportColumn.MusicBrainzId => "MusicBrainz ID",
        _ => column.ToString(),
    };
}
