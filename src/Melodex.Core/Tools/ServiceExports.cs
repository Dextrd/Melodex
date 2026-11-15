using System.IO.Compression;
using System.Text.Json;
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
            if (bytes.Length > 4 && bytes[0] == 'P' && bytes[1] == 'K')
                unpacked.AddRange(Unzip(bytes));
            else
                unpacked.Add((name, bytes));
        }
        return Spotify(unpacked) ?? YouTubeMusic(unpacked);
    }

    private static IEnumerable<(string Name, byte[] Bytes)> Unzip(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            if (entry.Length is 0 or > MaxEntry || !(entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || entry.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
                continue;
            using var stream = entry.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            yield return (entry.FullName, copy.ToArray());
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
