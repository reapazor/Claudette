using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Json;

namespace Claudette.Core.Claude;

/// <summary>Where a Claude Code settings file stands in its precedence.</summary>
public enum SettingsScope
{
    Managed,
    Local,
    Project,
    User,
}

/// <summary>One of Claude Code's settings files.</summary>
public sealed record ClaudeSettingsFile(string Path, SettingsScope Scope);

/// <summary>
/// What Claude Code's settings files say about the permission mode a session starts in (DESIGN.md §7, "Starting
/// mode"). A terminal or VS Code session starts in auto mode unless a file sets <c>permissions.defaultMode</c>, but a
/// <c>claude -p</c> session like a tab's starts in Manual, so Claudette asks for auto mode itself.
/// </summary>
/// <param name="DefaultMode">
/// The <c>permissions.defaultMode</c> that takes effect, with <c>manual</c> as <c>default</c>. Null when no file sets
/// one, or the file that wins is a project's setting <c>auto</c>, which Claude Code doesn't take from there.
/// </param>
/// <param name="AutoModeDisabled">A file sets <c>disableAutoMode</c>, so no session can use auto mode.</param>
public sealed record StartingPermissionMode(string? DefaultMode, bool AutoModeDisabled)
{
    public const string Auto = "auto";

    /// <summary>Manual mode, whose config value is <c>default</c>.</summary>
    public const string Manual = "default";

    /// <summary>
    /// The mode to launch with when Claudette's settings don't choose one: auto, as a terminal session would start in,
    /// or null to leave it to Claude Code, which applies <see cref="DefaultMode"/>. When auto mode turns out to be
    /// unavailable (the model doesn't support it, or it's off server-side), Claude Code starts in Manual instead.
    /// </summary>
    public string? LaunchMode => (DefaultMode is null or Auto) && !AutoModeDisabled ? Auto : null;

    /// <summary>The mode a session is expected to start in, for labels such as "Default (Auto)".</summary>
    public string Expected => LaunchMode ?? (DefaultMode is null or Auto ? Manual : DefaultMode);

    /// <summary>
    /// The folder of the <c>managed-settings.json</c> an organization deploys, for the OS Claudette runs on. Claude Code
    /// also takes managed settings from MDM, the registry and the claude.ai console, which Claudette doesn't read.
    /// </summary>
    public static string? ManagedDirectory =>
        OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClaudeCode")
        : OperatingSystem.IsMacOS() ? "/Library/Application Support/ClaudeCode"
        : OperatingSystem.IsLinux() ? "/etc/claude-code"
        : null;

    /// <summary>
    /// Claude Code's settings files for a session in <paramref name="projectFolder"/>, the one that wins first: managed
    /// settings (the <c>managed-settings.d</c> drop-ins, which apply after <c>managed-settings.json</c> in name order,
    /// so from the last), the project's local and shared settings, then the user's.
    /// </summary>
    /// <param name="managedDirectory">Where <c>managed-settings.json</c> is; null skips managed settings.</param>
    /// <param name="configDirectory">Claude Code's config folder (<c>claude auth status</c>'s <c>configDirectory</c>); null skips the user's file.</param>
    public static IReadOnlyList<ClaudeSettingsFile> SettingsFiles(string? managedDirectory, string? configDirectory, string? projectFolder)
    {
        var files = new List<ClaudeSettingsFile>();
        if (managedDirectory is not null)
        {
            files.AddRange(DropIns(Path.Combine(managedDirectory, "managed-settings.d"))
                .Select(f => new ClaudeSettingsFile(f, SettingsScope.Managed)));
            files.Add(new ClaudeSettingsFile(Path.Combine(managedDirectory, "managed-settings.json"), SettingsScope.Managed));
        }
        if (projectFolder is not null)
        {
            files.Add(new ClaudeSettingsFile(Path.Combine(projectFolder, ".claude", "settings.local.json"), SettingsScope.Local));
            files.Add(new ClaudeSettingsFile(Path.Combine(projectFolder, ".claude", "settings.json"), SettingsScope.Project));
        }
        if (configDirectory is not null)
        {
            files.Add(new ClaudeSettingsFile(Path.Combine(configDirectory, "settings.json"), SettingsScope.User));
        }
        return files;
    }

    /// <summary>
    /// Reads <paramref name="files"/>, in <see cref="SettingsFiles"/>' order. A file that's missing, unreadable or not
    /// valid JSON is skipped, as Claude Code skips it in a <c>-p</c> run.
    /// </summary>
    public static StartingPermissionMode Read(IEnumerable<ClaudeSettingsFile> files)
    {
        string? defaultMode = null;
        var found = false;
        var disabled = false;
        foreach (var file in files)
        {
            if (ReadObject(file.Path) is not { } root)
            {
                continue;
            }
            var permissions = root["permissions"] as JsonObject;
            disabled |= Text(root["disableAutoMode"]) == "disable" || Text(permissions?["disableAutoMode"]) == "disable";
            if (!found && Text(permissions?["defaultMode"]) is { } mode)
            {
                found = true;
                defaultMode = Effective(mode, file.Scope);
            }
        }
        return new StartingPermissionMode(defaultMode, disabled);
    }

    /// <summary>
    /// The mode <paramref name="mode"/> starts a session in. A project's files can't set <c>auto</c> (the built-in
    /// default applies instead, not the user's setting) or <c>bypassPermissions</c> (the session starts in Manual).
    /// </summary>
    private static string? Effective(string mode, SettingsScope scope) => (mode, scope) switch
    {
        ("manual", _) => Manual,
        (Auto, SettingsScope.Local or SettingsScope.Project) => null,
        ("bypassPermissions", SettingsScope.Local or SettingsScope.Project) => Manual,
        _ => mode,
    };

    private static IEnumerable<string> DropIns(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*.json")
                    .Where(f => !Path.GetFileName(f).StartsWith('.'))
                    .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
                    .ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static JsonObject? ReadObject(string file)
    {
        try
        {
            return File.Exists(file)
                ? JsonTree.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
