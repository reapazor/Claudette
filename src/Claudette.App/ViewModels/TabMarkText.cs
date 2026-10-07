using Claudette.Core.Settings;

namespace Claudette.App.ViewModels;

/// <summary>
/// A mark as the UI writes it (DESIGN.md §4, "Marks"): its name in the tab's menu, and the tip on the tab's row. Both
/// only say what the icon is, since what it means is the user's.
/// </summary>
public static class TabMarkText
{
    public static string Name(TabMark mark) => mark switch
    {
        TabMark.Check => "Check",
        TabMark.Cross => "Cross",
        TabMark.Question => "Question mark",
        TabMark.Star => "Star",
        TabMark.Flag => "Flag",
        TabMark.Pause => "Pause",
        _ => mark.ToString(),
    };

    public static string Tip(TabMark mark) => mark switch
    {
        TabMark.Check => "Marked with a check",
        TabMark.Cross => "Marked with a cross",
        TabMark.Question => "Marked with a question mark",
        TabMark.Star => "Marked with a star",
        TabMark.Flag => "Marked with a flag",
        TabMark.Pause => "Marked with a pause sign",
        _ => "Marked",
    };
}
