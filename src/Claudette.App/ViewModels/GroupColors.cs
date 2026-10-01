using Avalonia.Media;
using Claudette.Core.Settings;

namespace Claudette.App.ViewModels;

/// <summary>
/// The colors of tab groups (DESIGN.md §4, "Grouped by folder"): the color a new group takes, and the color remembered
/// for each folder in the app state, matched as groups match folders. Nothing here saves the state.
/// </summary>
internal static class GroupColors
{
    /// <summary>
    /// The color for a group opening on <paramref name="folder"/>: the one saved for it, else <see cref="Next"/>. It's
    /// remembered for the folder, so a color saved under another spelling of the path, or by an older Claudette, is
    /// saved once now, as the group's folder is written.
    /// </summary>
    /// <param name="open">The colors of the groups already open, in order.</param>
    public static Color ForNewGroup(AppState state, string folder, IReadOnlyList<Color> open)
    {
        var color = Saved(state, folder) ?? Next(open);
        Remember(state, folder, color);
        return color;
    }

    /// <summary>The color saved for a folder, matched as groups match folders. One saved as a group color's index is read too.</summary>
    public static Color? Saved(AppState state, string folder)
    {
        foreach (var (path, hex) in state.FolderColors)
        {
            if (FolderHistory.SamePath(path, folder) && TabGroupViewModel.TryParseHex(hex, out var color))
            {
                return color;
            }
        }
        var palette = TabGroupViewModel.Palette;
        foreach (var (path, index) in state.GroupColors)
        {
            if (FolderHistory.SamePath(path, folder))
            {
                return palette[(index % palette.Count + palette.Count) % palette.Count].Color;
            }
        }
        return null;
    }

    /// <summary>A new group's color: the first group color no open group has, else the next in turn.</summary>
    /// <param name="open">The colors of the groups already open, in order.</param>
    public static Color Next(IReadOnlyList<Color> open)
    {
        var palette = TabGroupViewModel.Palette;
        var used = open.ToHashSet();
        return palette.Select(p => p.Color).FirstOrDefault(c => !used.Contains(c), palette[open.Count % palette.Count].Color);
    }

    /// <summary>Saves <paramref name="color"/> for <paramref name="folder"/>, in place of any it had under another spelling or as an index.</summary>
    public static void Remember(AppState state, string folder, Color color)
    {
        foreach (var path in state.FolderColors.Keys.Where(p => FolderHistory.SamePath(p, folder)).ToList())
        {
            state.FolderColors.Remove(path);
        }
        foreach (var path in state.GroupColors.Keys.Where(p => FolderHistory.SamePath(p, folder)).ToList())
        {
            state.GroupColors.Remove(path);
        }
        state.FolderColors[folder] = TabGroupViewModel.ToHex(color);
    }
}
