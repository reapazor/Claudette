namespace Claudette.App.Services;

/// <summary>Claudette's command line (DESIGN.md §4, "Other ways in").</summary>
public static class LaunchArguments
{
    /// <summary><c>--folder &lt;path&gt;</c>: open a tab in that folder. The jump list and Open Recent use it too.</summary>
    public static string? Folder(IReadOnlyList<string> args)
    {
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == "--folder")
            {
                return Path.GetFullPath(args[i + 1]);
            }
        }
        return null;
    }
}
