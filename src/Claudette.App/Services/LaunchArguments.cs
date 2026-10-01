using Claudette.Core.LoginItems;

namespace Claudette.App.Services;

/// <summary>Claudette's command line (DESIGN.md §4, "Other ways in", and §9, "Starting at login" and "Working on Claudette").</summary>
public static class LaunchArguments
{
    public const string FolderOption = "--folder";

    public const string SourceBuildOption = "--source-build";

    public const string RestoreOption = "--restore";

    /// <summary><c>--login</c>: started by the login entry (DESIGN.md §9, "Starting at login").</summary>
    public const string LoginOption = LoginCommand.LoginOption;

    /// <summary>
    /// Started at login: open minimized, or hand over to a better copy of Claudette. A running Claudette ignores it from
    /// a later launch.
    /// </summary>
    public static bool IsLogin(IReadOnlyList<string> args) => args.Contains(LoginOption);

    /// <summary><c>--folder &lt;path&gt;</c>: open a tab in that folder. The jump list and Open Recent use it too.</summary>
    public static string? Folder(IReadOnlyList<string> args) => Value(args, FolderOption) is { } folder ? Path.GetFullPath(folder) : null;

    /// <summary><c>--source-build &lt;path&gt;</c>: this Claudette runs from a copy of that build output, and watches it for new builds.</summary>
    public static string? SourceBuild(IReadOnlyList<string> args) => Value(args, SourceBuildOption) is { } output ? Path.GetFullPath(output) : null;

    /// <summary><c>--restore &lt;nonce&gt;</c>: take over the tabs of the build that restarted into this one.</summary>
    public static string? RestoreNonce(IReadOnlyList<string> args) => Value(args, RestoreOption);

    /// <summary>
    /// The arguments with the paths of <c>--folder</c> and <c>--source-build</c> made absolute from
    /// <paramref name="currentDirectory"/>: where this launch started. A Claudette already running, which a second launch
    /// hands its arguments to, has a current folder of its own, so <c>claudette --folder .</c> would open that.
    /// </summary>
    public static string[] WithFullPaths(IReadOnlyList<string> args, string currentDirectory)
    {
        var resolved = args.ToArray();
        for (var i = 0; i + 1 < resolved.Length; i++)
        {
            if (resolved[i] is FolderOption or SourceBuildOption)
            {
                resolved[i + 1] = Path.GetFullPath(resolved[i + 1], currentDirectory);
                i++;
            }
        }
        return resolved;
    }

    /// <summary>The arguments without <c>--source-build</c> and <c>--restore</c>, which only Claudette passes itself.</summary>
    public static List<string> WithoutDevelopmentOptions(IReadOnlyList<string> args)
    {
        var kept = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is SourceBuildOption or RestoreOption)
            {
                i++;
                continue;
            }
            kept.Add(args[i]);
        }
        return kept;
    }

    private static string? Value(IReadOnlyList<string> args, string option)
    {
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == option)
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
