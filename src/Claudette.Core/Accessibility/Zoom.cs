namespace Claudette.Core.Accessibility;

/// <summary>
/// Settings → Appearance → Zoom and Ctrl/Cmd +, − and 0 (DESIGN.md §3, "Accessibility"): the main window's content,
/// scaled in steps, as a percentage.
/// </summary>
public static class Zoom
{
    public const int Default = 100;

    /// <summary>The steps, smallest first.</summary>
    public static IReadOnlyList<int> Steps { get; } = [80, 90, 100, 110, 125, 150, 175, 200];

    /// <summary>The next step up, or the largest.</summary>
    public static int In(int current) => Steps.FirstOrDefault(step => step > current, Steps[^1]);

    /// <summary>The next step down, or the smallest.</summary>
    public static int Out(int current) => Steps.LastOrDefault(step => step < current, Steps[0]);

    /// <summary>A stored value, within the steps' range; one edited by hand needn't be a step.</summary>
    public static int Clamp(int percent) => Math.Clamp(percent, Steps[0], Steps[^1]);
}
