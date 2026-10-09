namespace Claudette.Core.ProjectTools;

/// <summary>A running process on this machine.</summary>
/// <param name="Name">The executable's name without <c>.exe</c>, such as <c>UnrealEditor</c>.</param>
/// <param name="CommandLine">Null when it can't be read (another user's process, say).</param>
/// <param name="StartedAt">
/// When it started, if that could be read: with the PID, it tells this process apart from a later one that reuses the
/// PID after it exits.
/// </param>
public sealed record SystemProcess(int Pid, string Name, string? CommandLine, DateTimeOffset? StartedAt = null);

/// <summary>
/// Every running process by name, for project tools (DESIGN.md §18): whether an editor has a project open, and
/// <b>Kill all editors</b>. The implementations are in Claudette.Platform, with the process monitor's code.
/// </summary>
public interface ISystemProcesses
{
    /// <summary>
    /// The running processes named one of <paramref name="names"/>, ignoring case and <c>.exe</c>. A name ending in
    /// <c>*</c> matches as a prefix (<c>godot*</c>). Never includes Claudette itself.
    /// </summary>
    IReadOnlyList<SystemProcess> Find(IReadOnlyCollection<string> names);

    /// <summary>
    /// Ends a process found by <see cref="Find"/> and everything it started. Does nothing if it has already exited, or
    /// if its PID now belongs to a process that started at another time: the one found exited while the user decided,
    /// and its PID was reused.
    /// </summary>
    void KillTree(SystemProcess process);

    /// <summary>
    /// Whether a process with <paramref name="pid"/> is running: a Claude Code session's entry outlives a session that
    /// crashed (DESIGN.md §13, "Session naming"). One that can't tell says it is.
    /// </summary>
    bool IsRunning(int pid) => true;
}

public static class SystemProcessNames
{
    /// <summary>Does <paramref name="name"/> (with or without <c>.exe</c>) match one of <paramref name="patterns"/>?</summary>
    public static bool Matches(string name, IEnumerable<string> patterns)
    {
        var bare = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        foreach (var pattern in patterns)
        {
            var target = pattern.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? pattern[..^4] : pattern;
            if (target.EndsWith('*')
                    ? bare.StartsWith(target[..^1], StringComparison.OrdinalIgnoreCase)
                    : string.Equals(bare, target, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The file name, without its extension, of the first argument ending in <paramref name="extension"/> (such as
    /// <c>.uproject</c>), quoted or not; null when there's none.
    /// </summary>
    public static string? FileArgument(string? commandLine, string extension)
    {
        if (commandLine is null)
        {
            return null;
        }
        var end = commandLine.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            return null;
        }
        var start = commandLine.LastIndexOfAny(['"', '\'', '/', '\\', ' ', '='], end) + 1;
        var name = commandLine[start..end];
        return name.Length > 0 ? name : null;
    }

    /// <summary>The folder name after an option such as <c>-projectPath</c> or <c>--path</c>, quoted or not; null when there's none.</summary>
    public static string? FolderAfter(string? commandLine, string option)
    {
        if (commandLine is null)
        {
            return null;
        }
        var at = commandLine.IndexOf(option + " ", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }
        var rest = commandLine[(at + option.Length + 1)..].TrimStart();
        var value = rest.StartsWith('"') ? rest[1..].Split('"')[0] : rest.Split(' ')[0];
        var trimmed = value.TrimEnd('/', '\\');
        var name = trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];
        return name.Length > 0 ? name : null;
    }

    /// <summary>Does the process's command line name <paramref name="path"/>, with either slash, ignoring case?</summary>
    public static bool CommandLineMentions(SystemProcess process, string path) =>
        process.CommandLine is { } commandLine
        && commandLine.Replace('\\', '/').Contains(path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
