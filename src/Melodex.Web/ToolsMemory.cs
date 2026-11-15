namespace Melodex.Web;

/// <summary>
/// What was chosen on each Tools page and not yet sent (a list pasted, an export's playlists, which of an import's
/// lists are open), kept for the session, so leaving a Tools page and coming back finds it as it was left.
/// </summary>
public sealed class ToolsMemory
{
    private readonly Dictionary<string, object> _pages = [];

    /// <summary>Keeps the page's state, by its address.</summary>
    public void Keep(string page, object state) => _pages[page] = state;

    /// <summary>The page's state as it was left, if it's been kept this session.</summary>
    public T? Recall<T>(string page) where T : class => _pages.GetValueOrDefault(page) as T;

    public void Forget(string page) => _pages.Remove(page);
}
