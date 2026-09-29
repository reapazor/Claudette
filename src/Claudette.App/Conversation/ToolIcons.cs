using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Claudette.App.Conversation;

/// <summary>
/// The icon on each tool card (DESIGN.md §5): the key of one of the small vector icons in App.axaml, drawn the same
/// on every platform.
/// </summary>
public static class ToolIcons
{
    public const string Default = "IconToolDefault";

    public static string KeyFor(string toolName) => toolName switch
    {
        "Read" => "IconToolRead",
        "Edit" or "MultiEdit" or "NotebookEdit" => "IconToolEdit",
        "Write" => "IconToolWrite",
        "Bash" or "BashOutput" or "KillShell" or "KillBash" or "PowerShell" => "IconToolBash",
        "Grep" => "IconToolSearch",
        "Glob" or "LS" => "IconToolFolder",
        "WebFetch" => "IconToolWeb",
        "WebSearch" => "IconToolWebSearch",
        "Agent" or "Task" => "IconToolAgent",
        "TodoWrite" or "TaskCreate" or "TaskUpdate" or "TaskList" or "TaskGet" => "IconToolTodo",
        "Skill" => "IconToolSkill",
        "AskUserQuestion" => "IconToolQuestion",
        "ExitPlanMode" or "EnterPlanMode" => "IconToolPlan",
        "ListMcpResourcesTool" or "ReadMcpResourceTool" => "IconToolMcp",
        _ when toolName.StartsWith("mcp__", StringComparison.Ordinal) => "IconToolMcp",
        _ => Default,
    };

    /// <summary>Looks the icon up in the app's resources; the default icon when it's missing.</summary>
    public static readonly IValueConverter Geometry = new FuncValueConverter<string?, Geometry?>(key =>
        Find(key ?? Default) ?? Find(Default));

    private static Geometry? Find(string key) =>
        Application.Current?.TryFindResource(key, out var resource) == true ? resource as Geometry : null;
}
