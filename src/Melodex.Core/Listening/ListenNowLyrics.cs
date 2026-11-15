using Microsoft.EntityFrameworkCore;
using Melodex.Core.Data;
using Melodex.Core.Library;

namespace Melodex.Core.Listening;

/// <summary>
/// Lyrics for songs fetched to listen to, looked for the way a library song's are, once the song is ready to play and
/// without holding it up: they go in an .lrc file beside the song in the listen-now store, which goes when the song does,
/// and with it into the library when it's added. Which songs are being looked for is kept here, so the player can show
/// they're on their way.
/// </summary>
public sealed class ListenNowLyrics(IServiceScopeFactory scopes, ILogger<ListenNowLyrics> logger)
{
    private readonly HashSet<Guid> _looking = [];

    /// <summary>Whether lyrics are being looked for for the listen-now item.</summary>
    public bool IsLooking(Guid item)
    {
        lock (_looking)
            return _looking.Contains(item);
    }

    /// <summary>Starts looking for the item's lyrics, in the background.</summary>
    public void Start(Guid item)
    {
        lock (_looking)
            if (!_looking.Add(item))
                return;
        _ = Task.Run(async () =>
        {
            try
            {
                await LookAsync(item, CancellationToken.None);
            }
            catch (Exception e)
            {
                logger.LogWarning("Couldn't look for the lyrics of a song fetched to listen to: {Reason}", e.Message);
            }
            finally
            {
                lock (_looking)
                    _looking.Remove(item);
            }
        });
    }

    private async Task LookAsync(Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MelodexDbContext>();
        var listen = scope.ServiceProvider.GetRequiredService<ListenNowService>();
        if (await db.ListenNowItems.Include(i => i.Track).FirstOrDefaultAsync(i => i.Id == id, ct) is not { } item)
            return;
        var album = item.EditionId is { } edition ? await db.Editions.Where(e => e.Id == edition).Select(e => e.Album.Title).FirstOrDefaultAsync(ct) : null;
        var found = await scope.ServiceProvider.GetRequiredService<LyricsFinder>().LookUpAsync(item.Track, album, item.Duration, ct);
        if (found is not { Lyrics: { Instrumental: false } lyrics } || (lyrics.Synced ?? lyrics.Plain) is not { } text)
            return;
        // The song may have gone meanwhile; its lyrics aren't kept without it.
        await db.Entry(item).ReloadAsync(ct);
        if (listen.LyricsPath(item) is { } path && File.Exists(listen.FullPath(item)))
            await File.WriteAllTextAsync(path, text, ct);
    }
}
