namespace Claudette.Core.ProjectTools;

/// <summary>A running process on this machine.</summary>
/// <param name="Name">The executable's name without <c>.exe</c>, such as <c>UnrealEditor</c>.</param>
/// <param name="CommandLine">Null when it can't be read (another user's process, say).</param>
public sealed record SystemProcess(int Pid, string Name, string? CommandLine);

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

    /// <summary>Ends a process and everything it started. Does nothing if it has already exited.</summary>
    void KillTree(int pid);
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

    /// <summary>Does the process's command line name <paramref name="path"/>, with either slash, ignoring case?</summary>
    public static bool CommandLineMentions(SystemProcess process, string path) =>
        process.CommandLine is { } commandLine
        && commandLine.Replace('\\', '/').Contains(path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
