using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Melodex.Client;

namespace Melodex.Core.Tools;

/// <summary>
/// Reads the songs out of a list to import: an M3U or M3U8 playlist, a CSV (with its columns chosen, or guessed from its
/// first row), or lines of "Artist – Title".
/// </summary>
public static partial class ListParser
{
    /// <summary>Songs a list can hold at most.</summary>
    public const int MaxSongs = 20_000;

    private const int SampleSize = 5;

    /// <summary>What kind of list it is: by the file's name, or else by what's in it.</summary>
    public static ImportFormat Detect(string text, string? fileName)
    {
        switch (Path.GetExtension(fileName ?? "").ToLowerInvariant())
        {
            case ".m3u" or ".m3u8":
                return ImportFormat.M3u;
            case ".csv" or ".tsv":
                return ImportFormat.Csv;
            case ".txt":
                return ImportFormat.Text;
        }
        var lines = Lines(text).Take(10).ToList();
        if (lines.Count == 0)
            return ImportFormat.Text;
        if (lines[0].StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) || lines.Any(l => l.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase)))
            return ImportFormat.M3u;
        // A CSV has the same number of cells, more than one, on every line.
        var delimiter = Delimiter(lines[0]);
        if (delimiter != '\0')
        {
            var cells = SplitRow(lines[0], delimiter).Count;
            if (cells > 1 && lines.All(l => SplitRow(l, delimiter).Count == cells))
                return ImportFormat.Csv;
        }
        return ImportFormat.Text;
    }

    /// <summary>What the list looks like, with its columns guessed when it's a CSV.</summary>
    public static ImportPeek Peek(string text, string? fileName)
    {
        var format = Detect(text, fileName);
        if (format != ImportFormat.Csv)
        {
            var songs = Read(text, format, null, null);
            return new ImportPeek(format, songs.Count, [], [], false, songs.Take(SampleSize).ToList(), []);
        }
        var rows = CsvRows(text).ToList();
        var width = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        var header = rows.Count > 0 ? rows[0] : [];
        var byName = header.Select(GuessByName).ToList();
        var hasHeader = byName.Any(c => c != ImportColumn.Ignore) && !header.Any(LooksLikeValue);
        var columns = hasHeader
            ? Enumerable.Range(0, width).Select(i => i < header.Count && header[i].Trim().Length > 0 ? header[i].Trim() : $"Column {i + 1}").ToList()
            : Enumerable.Range(1, width).Select(i => $"Column {i}").ToList();
        var body = rows.Skip(hasHeader ? 1 : 0).ToList();
        var guess = hasHeader ? Pad(byName, width) : GuessByValues(body, width);
        var read = FromRows(body, guess, hasHeader ? header : null);
        return new ImportPeek(format, read.Count, columns, guess, hasHeader, read.Take(SampleSize).ToList(),
            body.Take(SampleSize).Select(r => (IReadOnlyList<string>)Pad(r.ToList(), width, "")).ToList());
    }

    /// <summary>The list's songs, in order. A CSV's columns and header are guessed when not given.</summary>
    public static IReadOnlyList<ImportSong> Read(string text, ImportFormat format, IReadOnlyList<ImportColumn>? columns, bool? hasHeader)
    {
        var songs = format switch
        {
            ImportFormat.M3u => ReadM3u(text),
            ImportFormat.Csv => ReadCsv(text, columns, hasHeader),
            _ => ReadText(text),
        };
        return songs.Where(s => s.Title.Length > 0).Take(MaxSongs).ToList();
    }

    // ---- M3U ----

    [GeneratedRegex(@"^#EXTINF:\s*(-?[\d.]+)?[^,]*,(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex ExtInf();

    private static IEnumerable<ImportSong> ReadM3u(string text)
    {
        double? length = null;
        string? named = null, artist = null, album = null;
        foreach (var line in Lines(text))
        {
            if (line.StartsWith('#'))
            {
                if (ExtInf().Match(line) is { Success: true } info)
                {
                    // One with no path before the next: a song by name only, as an export of songs not had writes it.
                    if (named is { Length: > 0 })
                    {
                        if (Named(named, artist, album, length) is { } nameOnly)
                            yield return nameOnly;
                        artist = album = null;
                    }
                    length = double.TryParse(info.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : null;
                    named = info.Groups[2].Value.Trim();
                }
                else if (line.StartsWith("#EXTART:", StringComparison.OrdinalIgnoreCase))
                    artist = line[8..].Trim();
                else if (line.StartsWith("#EXTALB:", StringComparison.OrdinalIgnoreCase))
                    album = line[8..].Trim();
                continue;
            }

            // A path or URL ends each song; what came before it says what it is.
            var fromPath = FromPath(line);
            ImportSong song;
            if (named is { Length: > 0 })
            {
                var (a, t) = SplitArtist(named);
                song = new ImportSong(t, artist ?? a ?? fromPath.Artist, album ?? fromPath.Album, length, Path: line);
            }
            else
                song = fromPath with { Artist = artist ?? fromPath.Artist, Album = album ?? fromPath.Album, DurationSeconds = length };
            yield return song;
            length = null;
            named = artist = album = null;
        }
        if (named is { Length: > 0 } && Named(named, artist, album, length) is { } last)
            yield return last;
    }

    private static ImportSong? Named(string named, string? artist, string? album, double? length)
    {
        var (a, t) = SplitArtist(named);
        return t.Length == 0 ? null : new ImportSong(t, artist ?? a, album, length);
    }

    [GeneratedRegex(@"^\s*(?:\d{1,3}[-.]?)?\d{1,3}\s*(?:[-.)_]\s*|\s+)")]
    private static partial Regex TrackNumber();

    /// <summary>
    /// What a path says: its file's name, less the track number, as the title (or "Artist - Title"), and the folders
    /// above as the album and artist, as music is usually filed.
    /// </summary>
    private static ImportSong FromPath(string path)
    {
        var url = Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var name = parts.Length == 0 ? path : Uri.UnescapeDataString(System.IO.Path.GetFileNameWithoutExtension(parts[^1]));
        name = TrackNumber().Replace(name, "");
        var (artist, title) = SplitArtist(name);
        if (url)
            return new ImportSong(title, artist, null, null, Path: path);
        return new ImportSong(title,
            artist ?? (parts.Length >= 3 ? parts[^3] : null),
            parts.Length >= 3 ? parts[^2] : null,
            null, Path: path);
    }

    // ---- Text ----

    [GeneratedRegex(@"^\s*\d{1,5}\s*[.)]\s+")]
    private static partial Regex Numbered();

    private static IEnumerable<ImportSong> ReadText(string text)
    {
        foreach (var line in Lines(text))
        {
            if (line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
                continue;
            var (artist, title) = SplitArtist(Numbered().Replace(line, ""));
            yield return new ImportSong(title, artist, null, null);
        }
    }

    private static readonly string[] ArtistSeparators = [" – ", " — ", " - ", "\t", " -- ", " | "];

    /// <summary>"Artist – Title" as its artist and title; the whole of it as the title when there's no dash.</summary>
    public static (string? Artist, string Title) SplitArtist(string text)
    {
        text = text.Trim();
        foreach (var separator in ArtistSeparators)
        {
            var at = text.IndexOf(separator, StringComparison.Ordinal);
            if (at > 0 && at + separator.Length < text.Length)
                return (text[..at].Trim(), text[(at + separator.Length)..].Trim());
        }
        return (null, text);
    }

    // ---- CSV ----

    private static IEnumerable<ImportSong> ReadCsv(string text, IReadOnlyList<ImportColumn>? columns, bool? hasHeader)
    {
        var rows = CsvRows(text).ToList();
        if (rows.Count == 0)
            return [];
        var width = rows.Max(r => r.Count);
        var header = rows[0];
        var header0 = hasHeader ?? (header.Any(h => GuessByName(h) != ImportColumn.Ignore) && !header.Any(LooksLikeValue));
        var body = rows.Skip(header0 ? 1 : 0).ToList();
        var chosen = columns is { Count: > 0 } ? Pad(columns.ToList(), width) : header0 ? Pad(header.Select(GuessByName).ToList(), width) : GuessByValues(body, width);
        return FromRows(body, chosen, header0 ? header : null);
    }

    private static List<ImportSong> FromRows(IReadOnlyList<IReadOnlyList<string>> rows, IReadOnlyList<ImportColumn> columns, IReadOnlyList<string>? header)
    {
        // A length column named in milliseconds, or holding numbers too big to be seconds, is milliseconds.
        var lengthAt = Index(columns, ImportColumn.Length);
        var millis = lengthAt >= 0 && (header is not null && lengthAt < header.Count && Regex.IsMatch(header[lengthAt], @"\bms\b|millis", RegexOptions.IgnoreCase)
                                       || rows.Take(20).Select(r => Cell(r, lengthAt)).Any(c => double.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 36_000));
        var songs = new List<ImportSong>(rows.Count);
        foreach (var row in rows)
        {
            string? Get(ImportColumn column) => Index(columns, column) is var i and >= 0 && Cell(row, i) is { Length: > 0 } value ? value : null;
            var title = Get(ImportColumn.Title);
            var artist = Get(ImportColumn.Artist);
            // A row with the artist and title in one cell, as "Artist - Title".
            if (title is not null && artist is null && Index(columns, ImportColumn.Artist) < 0)
                (artist, title) = SplitArtist(title);
            if (title is null)
                continue;
            songs.Add(new ImportSong(title, artist, Get(ImportColumn.Album), Length(Get(ImportColumn.Length), millis),
                Get(ImportColumn.Isrc)?.Replace("-", "").ToUpperInvariant(), Get(ImportColumn.MusicBrainzId)?.ToLowerInvariant()));
        }
        return songs;
    }

    private static int Index(IReadOnlyList<ImportColumn> columns, ImportColumn column)
    {
        for (var i = 0; i < columns.Count; i++)
            if (columns[i] == column)
                return i;
        return -1;
    }

    private static string Cell(IReadOnlyList<string> row, int index) => index >= 0 && index < row.Count ? row[index].Trim() : "";

    /// <summary>A length as minutes and seconds (3:45 or 1:02:03), seconds, or milliseconds.</summary>
    public static double? Length(string? text, bool millis = false)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim();
        if (text.Contains(':'))
        {
            double total = 0;
            foreach (var part in text.Split(':'))
            {
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    return null;
                total = total * 60 + n;
            }
            return total > 0 ? total : null;
        }
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value <= 0)
            return null;
        return millis ? value / 1000 : value;
    }

    [GeneratedRegex(@"^[A-Z]{2}-?[A-Z0-9]{3}-?\d{2}-?\d{5}$", RegexOptions.IgnoreCase)]
    private static partial Regex IsrcPattern();

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase)]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"^\d{1,2}:\d{2}(?::\d{2})?$")]
    private static partial Regex ClockPattern();

    // A cell that's a value rather than a column's name: a length, an ISRC or an ID.
    private static bool LooksLikeValue(string cell)
    {
        cell = cell.Trim();
        return ClockPattern().IsMatch(cell) || IsrcPattern().IsMatch(cell) || GuidPattern().IsMatch(cell);
    }

    /// <summary>What a column holds, by its name: "Artist Name(s)", "Track Name", "Duration (ms)" and the like.</summary>
    public static ImportColumn GuessByName(string name)
    {
        var key = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]", "");
        if (key.Length == 0)
            return ImportColumn.Ignore;
        if (key.Contains("isrc"))
            return ImportColumn.Isrc;
        if (key.Contains("mbid") || key.Contains("musicbrainz") || key == "recordingid")
            return ImportColumn.MusicBrainzId;
        if (key.Contains("albumartist") || key.Contains("tracknumber") || key.Contains("trackno") || key is "track" or "tracknum" or "no" or "number"
            || key.Contains("uri") || key.Contains("url") || key.Contains("id") && !key.Contains("video"))
            return key is "track" ? ImportColumn.Title : ImportColumn.Ignore;
        if (key.Contains("artist") || key is "by" or "performer" or "band" or "creator")
            return ImportColumn.Artist;
        if (key.Contains("album") || key == "release")
            return ImportColumn.Album;
        if (key.Contains("duration") || key.Contains("length") || key == "time")
            return ImportColumn.Length;
        if (key.Contains("title") || key.Contains("name") || key.Contains("song") || key.Contains("track"))
            return ImportColumn.Title;
        return ImportColumn.Ignore;
    }

    // Without a header, by what's in each column: IDs and lengths by their shape, and the first two text columns as the
    // artist and the title.
    private static List<ImportColumn> GuessByValues(IReadOnlyList<IReadOnlyList<string>> rows, int width)
    {
        var guess = Enumerable.Repeat(ImportColumn.Ignore, width).ToList();
        var sample = rows.Take(20).ToList();
        var text = new List<int>();
        for (var i = 0; i < width; i++)
        {
            var cells = sample.Select(r => Cell(r, i)).Where(c => c.Length > 0).ToList();
            if (cells.Count == 0)
                continue;
            if (cells.All(c => IsrcPattern().IsMatch(c)))
                guess[i] = ImportColumn.Isrc;
            else if (cells.All(c => GuidPattern().IsMatch(c)))
                guess[i] = ImportColumn.MusicBrainzId;
            else if (cells.All(c => ClockPattern().IsMatch(c)))
                guess[i] = ImportColumn.Length;
            else if (!cells.All(c => double.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                text.Add(i);
        }
        if (text.Count >= 2)
        {
            guess[text[0]] = ImportColumn.Artist;
            guess[text[1]] = ImportColumn.Title;
            if (text.Count >= 3)
                guess[text[2]] = ImportColumn.Album;
        }
        else if (text.Count == 1)
            guess[text[0]] = ImportColumn.Title;
        return guess;
    }

    private static List<T> Pad<T>(List<T> list, int width, T fill = default!)
    {
        while (list.Count < width)
            list.Add(fill);
        return list.Count > width ? list[..width] : list;
    }

    /// <summary>The cell separator a CSV's first line uses: a comma, semicolon or tab, whichever it has most of.</summary>
    private static char Delimiter(string line)
    {
        var best = '\0';
        var most = 0;
        foreach (var c in new[] { ',', ';', '\t' })
        {
            var count = SplitRow(line, c).Count - 1;
            if (count > most)
                (best, most) = (c, count);
        }
        return best;
    }

    internal static IEnumerable<IReadOnlyList<string>> CsvRows(string text)
    {
        var first = Lines(text).FirstOrDefault();
        if (first is null)
            yield break;
        var delimiter = Delimiter(first);
        if (delimiter == '\0')
            delimiter = ',';
        // Quoted cells can hold line breaks, so the text is read as a whole rather than line by line.
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var start = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                    quoted = false;
                else
                    cell.Append(c);
            }
            else if (c == '"' && cell.Length == 0)
                quoted = true;
            else if (c == delimiter)
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                row.Add(cell.ToString());
                cell.Clear();
                if (row.Any(x => x.Trim().Length > 0))
                    yield return row;
                row = [];
            }
            else
                cell.Append(c);
        }
        row.Add(cell.ToString());
        if (row.Any(x => x.Trim().Length > 0))
            yield return row;
    }

    // One line's cells, for telling what separates them; quotes are honoured but line breaks in them aren't.
    private static List<string> SplitRow(string line, char delimiter)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"')
                quoted = !quoted;
            else if (c == delimiter && !quoted)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
                cell.Append(c);
        }
        cells.Add(cell.ToString());
        return cells;
    }

    private static IEnumerable<string> Lines(string text) =>
        text.TrimStart('﻿').Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0);
}
