using System.Diagnostics;
using System.Runtime.InteropServices;
using Claudette.Core;
using Claudette.Core.Development;
using Claudette.Core.Processes;

namespace Claudette.App.Services;

/// <summary>
/// Starting a source build of Claudette (DESIGN.md §9, "Working on Claudette"). It runs from a copy of its build
/// output, so a Claude Code session working on Claudette can rebuild it while it runs: Windows locks the files of a
/// running program, and replacing them in place elsewhere can crash it.
/// </summary>
public static class DevelopmentLaunch
{
    public const string AssemblyName = "Claudette";

    /// <summary>Set to <c>1</c> to run a source build where it is, for example to profile it. A debugger has the same effect.</summary>
    public const string RunInPlaceVariable = "CLAUDETTE_RUN_IN_PLACE";

    /// <summary>
    /// For a source build started from its build output: copies the build, starts the copy with the same arguments and
    /// returns true, so this process should exit. False to carry on here: an installed Claudette, a copy already, a
    /// debugger attached, or the copy couldn't be made.
    /// </summary>
    public static bool TryRunFromCopy(IReadOnlyList<string> args, AppPaths paths, IProcessLauncher launcher)
    {
        if (LaunchArguments.SourceBuild(args) is not null
            || Debugger.IsAttached
            || Environment.GetEnvironmentVariable(RunInPlaceVariable) == "1"
            || SourceBuild.Detect(AppContext.BaseDirectory) is not { } build)
        {
            return false;
        }
        try
        {
            var copies = new BuildCopies(paths.BuildCopiesDirectory);
            if (copies.Create(build.OutputDirectory, RuntimeInformation.RuntimeIdentifier) is not { } copy)
            {
                // Being rebuilt right now: run in place this time.
                return false;
            }
            copies.CleanUp(copy.Directory);
            launcher.Start(Command(copy.Directory, [.. LaunchArguments.WithoutDevelopmentOptions(args), LaunchArguments.SourceBuildOption, build.OutputDirectory]) with { Detached = true });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Trace.WriteLine($"Couldn't run from a copy of the build, so running in place: {ex}");
            return false;
        }
    }

    /// <summary>
    /// The command that starts the Claudette in <paramref name="directory"/>: its own executable, or
    /// <c>dotnet Claudette.dll</c> when this one was started that way.
    /// </summary>
    public static ProcessStartSpec Command(string directory, IReadOnlyList<string> args)
    {
        if (Environment.ProcessPath is { } host && Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return new ProcessStartSpec(host, [Path.Combine(directory, AssemblyName + ".dll"), .. args]) { WorkingDirectory = Environment.CurrentDirectory };
        }
        var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? AssemblyName + ".exe" : AssemblyName);
        return new ProcessStartSpec(executable, args) { WorkingDirectory = Environment.CurrentDirectory };
    }

    /// <summary>The source build this Claudette is, or null for an installed one.</summary>
    public static DevelopmentBuild? Current(IReadOnlyList<string> args)
    {
        var here = AppContext.BaseDirectory;
        if (LaunchArguments.SourceBuild(args) is { } output)
        {
            return new DevelopmentBuild(output, here, BuildCopies.Read(here)?.Stamp ?? BuildOutput.LatestWrite(here) ?? DateTime.MinValue);
        }
        return SourceBuild.Detect(here) is { } build
            ? new DevelopmentBuild(build.OutputDirectory, build.OutputDirectory, BuildOutput.LatestWrite(build.OutputDirectory) ?? DateTime.MinValue)
            : null;
    }
}
