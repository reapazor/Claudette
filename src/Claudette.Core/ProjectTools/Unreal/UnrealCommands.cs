using Claudette.Core.Processes;
using Claudette.Core.Settings;

namespace Claudette.Core.ProjectTools.Unreal;

/// <summary>
/// Every command the Unreal provider runs, for any OS (DESIGN.md §18, "Actions"). Pure: paths are joined for the OS
/// given, so the commands for Windows, macOS and Linux are all tested on any machine.
/// </summary>
public static class UnrealCommands
{
    /// <summary>The platform name Unreal's tools use: <c>Win64</c>, <c>Mac</c> or <c>Linux</c>.</summary>
    public static string Platform(ToolOS os) => os switch
    {
        ToolOS.Windows => "Win64",
        ToolOS.MacOS => "Mac",
        _ => "Linux",
    };

    /// <summary>
    /// The editor's executable: <c>Engine/Binaries/Win64/UnrealEditor.exe</c>,
    /// <c>Engine/Binaries/Mac/UnrealEditor.app/Contents/MacOS/UnrealEditor</c> or <c>Engine/Binaries/Linux/UnrealEditor</c>,
    /// with <c>UE4Editor</c> for Unreal Engine 4.
    /// </summary>
    public static string EditorPath(string engineRoot, string editorName, ToolOS os) => os switch
    {
        ToolOS.Windows => os.Join(engineRoot, "Engine", "Binaries", "Win64", $"{editorName}.exe"),
        ToolOS.MacOS => os.Join(engineRoot, "Engine", "Binaries", "Mac", $"{editorName}.app", "Contents", "MacOS", editorName),
        _ => os.Join(engineRoot, "Engine", "Binaries", "Linux", editorName),
    };

    /// <summary><c>Engine/Build/BatchFiles/Build.bat</c>, or <c>Mac/Build.sh</c> or <c>Linux/Build.sh</c> under it.</summary>
    public static string BuildScript(string engineRoot, ToolOS os) => os switch
    {
        ToolOS.Windows => os.Join(engineRoot, "Engine", "Build", "BatchFiles", "Build.bat"),
        ToolOS.MacOS => os.Join(engineRoot, "Engine", "Build", "BatchFiles", "Mac", "Build.sh"),
        _ => os.Join(engineRoot, "Engine", "Build", "BatchFiles", "Linux", "Build.sh"),
    };

    /// <summary>
    /// <b>Launch editor</b>: the editor with the project, detached so it outlives Claudette. DebugGame adds
    /// <c>-debug</c>, which makes the editor load the project's DebugGame modules.
    /// </summary>
    public static ProcessStartSpec LaunchEditor(string engineRoot, string editorName, string uproject, UnrealConfiguration configuration, ToolOS os)
    {
        List<string> arguments = [uproject];
        if (configuration == UnrealConfiguration.DebugGame)
        {
            arguments.Add("-debug");
        }
        return new ProcessStartSpec(EditorPath(engineRoot, editorName, os), arguments)
        {
            WorkingDirectory = ParentOf(uproject, os),
            Detached = true,
        };
    }

    /// <summary>
    /// <b>Generate project files</b>: <c>Build.bat -projectfiles -project="&lt;uproject&gt;" -game -progress</c>, what
    /// UnrealVersionSelector runs, which works for installed and source builds alike. VS Code adds <c>-vscode</c>.
    /// Visual Studio and Xcode are UnrealBuildTool's own defaults on Windows and macOS, so they add nothing, which
    /// leaves the user's <c>BuildConfiguration.xml</c> in charge.
    /// </summary>
    public static ProcessStartSpec GenerateProjectFiles(string engineRoot, string uproject, ProjectFileFormat format, ToolOS os)
    {
        List<string> arguments = ["-projectfiles", $"-project={uproject}", "-game", "-progress"];
        if (FormatSwitch(format) is { } formatSwitch)
        {
            arguments.Add(formatSwitch);
        }
        return Script(engineRoot, arguments, uproject, os);
    }

    /// <summary>The UnrealBuildTool switch for a project file format, or null for UnrealBuildTool's default.</summary>
    public static string? FormatSwitch(ProjectFileFormat format) => format == ProjectFileFormat.VSCode ? "-vscode" : null;

    /// <summary>
    /// <b>Build editor</b>: <c>Build.bat &lt;EditorTarget&gt; Win64 &lt;Development|DebugGame&gt; -Project="&lt;uproject&gt;" -WaitMutex</c>,
    /// with <c>Build.sh</c> and <c>Mac</c> or <c>Linux</c> elsewhere.
    /// </summary>
    public static ProcessStartSpec BuildEditor(string engineRoot, string target, string uproject, UnrealConfiguration configuration, ToolOS os) =>
        Script(engineRoot, [target, Platform(os), configuration.ToString(), $"-Project={uproject}", "-WaitMutex"], uproject, os);

    /// <summary>Runs the engine's build script with <paramref name="arguments"/>: through cmd.exe on Windows, bash elsewhere.</summary>
    private static ProcessStartSpec Script(string engineRoot, IReadOnlyList<string> arguments, string uproject, ToolOS os)
    {
        var script = BuildScript(engineRoot, os);
        return os == ToolOS.Windows
            ? CommandLines.BatchFile(script, arguments, ParentOf(uproject, os))
            : CommandLines.ShellScript(script, arguments, ParentOf(uproject, os));
    }

    /// <summary>
    /// The solution or workspace <b>Open solution</b> opens, for the format: <c>&lt;Name&gt;.sln</c>,
    /// <c>&lt;Name&gt; (Mac).xcworkspace</c> or <c>&lt;Name&gt;.xcworkspace</c>, or <c>&lt;Name&gt;.code-workspace</c>, in the
    /// project's folder. In order of preference.
    /// </summary>
    public static IReadOnlyList<string> SolutionCandidates(string root, string name, ProjectFileFormat format, ToolOS os) => format switch
    {
        ProjectFileFormat.Xcode => [os.Join(root, $"{name} (Mac).xcworkspace"), os.Join(root, $"{name}.xcworkspace")],
        ProjectFileFormat.VSCode => [os.Join(root, $"{name}.code-workspace")],
        _ => [os.Join(root, $"{name}.sln")],
    };

    /// <summary><b>Open latest log</b>: <c>Saved/Logs/&lt;Name&gt;.log</c>.</summary>
    public static string LogPath(string root, string name, ToolOS os) => os.Join(root, "Saved", "Logs", $"{name}.log");

    private static string ParentOf(string path, ToolOS os)
    {
        var cut = path.LastIndexOfAny(os == ToolOS.Windows ? ['\\', '/'] : ['/']);
        return cut > 0 ? path[..cut] : path;
    }
}
