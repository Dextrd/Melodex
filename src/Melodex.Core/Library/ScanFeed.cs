using Melodex.Client;

namespace Melodex.Core.Library;

/// <summary>
/// The files the current scan has read or matched, so the Activity page can show a scan as it goes: the file name
/// while it's read, then the song once it's matched. Each file keeps the place it first took, and every change gets a
/// sequence number, so a page can ask for only what changed since it last looked. Once a scan's done its files go to the
/// <see cref="Tasks.TaskArchive"/> too, so earlier scans keep their lists.
/// </summary>
public sealed class ScanFeed(Tasks.TaskArchive archive)
{
    private const string What = "scan";

    private readonly Lock _lock = new();
    private readonly Dictionary<string, ScanFile> _files = new(StringComparer.Ordinal);
    private long _seq;
    private long _order;

    /// <summary>The scan task the files belong to; it changes when a new scan clears the list.</summary>
    public Guid Scan { get; private set; } = Guid.NewGuid();

    /// <summary>A new scan starts with an empty list; one carrying on after a pause keeps its own.</summary>
    public void Clear(Guid scan)
    {
        lock (_lock)
        {
            if (scan == Scan)
                return;
            _files.Clear();
            _order = 0;
            Scan = scan;
        }
    }

    /// <summary>The scan is done: its files go to the archive, as well as staying here until the next.</summary>
    public void Finish(Guid scan)
    {
        List<ScanFile> files;
        lock (_lock)
        {
            if (scan != Scan)
                return;
            files = _files.Values.OrderBy(f => f.Order).ToList();
        }
        archive.Save(scan, What, files);
    }

    public void Set(string path, ScanFileState state, string? title = null, string? artist = null, string? detail = null) =>
        Put(new ScanFile(0, 0, path, state, title, artist, null, null, null, null, detail, DateTime.UtcNow));

    public void Matched(string path, string title, string artist, Guid? artistId, string? albumTitle, Guid? albumId, string? coverUrl) =>
        Put(new ScanFile(0, 0, path, ScanFileState.Matched, title, artist, artistId, albumTitle, albumId, coverUrl, null, DateTime.UtcNow));

    private void Put(ScanFile file)
    {
        lock (_lock)
        {
            var order = _files.TryGetValue(file.Path, out var old) ? old.Order : ++_order;
            _files[file.Path] = file with { Seq = ++_seq, Order = order };
        }
    }

    /// <summary>The files changed after <paramref name="since"/> (all of them for 0), in the order they were first seen.</summary>
    public ScanFeedPage Since(long since)
    {
        lock (_lock)
            return new ScanFeedPage(Scan, _seq, _files.Values.Where(f => f.Seq > since).OrderBy(f => f.Order).ToList());
    }

    /// <summary>The files of one scan, the current one or an earlier one; null when an earlier one's weren't kept.</summary>
    public ScanFeedPage? Since(Guid scan, long since)
    {
        lock (_lock)
        {
            if (scan == Scan)
                return Since(since);
        }
        return archive.Load<ScanFile>(scan, What) is { } kept
            ? new ScanFeedPage(scan, kept.Count == 0 ? 0 : kept.Max(f => f.Seq), kept.Where(f => f.Seq > since).ToList())
            : null;
    }
}
