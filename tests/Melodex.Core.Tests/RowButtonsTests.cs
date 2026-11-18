using System.Text.RegularExpressions;
using Melodex.Core.Tests.Support;

namespace Melodex.Core.Tests;

/// <summary>
/// A row's buttons line up with the rows around it: Razor keeps the line break between a button and the song menu after
/// it as a space, which pushes one row's buttons a few pixels from the next's, so none is left there.
/// </summary>
public sealed partial class RowButtonsTests
{
    [GeneratedRegex(@"(/>|</WishButton>)\r?\n\s*<ItemMenu")]
    private static partial Regex SpaceBeforeMenu();

    [Fact]
    public void No_button_is_spaced_from_the_song_menu_after_it()
    {
        var web = Path.Combine(TestLibrary.RepositoryRoot, "src", "Melodex.Web");
        var spaced = Directory.EnumerateFiles(web, "*.razor", SearchOption.AllDirectories)
            .Where(file => SpaceBeforeMenu().IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(web, file))
            .ToList();
        Assert.Empty(spaced);
    }
}
