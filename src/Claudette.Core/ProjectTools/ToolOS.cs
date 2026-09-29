namespace Claudette.Core.ProjectTools;

/// <summary>
/// The OS a project action is built for. Every command is built by a pure function that takes it as a parameter, so
/// the commands for all three OSes are tested on any machine (DESIGN.md §18, "Project tools").
/// </summary>
public enum ToolOS
{
    Windows,
    MacOS,
    Linux,
}

public static class ToolOSExtensions
{
    /// <summary>The OS Claudette is running on.</summary>
    public static ToolOS Current { get; } =
        OperatingSystem.IsWindows() ? ToolOS.Windows
        : OperatingSystem.IsMacOS() ? ToolOS.MacOS
        : ToolOS.Linux;

    /// <summary>Paths on Windows and macOS usually ignore case; on Linux they don't.</summary>
    public static StringComparison PathComparison(this ToolOS os) =>
        os == ToolOS.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static StringComparer PathComparer(this ToolOS os) =>
        os == ToolOS.Linux ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>Joins path parts with the OS's separator, so a Windows path built on Linux still reads as one.</summary>
    public static string Join(this ToolOS os, string first, params string[] rest)
    {
        var separator = os == ToolOS.Windows ? '\\' : '/';
        var result = first.TrimEnd('/', '\\');
        foreach (var part in rest)
        {
            var trimmed = part.Trim('/', '\\');
            if (trimmed.Length == 0)
            {
                continue;
            }
            if (os == ToolOS.Windows)
            {
                trimmed = trimmed.Replace('/', '\\');
            }
            result = result.Length == 0 ? trimmed : $"{result}{separator}{trimmed}";
        }
        return result;
    }
}
