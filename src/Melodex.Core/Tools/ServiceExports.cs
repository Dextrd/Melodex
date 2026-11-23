using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Melodex.Client;

namespace Melodex.Core.Tools;

/// <summary>A song, or an album (its title as the title), in a service's list, with the service's own ID for it.</summary>
public sealed record ServiceItem(ImportSong Song, string? Id);

/// <summary>One list from a service's export: a playlist, liked songs, or saved albums.</summary>
public sealed record ServiceListData(string Key, string Name, ServiceListKind Kind, IReadOnlyList<ServiceItem> Items);

/// <summary>What a service's export holds.</summary>
public sealed record ServiceExport(ImportFormat Service, IReadOnlyList<ServiceListData> Lists);

/// <summary>
/// Reads the copies of your data that music services give you: the zip they send, or the files from it. Each service's
/// files are recognised by their names and shapes; what isn't recognised is left out.
/// </summary>
public static class ServiceExports
{
    /// <summary>The most a file inside a zip may be: 50 MB.</summary>
    private const long MaxEntry = 50 * 1024 * 1024;

    /// <summary>Reads the files given, unzipping any zip; null if none is an export Melodex knows.</summary>
    public static ServiceExport? Read(IReadOnlyList<(string Name, byte[] Bytes)> files)
    {
        var unpacked = new List<(string Name, byte[] Bytes)>();
        foreach (var (name, bytes) in files)
        {
            // A spreadsheet is a zip too, but it's read whole.
            if (bytes.Length > 4 && bytes[0] == 'P' && bytes[1] == 'K' && !IsSpreadsheet(bytes))
                unpacked.AddRange(Unzip(bytes));
            else
                unpacked.Add((name, bytes));
        }
        return Spotify(unpacked) ?? YouTubeMusic(unpacked) ?? Deezer(unpacked) ?? AppleMusic(unpacked);
    }

    private static bool IsSpreadsheet(byte[] bytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            return zip.GetEntry("xl/workbook.xml") is not null;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    // Zips inside zips are opened too, as Apple's are, this many deep.
    private const int Nested = 3;

    private static IEnumerable<(string Name, byte[] Bytes)> Unzip(byte[] bytes, int depth = 0)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            var nested = entry.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            if (entry.Length is 0 or > MaxEntry || nested && depth >= Nested
                || !nested && !new[] { ".json", ".csv", ".xlsx" }.Any(e => entry.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase))
                || !nested && IsApple(entry.FullName) && !AppleFiles.Any(f => entry.Name.Equals(f, StringComparison.OrdinalIgnoreCase)))
                continue;
            using var stream = entry.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            if (!nested)
                yield return (entry.FullName, copy.ToArray());
            else
                foreach (var inner in Unzip(copy.ToArray(), depth + 1))
                    yield return ($"{entry.FullName}/{inner.Name}", inner.Bytes);
        }
    }

    // ---- Spotify ----

    /// <summary>
    /// Spotify's "Account data": YourLibrary.json, with liked songs ("tracks") and saved albums, and Playlist1.json and on,
    /// each with "playlists", whose items are tracks (or episodes and local files).
    /// </summary>
    private static ServiceExport? Spotify(IReadOnlyList<(string Name, byte[] Bytes)> files)
    {
        var lists = new List<ServiceListData>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var known = false;
        foreach (var (path, bytes) in files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path.Replace('\\', '/'));
            JsonElement root;
            try
            {
                root = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = true }).RootElement;
            }
            catch (JsonException)
            {
                continue;
            }
            if (root.ValueKind != JsonValueKind.Object)
                continue;

            if (name.StartsWith("YourLibrary", StringComparison.OrdinalIgnoreCase) && root.TryGetProperty("tracks", out var liked))
            {
                known = true;
                var songs = Items(liked).Select(t => Item(Text(t, "track"), Text(t, "artist"), Text(t, "album"), Text(t, "uri"))).OfType<ServiceItem>().ToList();
                if (songs.Count > 0)
                    lists.Add(new ServiceListData("liked", "Liked Songs", ServiceListKind.Liked, songs));
                if (root.TryGetProperty("albums", out var saved))
                {
                    var albums = Items(saved).Select(a => Item(Text(a, "album"), Text(a, "artist"), null, Text(a, "uri"))).OfType<ServiceItem>().ToList();
                    if (albums.Count > 0)
                        lists.Add(new ServiceListData("albums", "Saved albums", ServiceListKind.Albums, albums));
                }
            }
            else if (root.TryGetProperty("playlists", out var playlists) && playlists.ValueKind == JsonValueKind.Array)
            {
                known = true;
                foreach (var playlist in playlists.EnumerateArray())
                {
                    var title = Text(playlist, "name") ?? "Playlist";
                    var songs = Items(playlist.TryGetProperty("items", out var items) ? items : default).Select(PlaylistItem).OfType<ServiceItem>().ToList();
                    var key = $"playlist:{title}";
                    for (var n = 2; !keys.Add(key); n++)
                        key = $"playlist:{title} ({n})";
                    lists.Add(new ServiceListData(key, key["playlist:".Length..], ServiceListKind.Playlist, songs));
                }
            }
        }
        // Liked songs and saved albums first, then the playlists as they came.
        return known ? new ServiceExport(ImportFormat.Spotify, lists.OrderBy(l => l.Kind switch { ServiceListKind.Liked => 0, ServiceListKind.Albums => 1, _ => 2 }).ToList()) : null;
    }

    // ---- YouTube Music ----

    /// <summary>The prefix of a line's ID for a YouTube video, whose title and channel are looked up when it's matched.</summary>
    public const string YouTubePrefix = "youtube:";

    /// <summary>
    /// Google Takeout's "YouTube and YouTube Music": "music library songs.csv" (Video ID, Song Title, Album Title, Artist Name
    /// 1 and on, the first being the artist), and a "{playlist}-videos.csv" for each playlist, which has only its videos'
    /// IDs. Those are filled in from the library's songs where they're the same, and looked up when matched otherwise.
    /// Watch later is left out: it's videos to watch, not music.
    /// </summary>
    private static ServiceExport? YouTubeMusic(IReadOnlyList<(string Name, byte[] Bytes)> files)
    {
        var library = new List<ServiceItem>();
        var playlists = new List<(string Name, List<string> Ids)>();
        var known = false;
        foreach (var (path, bytes) in files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path.Replace('\\', '/'));
            if (!name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                continue;
            var rows = ListParser.CsvRows(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')).Where(r => r.Any(c => c.Length > 0)).ToList();
            if (rows.Count == 0 || rows[0].Count == 0 || !rows[0][0].Trim().Equals("Video ID", StringComparison.OrdinalIgnoreCase))
                continue;
            var header = rows[0].Select(c => c.Trim()).ToList();
            if (header.Contains("Song Title", StringComparer.OrdinalIgnoreCase))
            {
                known = true;
                int Column(string column) => header.FindIndex(c => c.Equals(column, StringComparison.OrdinalIgnoreCase));
                var (title, album, artist) = (Column("Song Title"), Column("Album Title"), Column("Artist Name 1"));
                foreach (var row in rows.Skip(1))
                    if (Cell(row, title) is { } song)
                        library.Add(new ServiceItem(new ImportSong(song, Cell(row, artist), Cell(row, album), null), YouTubePrefix + row[0].Trim()));
            }
            else if (name.EndsWith("-videos.csv", StringComparison.OrdinalIgnoreCase))
            {
                known = true;
                var playlist = name[..^"-videos.csv".Length];
                if (!playlist.Equals("Watch later", StringComparison.OrdinalIgnoreCase))
                    playlists.Add((playlist, rows.Skip(1).Select(r => r[0].Trim()).Where(id => id.Length > 0).ToList()));
            }
        }
        if (!known)
            return null;

        var byId = library.Where(i => i.Id is not null).GroupBy(i => i.Id!).ToDictionary(g => g.Key, g => g.First().Song);
        var lists = new List<ServiceListData>();
        if (library.Count > 0)
            lists.Add(new ServiceListData("library", "Library songs", ServiceListKind.Playlist, library));
        foreach (var (name, ids) in playlists)
            lists.Add(new ServiceListData($"playlist:{name}", name, ServiceListKind.Playlist,
                ids.Select(id => new ServiceItem(byId.GetValueOrDefault(YouTubePrefix + id) ?? new ImportSong("", null, null, null), YouTubePrefix + id)).ToList()));
        return new ServiceExport(ImportFormat.YouTubeMusic, lists);

        static string? Cell(IReadOnlyList<string> row, int index) => index >= 0 && index < row.Count && row[index].Trim() is { Length: > 0 } cell ? cell : null;
    }

    // ---- Deezer ----

    /// <summary>The prefix of a line's ID for a Deezer track or album: "deezer:track:138547415".</summary>
    public const string DeezerPrefix = "deezer:";

    /// <summary>
    /// Deezer's copy of your personal data (Account settings → Privacy → My personal data): a spreadsheet with a sheet for
    /// each kind of data. Only these are read, the rest (your account, devices and listening) left unopened:
    /// "favoriteSong" (Song Title, Artists, Album Title, ISRC, Link), "favoriteAlbum" (Album Title, Artists, Link), and
    /// "playlistCreated", a row for each song of each playlist you made (Playlist Title, Playlist Link, then the song's
    /// columns, its link as Track Link). Favourite artists are follows, not music to import.
    /// </summary>
    private static ServiceExport? Deezer(IReadOnlyList<(string Name, byte[] Bytes)> files)
    {
        var lists = new List<ServiceListData>();
        var known = false;
        foreach (var (_, bytes) in files)
        {
            Dictionary<string, List<Dictionary<string, string>>> sheets;
            try
            {
                sheets = Spreadsheet.Read(bytes, name => name.EndsWith("favoriteSong", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("favoriteAlbum", StringComparison.OrdinalIgnoreCase) || name.EndsWith("playlistCreated", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException)
            {
                continue;
            }
            if (sheets.Count == 0)
                continue;
            known = true;
            foreach (var (name, rows) in sheets.OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                if (name.EndsWith("favoriteSong", StringComparison.OrdinalIgnoreCase))
                {
                    var songs = rows.Select(r => Song(r, "Link")).OfType<ServiceItem>().ToList();
                    if (songs.Count > 0)
                        lists.Add(new ServiceListData("liked", "Favourite tracks", ServiceListKind.Liked, songs));
                }
                else if (name.EndsWith("favoriteAlbum", StringComparison.OrdinalIgnoreCase))
                {
                    var albums = rows.Select(r => Item(Get(r, "Album Title"), Get(r, "Artists") ?? Get(r, "Artist"), null, DeezerId(Get(r, "Link"))))
                        .OfType<ServiceItem>().ToList();
                    if (albums.Count > 0)
                        lists.Add(new ServiceListData("albums", "Favourite albums", ServiceListKind.Albums, albums));
                }
                else
                {
                    // Known by its link, as two can share a name; in the order they first come.
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var playlist in rows.GroupBy(r => Get(r, "Playlist Link") ?? Get(r, "Playlist Title") ?? ""))
                    {
                        var title = Get(playlist.First(), "Playlist Title") ?? "Playlist";
                        var key = $"playlist:{title}";
                        for (var n = 2; !keys.Add(key); n++)
                            key = $"playlist:{title} ({n})";
                        lists.Add(new ServiceListData(key, key["playlist:".Length..], ServiceListKind.Playlist,
                            playlist.Select(r => Song(r, "Track Link")).OfType<ServiceItem>().ToList()));
                    }
                }
            }
        }
        return known ? new ServiceExport(ImportFormat.Deezer, lists.OrderBy(l => l.Kind switch { ServiceListKind.Liked => 0, ServiceListKind.Albums => 1, _ => 2 }).ToList()) : null;

        static ServiceItem? Song(Dictionary<string, string> row, string link) => Get(row, "Song Title") is { } title
            ? new ServiceItem(new ImportSong(title, Get(row, "Artists") ?? Get(row, "Artist"), Get(row, "Album Title"), null, Get(row, "ISRC")), DeezerId(Get(row, link)))
            : null;
        static string? Get(Dictionary<string, string> row, string column) => row.TryGetValue(column, out var value) && value.Trim() is { Length: > 0 } text ? text : null;
        // "www.deezer.com/track/138547415" → "deezer:track:138547415".
        static string? DeezerId(string? link) =>
            link is not null && System.Text.RegularExpressions.Regex.Match(link, @"deezer\.com/(?:[a-z]{2}/)?(track|album|playlist)/(\d+)") is { Success: true } m
                ? $"{DeezerPrefix}{m.Groups[1].Value}:{m.Groups[2].Value}"
                : null;
    }

    // ---- Apple Music ----

    /// <summary>The prefix of a line's ID for an Apple Music song: "apple:song:290286598", or "apple:library:…" for one of your own.</summary>
    public const string ApplePrefix = "apple:";

    // The only files of Apple's copy that are read; the rest (purchases, payments, devices, listening and the like) are
    // left in the zip unopened.
    private static readonly string[] AppleFiles = ["Apple Music Library Tracks.json", "Apple Music Library Playlists.json", "Apple Music - Favorites.csv"];

    private static bool IsApple(string path) =>
        path.Contains("Apple Media Services", StringComparison.OrdinalIgnoreCase) || path.Contains("Apple_Media_Services", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Apple's copy of your data (privacy.apple.com → Request a copy of your data → Apple Media Services information): a
    /// zip, holding another, Apple_Media_Services.zip, whose "Apple Music Activity" folder has your library as zipped JSON.
    /// Only three files are read: "Apple Music Library Tracks.json" (each song in your library or a playlist: Title,
    /// Artist, Album, Track Duration in milliseconds, Playlist Only Track, Favorite Status - Track, its Track Identifier,
    /// and its Apple Music Track Identifier in the catalogue), "Apple Music Library Playlists.json" (each playlist's Title,
    /// Container Type and its songs' Track Identifiers) and "Apple Music - Favorites.csv" (Favorite Type, Item Reference,
    /// the catalogue's ID, Item Description, "Artist - Title", and Preference). Loved albums and artists are left out:
    /// Apple's copy names an album without its artist, and artists are follows, not music to import.
    /// </summary>
    private static ServiceExport? AppleMusic(IReadOnlyList<(string Name, byte[] Bytes)> files)
    {
        List<JsonElement>? tracks = null, containers = null;
        List<IReadOnlyList<string>>? favourites = null;
        foreach (var (path, bytes) in files)
        {
            var name = Path.GetFileName(path.Replace('\\', '/'));
            if (name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                var rows = ListParser.CsvRows(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')).Where(r => r.Any(c => c.Length > 0)).ToList();
                if (rows.Count > 0 && rows[0].Count > 3 && rows[0][0].Trim() == "Favorite Type" && rows[0][1].Trim() == "Item Reference")
                    favourites = rows;
                continue;
            }
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;
            JsonElement root;
            try
            {
                root = JsonDocument.Parse(bytes).RootElement;
            }
            catch (JsonException)
            {
                continue;
            }
            var rowsOf = Items(root).ToList();
            if (rowsOf.Any(r => r.TryGetProperty("Track Identifier", out _) && r.TryGetProperty("Title", out _)))
                tracks = rowsOf;
            else if (rowsOf.Any(r => r.TryGetProperty("Container Type", out _) && r.TryGetProperty("Playlist Item Identifiers", out _)))
                containers = rowsOf;
        }
        if (tracks is null && containers is null && favourites is null)
            return null;

        var songs = new Dictionary<string, (ServiceItem Item, bool InLibrary, bool Loved)>();
        foreach (var track in tracks ?? [])
        {
            if (Number(track, "Track Identifier") is not { } own || Text(track, "Content Type") is { } type && type != "Song")
                continue;
            var id = Number(track, "Apple Music Track Identifier") is { } catalogue ? $"{ApplePrefix}song:{catalogue}" : $"{ApplePrefix}library:{own}";
            var seconds = Number(track, "Track Duration") is { } ms && long.TryParse(ms, out var length) && length > 0 ? length / 1000.0 : (double?)null;
            if (Text(track, "Title") is not { } title)
                continue;
            var loved = track.TryGetProperty("Favorite Status - Track", out var favourite) && favourite.ValueKind == JsonValueKind.True
                        || Text(track, "Track Like Rating") == "liked";
            var inLibrary = !(track.TryGetProperty("Playlist Only Track", out var only) && only.ValueKind == JsonValueKind.True);
            songs[own] = (new ServiceItem(new ImportSong(title, Text(track, "Artist"), Text(track, "Album"), seconds), id), inLibrary, loved);
        }

        var lists = new List<ServiceListData>();
        // Loved songs: those marked so in the library, those in the Favourite Songs playlist, and those Favorites.csv names.
        var liked = songs.Values.Where(s => s.Loved).Select(s => s.Item).ToList();
        foreach (var container in containers ?? [])
            if (Text(container, "Container Type") == "Favorites Playlist")
                liked.AddRange(Ids(container).Select(i => songs.GetValueOrDefault(i).Item).OfType<ServiceItem>());
        if (favourites is not null)
        {
            var header = favourites[0].Select(c => c.Trim()).ToList();
            int Column(string column) => header.FindIndex(c => c.Equals(column, StringComparison.OrdinalIgnoreCase));
            var (kind, reference, description, preference) = (Column("Favorite Type"), Column("Item Reference"), Column("Item Description"), Column("Preference"));
            foreach (var row in favourites.Skip(1))
            {
                if (Cell(row, kind) != "Song" || Cell(row, preference) is { } said && said != "LIKE" || Cell(row, description) is not { } text)
                    continue;
                // "Artist - Title".
                var dash = text.IndexOf(" - ", StringComparison.Ordinal);
                var song = dash > 0 ? new ImportSong(text[(dash + 3)..].Trim(), text[..dash].Trim(), null, null) : new ImportSong(text, null, null, null);
                liked.Add(new ServiceItem(song, Cell(row, reference) is { } catalogue ? $"{ApplePrefix}song:{catalogue}" : null));
            }
        }
        liked = liked.DistinctBy(i => i.Id ?? i.Song.Title).ToList();
        if (liked.Count > 0)
            lists.Add(new ServiceListData("liked", "Favourite songs", ServiceListKind.Liked, liked));
        if (songs.Values.Where(s => s.InLibrary).Select(s => s.Item).ToList() is { Count: > 0 } library)
            lists.Add(new ServiceListData("library", "Library songs", ServiceListKind.Playlist, library));
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var container in containers ?? [])
        {
            if (Text(container, "Container Type") != "Playlist")
                continue;
            var title = Text(container, "Title") ?? "Playlist";
            var key = $"playlist:{title}";
            for (var n = 2; !keys.Add(key); n++)
                key = $"playlist:{title} ({n})";
            lists.Add(new ServiceListData(key, key["playlist:".Length..], ServiceListKind.Playlist,
                Ids(container).Select(i => songs.GetValueOrDefault(i).Item).OfType<ServiceItem>().ToList()));
        }
        return new ServiceExport(ImportFormat.AppleMusic, lists);

        static IEnumerable<string> Ids(JsonElement container) =>
            container.TryGetProperty("Playlist Item Identifiers", out var ids) && ids.ValueKind == JsonValueKind.Array
                ? ids.EnumerateArray().Select(i => i.ValueKind == JsonValueKind.Number ? i.GetRawText() : i.GetString() ?? "")
                : [];
        static string? Number(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetRawText()
            : value.ValueKind == JsonValueKind.String && value.GetString()?.Trim() is { Length: > 0 } text ? text : null;
        static string? Cell(IReadOnlyList<string> row, int index) => index >= 0 && index < row.Count && row[index].Trim() is { Length: > 0 } cell ? cell : null;
    }

    // A playlist's item: a track, or a file of the listener's own ("spotify:local:Artist:Album:Title:Seconds"). Episodes
    // and audiobooks are left out.
    private static ServiceItem? PlaylistItem(JsonElement item)
    {
        if (item.TryGetProperty("track", out var track) && track.ValueKind == JsonValueKind.Object)
            return Item(Text(track, "trackName"), Text(track, "artistName"), Text(track, "albumName"), Text(track, "trackUri"));
        if (item.TryGetProperty("localTrack", out var local) && local.ValueKind == JsonValueKind.Object && Text(local, "uri") is { } uri)
        {
            var parts = uri.Split(':');
            if (parts.Length >= 6 && parts[1] == "local")
            {
                static string? Part(string text) => Uri.UnescapeDataString(text.Replace('+', ' ')) is { Length: > 0 } part ? part : null;
                var seconds = double.TryParse(parts[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0 ? s : (double?)null;
                return Part(parts[4]) is { } title ? new ServiceItem(new ImportSong(title, Part(parts[2]), Part(parts[3]), seconds), uri) : null;
            }
        }
        return null;
    }

    private static ServiceItem? Item(string? title, string? artist, string? album, string? id) =>
        title is { Length: > 0 } ? new ServiceItem(new ImportSong(title, artist, album, null), id) : null;

    private static IEnumerable<JsonElement> Items(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object) : [];

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()?.Trim() is { Length: > 0 } text ? text : null;
}

/// <summary>
/// Reads sheets of an .xlsx spreadsheet as rows of named cells, the first row being the names. Only the sheets asked
/// for are opened.
/// </summary>
internal static class Spreadsheet
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>The sheets whose names <paramref name="wanted"/> accepts, by name; none if it isn't a spreadsheet.</summary>
    public static Dictionary<string, List<Dictionary<string, string>>> Read(byte[] bytes, Func<string, bool> wanted)
    {
        var sheets = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.Ordinal);
        if (bytes.Length < 4 || bytes[0] != 'P' || bytes[1] != 'K')
            return sheets;
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (Load(zip, "xl/workbook.xml") is not { } workbook || Load(zip, "xl/_rels/workbook.xml.rels") is not { } rels)
            return sheets;
        var targets = rels.Root!.Elements(PackageRelationships + "Relationship")
            .ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!);
        List<string>? shared = null;
        foreach (var sheet in workbook.Root!.Element(Main + "sheets")?.Elements(Main + "sheet") ?? [])
        {
            var name = (string?)sheet.Attribute("name") ?? "";
            if (!wanted(name) || (string?)sheet.Attribute(Relationships + "id") is not { } id || !targets.TryGetValue(id, out var target))
                continue;
            var path = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            if (Load(zip, path) is not { } xml)
                continue;
            shared ??= Load(zip, "xl/sharedStrings.xml")?.Root!.Elements(Main + "si").Select(si => string.Concat(si.Descendants(Main + "t").Select(t => t.Value))).ToList() ?? [];
            var rows = xml.Descendants(Main + "row")
                .Select(row => row.Elements(Main + "c").GroupBy(c => Column((string?)c.Attribute("r"))).ToDictionary(g => g.Key, g => Value(g.First(), shared)))
                .ToList();
            if (rows.Count == 0)
                continue;
            var header = rows[0];
            // A name used twice is the first column of it.
            var columns = header.Where(h => h.Value.Trim().Length > 0).GroupBy(h => h.Value.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.Key, Column: g.First().Key)).ToList();
            sheets[name] = rows.Skip(1).Select(row => columns.ToDictionary(c => c.Name, c => row.GetValueOrDefault(c.Column, ""), StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
        return sheets;
    }

    private static XDocument? Load(ZipArchive zip, string path)
    {
        if (zip.GetEntry(path) is not { } entry)
            return null;
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    // "C12" → "C".
    private static string Column(string? reference) => new((reference ?? "").TakeWhile(char.IsLetter).ToArray());

    private static string Value(XElement cell, List<string> shared) => (string?)cell.Attribute("t") switch
    {
        "s" => int.TryParse(cell.Element(Main + "v")?.Value, out var index) && index >= 0 && index < shared.Count ? shared[index] : "",
        "inlineStr" => string.Concat(cell.Descendants(Main + "t").Select(t => t.Value)),
        _ => cell.Element(Main + "v")?.Value ?? "",
    };
}
