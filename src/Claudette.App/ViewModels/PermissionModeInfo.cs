namespace Claudette.App.ViewModels;

/// <summary>A permission mode in the tab's mode picker (DESIGN.md §7).</summary>
public sealed record PermissionModeChoice(string Value, string Label, string Description)
{
    public bool IsBypass => Value == PermissionModeInfo.Bypass;
}

/// <summary>Claude Code's permission modes, with the names Claudette shows for them.</summary>
public static class PermissionModeInfo
{
    public const string Bypass = "bypassPermissions";

    public static IReadOnlyList<PermissionModeChoice> Choices { get; } =
    [
        new("default", "Default", "Ask before edits and commands"),
        new("acceptEdits", "Accept edits", "Edit files without asking; still ask before commands"),
        new("plan", "Plan", "Look around and plan; don't change anything"),
        new(Bypass, "Bypass permissions", "Never ask. Claude Code's own deny rules still apply"),
    ];

    /// <summary>"Accept edits" for <c>acceptEdits</c>; modes Claudette doesn't know (such as <c>auto</c>) keep their name.</summary>
    public static string Label(string? mode) => mode switch
    {
        null => "Default",
        "auto" => "Auto",
        "dontAsk" => "Don't ask",
        _ => Choices.FirstOrDefault(c => c.Value == mode)?.Label ?? mode,
    };
}
