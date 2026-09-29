using Claudette.Core.ProjectTools;
using Claudette.Core.ProjectTools.Unreal;
using Claudette.Core.Settings;

namespace Claudette.Core.Tests.ProjectTools;

/// <summary>
/// The exact commands of every Unreal action on Windows, macOS and Linux (DESIGN.md §18, "Actions"), and the cmd.exe
/// quoting of <c>.bat</c> files. Nothing is started.
/// </summary>
public sealed class UnrealCommandTests
{
    private const string WinEngine = @"C:\Program Files\Epic Games\UE_5.4";
    private const string WinProject = @"D:\My Games\NightOwl\NightOwl.uproject";
    private const string MacEngine = "/Users/Shared/Epic Games/UE_5.4";
    private const string MacProject = "/Users/matt/My Games/NightOwl/NightOwl.uproject";
    private const string LinuxEngine = "/home/matt/UnrealEngine";
    private const string LinuxProject = "/home/matt/games/NightOwl/NightOwl.uproject";

    // ---- Launch editor ---------------------------------------------------------------------------------------------

    [Fact]
    public void Launch_editor_on_Windows_starts_UnrealEditor_exe_detached()
    {
        var spec = UnrealCommands.LaunchEditor(WinEngine, "UnrealEditor", WinProject, UnrealConfiguration.Development, ToolOS.Windows);

        Assert.Equal(@"C:\Program Files\Epic Games\UE_5.4\Engine\Binaries\Win64\UnrealEditor.exe", spec.FileName);
        Assert.Equal([WinProject], spec.Arguments);
        Assert.True(spec.Detached);
        Assert.Equal(@"D:\My Games\NightOwl", spec.WorkingDirectory);
    }

    [Fact]
    public void Launch_editor_on_macOS_starts_the_binary_inside_the_app_bundle()
    {
        var spec = UnrealCommands.LaunchEditor(MacEngine, "UnrealEditor", MacProject, UnrealConfiguration.Development, ToolOS.MacOS);

        Assert.Equal("/Users/Shared/Epic Games/UE_5.4/Engine/Binaries/Mac/UnrealEditor.app/Contents/MacOS/UnrealEditor", spec.FileName);
        Assert.Equal([MacProject], spec.Arguments);
    }

    [Fact]
    public void Launch_editor_on_Linux_and_for_UE4()
    {
        var spec = UnrealCommands.LaunchEditor(LinuxEngine, "UnrealEditor", LinuxProject, UnrealConfiguration.Development, ToolOS.Linux);
        Assert.Equal("/home/matt/UnrealEngine/Engine/Binaries/Linux/UnrealEditor", spec.FileName);

        Assert.Equal(@"C:\UE_4.27\Engine\Binaries\Win64\UE4Editor.exe",
            UnrealCommands.LaunchEditor(@"C:\UE_4.27", "UE4Editor", WinProject, UnrealConfiguration.Development, ToolOS.Windows).FileName);
        Assert.Equal("/UE_4.27/Engine/Binaries/Mac/UE4Editor.app/Contents/MacOS/UE4Editor",
            UnrealCommands.LaunchEditor("/UE_4.27", "UE4Editor", MacProject, UnrealConfiguration.Development, ToolOS.MacOS).FileName);
        Assert.Equal("/UE_4.27/Engine/Binaries/Linux/UE4Editor",
            UnrealCommands.LaunchEditor("/UE_4.27", "UE4Editor", LinuxProject, UnrealConfiguration.Development, ToolOS.Linux).FileName);
    }

    [Theory]
    [InlineData(ToolOS.Windows)]
    [InlineData(ToolOS.MacOS)]
    [InlineData(ToolOS.Linux)]
    public void DebugGame_adds_debug_so_the_editor_loads_the_DebugGame_modules(ToolOS os)
    {
        var project = os == ToolOS.Windows ? WinProject : MacProject;

        var spec = UnrealCommands.LaunchEditor("/UE", "UnrealEditor", project, UnrealConfiguration.DebugGame, os);

        Assert.Equal([project, "-debug"], spec.Arguments);
    }

    // ---- Generate project files and build ----------------------------------------------------------------------------

    [Fact]
    public void Generate_project_files_on_Windows_runs_Build_bat_through_cmd_with_paths_quoted()
    {
        var spec = UnrealCommands.GenerateProjectFiles(WinEngine, WinProject, ProjectFileFormat.VisualStudio, ToolOS.Windows);

        Assert.Equal("cmd.exe", spec.FileName);
        Assert.Equal(
            """/d /s /c ""C:\Program Files\Epic Games\UE_5.4\Engine\Build\BatchFiles\Build.bat" -projectfiles -project="D:\My Games\NightOwl\NightOwl.uproject" -game -progress" """.TrimEnd(),
            spec.CommandLine);
        Assert.Equal(@"D:\My Games\NightOwl", spec.WorkingDirectory);
        Assert.Equal(
            @"""C:\Program Files\Epic Games\UE_5.4\Engine\Build\BatchFiles\Build.bat"" -projectfiles -project=""D:\My Games\NightOwl\NightOwl.uproject"" -game -progress",
            CommandLines.Display(spec));
    }

    [Fact]
    public void Generate_project_files_on_macOS_and_Linux_runs_Build_sh_through_bash()
    {
        var mac = UnrealCommands.GenerateProjectFiles(MacEngine, MacProject, ProjectFileFormat.Xcode, ToolOS.MacOS);
        Assert.Equal("/bin/bash", mac.FileName);
        Assert.Equal(["/Users/Shared/Epic Games/UE_5.4/Engine/Build/BatchFiles/Mac/Build.sh", "-projectfiles", $"-project={MacProject}", "-game", "-progress"], mac.Arguments);
        Assert.Null(mac.CommandLine);

        var linux = UnrealCommands.GenerateProjectFiles(LinuxEngine, LinuxProject, ProjectFileFormat.VSCode, ToolOS.Linux);
        Assert.Equal(["/home/matt/UnrealEngine/Engine/Build/BatchFiles/Linux/Build.sh", "-projectfiles", $"-project={LinuxProject}", "-game", "-progress", "-vscode"], linux.Arguments);
        Assert.Equal("/home/matt/UnrealEngine/Engine/Build/BatchFiles/Linux/Build.sh -projectfiles -project=/home/matt/games/NightOwl/NightOwl.uproject -game -progress -vscode",
            CommandLines.Display(linux));
    }

    [Fact]
    public void Only_VS_Code_adds_a_format_switch()
    {
        Assert.Equal("-vscode", UnrealCommands.FormatSwitch(ProjectFileFormat.VSCode));
        Assert.Null(UnrealCommands.FormatSwitch(ProjectFileFormat.VisualStudio));
        Assert.Null(UnrealCommands.FormatSwitch(ProjectFileFormat.Xcode));
        Assert.EndsWith("-game -progress -vscode\"", UnrealCommands.GenerateProjectFiles(WinEngine, WinProject, ProjectFileFormat.VSCode, ToolOS.Windows).CommandLine, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UnrealConfiguration.Development)]
    [InlineData(UnrealConfiguration.DebugGame)]
    public void Build_editor_on_Windows(UnrealConfiguration configuration)
    {
        var spec = UnrealCommands.BuildEditor(WinEngine, "NightOwlEditor", WinProject, configuration, ToolOS.Windows);

        Assert.Equal(
            $"""/d /s /c ""C:\Program Files\Epic Games\UE_5.4\Engine\Build\BatchFiles\Build.bat" NightOwlEditor Win64 {configuration} -Project="D:\My Games\NightOwl\NightOwl.uproject" -WaitMutex" """.TrimEnd(),
            spec.CommandLine);
    }

    [Fact]
    public void Build_editor_on_macOS_and_Linux_uses_the_Mac_or_Linux_platform()
    {
        Assert.Equal(["/Users/Shared/Epic Games/UE_5.4/Engine/Build/BatchFiles/Mac/Build.sh", "NightOwlEditor", "Mac", "DebugGame", $"-Project={MacProject}", "-WaitMutex"],
            UnrealCommands.BuildEditor(MacEngine, "NightOwlEditor", MacProject, UnrealConfiguration.DebugGame, ToolOS.MacOS).Arguments);
        Assert.Equal(["/home/matt/UnrealEngine/Engine/Build/BatchFiles/Linux/Build.sh", "NightOwlEditor", "Linux", "Development", $"-Project={LinuxProject}", "-WaitMutex"],
            UnrealCommands.BuildEditor(LinuxEngine, "NightOwlEditor", LinuxProject, UnrealConfiguration.Development, ToolOS.Linux).Arguments);
    }

    // ---- Solutions and logs ------------------------------------------------------------------------------------------

    [Fact]
    public void The_solution_depends_on_the_format()
    {
        Assert.Equal([@"D:\NightOwl\NightOwl.sln"], UnrealCommands.SolutionCandidates(@"D:\NightOwl", "NightOwl", ProjectFileFormat.VisualStudio, ToolOS.Windows));
        Assert.Equal(["/g/NightOwl/NightOwl (Mac).xcworkspace", "/g/NightOwl/NightOwl.xcworkspace"],
            UnrealCommands.SolutionCandidates("/g/NightOwl", "NightOwl", ProjectFileFormat.Xcode, ToolOS.MacOS));
        Assert.Equal(["/g/NightOwl/NightOwl.code-workspace"], UnrealCommands.SolutionCandidates("/g/NightOwl", "NightOwl", ProjectFileFormat.VSCode, ToolOS.Linux));
        Assert.Equal(@"D:\NightOwl\Saved\Logs\NightOwl.log", UnrealCommands.LogPath(@"D:\NightOwl", "NightOwl", ToolOS.Windows));
    }

    [Fact]
    public void Each_OS_has_its_default_project_file_format()
    {
        Assert.Equal(ProjectFileFormat.VisualStudio, new ProjectToolSettings().FormatFor(ToolOS.Windows));
        Assert.Equal(ProjectFileFormat.Xcode, new ProjectToolSettings().FormatFor(ToolOS.MacOS));
        Assert.Equal(ProjectFileFormat.VSCode, new ProjectToolSettings().FormatFor(ToolOS.Linux));
        Assert.Equal(ProjectFileFormat.VSCode, new ProjectToolSettings { ProjectFileFormat = ProjectFileFormat.VSCode }.FormatFor(ToolOS.Windows));
    }
}
