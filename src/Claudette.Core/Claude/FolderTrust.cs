using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Json;
using Claudette.Core.Protocol;

namespace Claudette.Core.Claude;

/// <summary>What a folder's own Claude Code configuration makes a session there run or allow.</summary>
public enum ProjectSettingKind
{
    /// <summary>A hook in the folder's settings: a command (or request) Claude Code runs on an event.</summary>
    Hook,

    /// <summary>A helper command, such as <c>apiKeyHelper</c>, that Claude Code runs for credentials or headers.</summary>
    Helper,

    /// <summary>Environment variables Claude Code sets for itself and everything it starts.</summary>
    Environment,

    /// <summary>A server in a <c>.mcp.json</c>: a program Claude Code starts, or an address it connects to.</summary>
    McpServer,

    /// <summary>A plugin the folder's settings turn on, with whatever hooks and servers it has.</summary>
    Plugin,

    /// <summary>A project skill whose frontmatter has hooks.</summary>
    SkillHook,

    /// <summary>A rule in the folder's local settings that lets a tool run without asking.</summary>
    AllowRule,
}

/// <summary>One thing a folder's configuration runs or allows, and the file that says so (relative to the folder).</summary>
public sealed record ProjectSetting(ProjectSettingKind Kind, string File, string Text);

/// <summary>
/// Which folders Claudette starts Claude Code in without asking first (DESIGN.md §7, "Folder trust"). Claude Code's own
/// trust dialog never appears in a <c>claude -p</c> session like a tab's, and such a session runs what a folder's
/// configuration says straight away: its settings' hooks, <c>env</c> and helper commands, its skills' hooks, and the
/// servers in any <c>.mcp.json</c> from the folder up. So before the first start in a folder it hasn't been told to
/// trust, Claudette lists those and asks.
/// </summary>
public static class FolderTrust
{
    /// <summary>The settings keys whose value is a command line Claude Code runs (settings reference, "Authentication").</summary>
    public static readonly IReadOnlyList<string> HelperKeys = ["apiKeyHelper", "awsAuthRefresh", "awsCredentialExport", "gcpAuthRefresh", "otelHeadersHelper"];

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// Whether <paramref name="folder"/> is one of <paramref name="trusted"/> or inside one: trusting a folder trusts
    /// what's in it, as Claude Code's own trust does outside a repository.
    /// </summary>
    public static bool IsTrusted(IEnumerable<string> trusted, string folder)
    {
        var full = Trimmed(folder);
        foreach (var entry in trusted)
        {
            var root = Trimmed(entry);
            if (string.Equals(full, root, PathComparison)
                || full.Length > root.Length && full.StartsWith(root, PathComparison) && full[root.Length] is '/' or '\\')
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// What a <c>claude -p</c> session in <paramref name="folder"/> would run or allow from the folder's own
    /// configuration. Files that are missing, unreadable or not valid JSON are skipped, as Claude Code skips them.
    /// </summary>
    public static IReadOnlyList<ProjectSetting> WhatRuns(string folder)
    {
        var found = new List<ProjectSetting>();
        foreach (var (name, local) in new[] { ("settings.json", false), ("settings.local.json", true) })
        {
            var file = Path.Combine(".claude", name);
            if (ReadObject(Path.Combine(folder, file)) is { } settings)
            {
                ReadSettings(settings, file, local, found);
            }
        }
        ReadSkills(folder, found);
        ReadMcpServers(folder, found);
        return found;
    }

    private static void ReadSettings(JsonObject settings, string file, bool local, List<ProjectSetting> found)
    {
        if (settings["hooks"] is JsonObject hooks)
        {
            foreach (var (hookEvent, groups) in hooks)
            {
                foreach (var group in (groups as JsonArray ?? []).OfType<JsonObject>())
                {
                    var matcher = group.GetString("matcher") is { Length: > 0 } m && m != "*" ? $" ({m})" : "";
                    foreach (var hook in (group["hooks"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        found.Add(new ProjectSetting(ProjectSettingKind.Hook, file, $"{hookEvent}{matcher}: {Describe(hook)}"));
                    }
                }
            }
        }
        foreach (var key in HelperKeys)
        {
            if (settings.GetString(key) is { Length: > 0 } command)
            {
                found.Add(new ProjectSetting(ProjectSettingKind.Helper, file, $"{key}: {command}"));
            }
        }
        // Only the names: values can be secrets.
        if (settings["env"] is JsonObject { Count: > 0 } env)
        {
            found.Add(new ProjectSetting(ProjectSettingKind.Environment, file, string.Join(", ", env.Select(e => e.Key))));
        }
        if (settings["enabledPlugins"] is JsonObject plugins)
        {
            foreach (var (plugin, on) in plugins)
            {
                if (on is JsonValue value && value.GetValueKind() == JsonValueKind.True)
                {
                    found.Add(new ProjectSetting(ProjectSettingKind.Plugin, file, plugin));
                }
            }
        }
        // A -p session leaves the shared file's allow rules out until the folder is trusted in Claude Code itself, but
        // takes an untracked local file's.
        if (local && settings["permissions"] is JsonObject permissions)
        {
            foreach (var rule in permissions.GetStringList("allow"))
            {
                found.Add(new ProjectSetting(ProjectSettingKind.AllowRule, file, rule));
            }
        }
    }

    private static string Describe(JsonObject hook) => hook.GetString("type") switch
    {
        "command" or null => hook.GetString("command") ?? "a command",
        "http" => hook.GetString("url") is { } url ? $"sends to {url}" : "an HTTP request",
        var other => $"a {other} hook",
    };

    /// <summary>Project skills (<c>.claude/skills/&lt;name&gt;/SKILL.md</c>) with hooks in their frontmatter.</summary>
    private static void ReadSkills(string folder, List<ProjectSetting> found)
    {
        var skills = Path.Combine(folder, ".claude", "skills");
        string[] files;
        try
        {
            files = Directory.Exists(skills) ? Directory.GetFiles(skills, "SKILL.md", SearchOption.AllDirectories) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            if (FrontmatterHasHooks(file))
            {
                var relative = Path.GetRelativePath(folder, file);
                found.Add(new ProjectSetting(ProjectSettingKind.SkillHook, relative, Path.GetFileName(Path.GetDirectoryName(file)!)));
            }
        }
    }

    private static bool FrontmatterHasHooks(string file)
    {
        try
        {
            using var reader = new StreamReader(file);
            if (reader.ReadLine()?.Trim() != "---")
            {
                return false;
            }
            for (var line = reader.ReadLine(); line is not null && line.Trim() != "---"; line = reader.ReadLine())
            {
                if (line.StartsWith("hooks:", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return false;
    }

    /// <summary>
    /// The servers in every <c>.mcp.json</c> from <paramref name="folder"/> up: Claude Code finds one in a parent folder
    /// too, past the repository's root (checked with 2.1.286). A name a nearer file has already listed is left out.
    /// </summary>
    private static void ReadMcpServers(string folder, List<ProjectSetting> found)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var directory = new DirectoryInfo(Path.GetFullPath(folder)); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, ".mcp.json");
            if (ReadObject(path)?["mcpServers"] is not JsonObject servers)
            {
                continue;
            }
            var file = Path.GetRelativePath(folder, path);
            foreach (var (name, node) in servers)
            {
                if (node is JsonObject server && names.Add(name))
                {
                    found.Add(new ProjectSetting(ProjectSettingKind.McpServer, file, $"{name}: {DescribeServer(server)}"));
                }
            }
        }
    }

    private static string DescribeServer(JsonObject server)
    {
        if (server.GetString("url") is { } url)
        {
            return url;
        }
        var command = server.GetString("command") ?? "";
        var arguments = server.GetStringList("args");
        return arguments.Count == 0 ? command : $"{command} {string.Join(' ', arguments)}";
    }

    private static JsonObject? ReadObject(string file)
    {
        try
        {
            return File.Exists(file) ? JsonTree.Parse(File.ReadAllText(file), Lenient) as JsonObject : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Trimmed(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    private static StringComparison PathComparison => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}
