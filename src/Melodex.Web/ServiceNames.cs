using Melodex.Client;

namespace Melodex.Web;

/// <summary>The music services whose exports can be imported, by name.</summary>
public static class ServiceNames
{
    public static string Of(ImportFormat format) => format switch
    {
        ImportFormat.Spotify => "Spotify",
        ImportFormat.YouTubeMusic => "YouTube Music",
        ImportFormat.Deezer => "Deezer",
        ImportFormat.M3u => "M3U",
        ImportFormat.Csv => "CSV",
        _ => "pasted",
    };
}
