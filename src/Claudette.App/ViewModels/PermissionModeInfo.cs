using Claudette.Core.Claude;

namespace Claudette.App.ViewModels;

/// <summary>A permission mode in the tab's mode picker (DESIGN.md §7).</summary>
public sealed record PermissionModeChoice(string Value, string Label, string Description)
{
    public bool IsBypass => Value == PermissionModeInfo.Bypass;

    public bool IsAuto => Value == PermissionModeInfo.Auto;
}

/// <summary>Claude Code's permission modes, with the names Claudette shows for them.</summary>
public static class PermissionModeInfo
{
    /// <summary>Manual mode, whose config value is <c>default</c>.</summary>
    public const string Manual = StartingPermissionMode.Manual;

    public const string Auto = StartingPermissionMode.Auto;

    public const string Bypass = "bypassPermissions";

    public static IReadOnlyList<PermissionModeChoice> Choices { get; } =
    [
        new(Manual, "Manual", "Ask before edits and commands"),
        new("acceptEdits", "Accept edits", "Edit files without asking; still ask before commands"),
        new("plan", "Plan", "Look around and plan; don't change anything"),
        new(Auto, "Auto", "Don't ask; a safety check reviews actions and blocks risky ones"),
        new(Bypass, "Bypass permissions", "Never ask. Claude Code's own deny rules still apply"),
    ];

    /// <summary>"Accept edits" for <c>acceptEdits</c>; modes Claudette doesn't offer (such as <c>dontAsk</c>) keep their name.</summary>
    public static string Label(string? mode) => mode switch
    {
        null or "manual" => "Manual",
        "dontAsk" => "Don't ask",
        _ => Choices.FirstOrDefault(c => c.Value == mode)?.Label ?? mode,
    };
}
