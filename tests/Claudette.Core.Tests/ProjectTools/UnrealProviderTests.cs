using Claudette.Core.ProjectTools;
using Claudette.Core.ProjectTools.Unreal;
using Claudette.Core.Settings;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.ProjectTools;

/// <summary>The Unreal provider's project: its actions, what's enabled, Clean, and the note to Claude (DESIGN.md §18).</summary>
public sealed class UnrealProviderTests : IDisposable
{
    private readonly TempFolder _temp = new("claudette-unrealprovider");
    private readonly UnrealProvider _unreal = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>A code project under an engine in a parent folder (EngineAssociation empty): no registry needed.</summary>
    private (ProjectInfo Info, string Engine, string Project) NativeProject(ProjectToolSettings? settings = null, IProjectMemory? memory = null, bool code = true,
        ToolOS? os = null, (int Major, int Minor, int Patch)? version = null)
    {
        var (major, minor, patch) = version ?? (5, 4, 2);
        var engine = ProjectToolFixtures.Engine(_temp, "UE5", major, minor, patch, installed: false);
        var project = ProjectToolFixtures.UProject(_temp, "UE5/NightOwl/NightOwl.uproject", association: "", code: code);
        var context = ProjectToolFixtures.Context(_temp, os: os, settings: settings, memory: memory);
        var info = _unreal.Describe(new ProjectCandidate(UnrealProvider.KindId, project, "NightOwl"), context);
        return (info, engine, project);
    }

    private static ProjectAction Action(ProjectInfo info, string id) => info.Actions.Single(a => a.Id == id);

    [Fact]
    public void A_project_lists_its_actions_with_the_editor_as_the_main_one()
    {
        var (info, engine, project) = NativeProject();

        Assert.Equal("NightOwl", info.Name);
        Assert.Equal("UE 5.4", info.ShortVersion);
        Assert.Equal(["launch-editor", "generate-project-files", "build-editor", "build-and-launch", "open-solution", "open-log", "clean", "kill-editors"], info.Actions.Select(a => a.Id));
        Assert.Equal("launch-editor", info.MainAction!.Id);
        Assert.Equal(ProjectActionKind.Launch, Action(info, "launch-editor").Kind);
        Assert.True(Action(info, "launch-editor").IsEnabled);
        Assert.Equal(ProjectActionKind.Run, Action(info, "build-editor").Kind);
        Assert.Contains("NightOwlEditor", Action(info, "build-editor").CommandText, StringComparison.Ordinal);
        Assert.Equal(["Unreal Engine 5.4.2 · engine in a parent folder", engine], info.HeaderLines);
        Assert.Contains(info.Details, d => d.Label == "Project" && d.Value == project);
        Assert.Contains(info.Details, d => d.Label == "Editor target" && d.Value == "NightOwlEditor");
        Assert.Contains(info.Details, d => d.Label == "Configuration" && d.Value == "Development");
    }

    [Fact]
    public void Build_and_launch_launches_the_editor_after_the_build()
    {
        var (info, _, _) = NativeProject();

        var both = Action(info, "build-and-launch");

        Assert.Equal(Action(info, "build-editor").Process, both.Process);
        Assert.Equal("launch-editor", both.ThenOnSuccess!.Id);
        Assert.True(both.ThenOnSuccess.IsEnabled);
    }

    [Fact]
    public void Open_solution_is_disabled_until_project_files_are_generated()
    {
        var settings = new ProjectToolSettings { ProjectFileFormat = ProjectFileFormat.VisualStudio };
        var (before, _, _) = NativeProject(settings);
        Assert.False(Action(before, "open-solution").IsEnabled);
        Assert.Equal("Generate project files first", Action(before, "open-solution").Tip);

        var sln = _temp.Write("UE5/NightOwl/NightOwl.sln", "");
        var (after, _, _) = NativeProject(settings);

        Assert.True(Action(after, "open-solution").IsEnabled);
        Assert.Equal(sln, Action(after, "open-solution").OpenPath);
        Assert.True(Action(after, "open-solution").OpenWithIde);
    }

    /// <summary>
    /// Rider reads a <c>.uproject</c> itself, so with Rider chosen the action opens the project, with nothing to generate
    /// first, whatever the project file format (GitHub issue #6).
    /// </summary>
    [Theory]
    [InlineData(ToolOS.Windows)]
    [InlineData(ToolOS.MacOS)]
    [InlineData(ToolOS.Linux)]
    public void With_Rider_Open_in_Rider_opens_the_uproject_with_no_project_files_needed(ToolOS os)
    {
        var (info, _, project) = NativeProject(new ProjectToolSettings { OpenSolutionsWith = SolutionOpener.Rider }, os: os);

        var open = Action(info, "open-solution");
        Assert.Equal("Open in Rider", open.Label);
        Assert.True(open.IsEnabled);
        Assert.Equal(project, open.OpenPath);
        Assert.True(open.OpenWithIde);
        Assert.NotNull(open.WithoutIde);
        Assert.Equal("Opens NightOwl.uproject in Rider, which reads the project itself: no project files to generate.", open.Tip);
        Assert.EndsWith("Rider doesn't need them: Open in Rider reads the .uproject itself.", Action(info, "generate-project-files").Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Other_IDEs_open_the_generated_solution_and_fall_back_to_the_OSs_app()
    {
        var (info, _, _) = NativeProject(new ProjectToolSettings { OpenSolutionsWith = SolutionOpener.VisualStudio, ProjectFileFormat = ProjectFileFormat.VisualStudio });

        var open = Action(info, "open-solution");
        Assert.Equal("Open solution", open.Label);
        Assert.Null(open.WithoutIde);
        Assert.DoesNotContain("Rider", Action(info, "generate-project-files").Description, StringComparison.Ordinal);
    }

    /// <summary>Rider reads a .uproject from Unreal Engine 4.25.4 on Windows and 4.26 elsewhere; older engines open the solution.</summary>
    [Theory]
    [InlineData(ToolOS.Windows, 4, 25, 3, false)]
    [InlineData(ToolOS.Windows, 4, 25, 4, true)]
    [InlineData(ToolOS.MacOS, 4, 25, 4, false)]
    [InlineData(ToolOS.MacOS, 4, 26, 0, true)]
    [InlineData(ToolOS.Linux, 4, 26, 2, true)]
    [InlineData(ToolOS.Linux, 5, 4, 2, true)]
    public void Rider_reads_a_uproject_from_the_engines_that_support_it(ToolOS os, int major, int minor, int patch, bool reads)
    {
        Assert.Equal(reads, UnrealProvider.RiderReadsUProject(new UnrealBuildVersion(major, minor, patch, null), os));
        Assert.True(UnrealProvider.RiderReadsUProject(null, os));
    }

    [Fact]
    public void With_Rider_and_an_engine_too_old_for_its_uproject_model_the_solution_opens_as_before()
    {
        var settings = new ProjectToolSettings { OpenSolutionsWith = SolutionOpener.Rider, ProjectFileFormat = ProjectFileFormat.VisualStudio };
        var (info, _, _) = NativeProject(settings, os: ToolOS.Windows, version: (4, 24, 3));

        Assert.Equal("Open solution", Action(info, "open-solution").Label);
        Assert.Equal("Generate project files first", Action(info, "open-solution").Tip);
    }

    [Fact]
    public void A_macOS_workspace_is_a_folder()
    {
        _temp.CreateFolder("UE5/NightOwl/NightOwl (Mac).xcworkspace/contents");

        var (info, _, _) = NativeProject(new ProjectToolSettings { ProjectFileFormat = ProjectFileFormat.Xcode });

        Assert.Equal(_temp.Combine("UE5", "NightOwl", "NightOwl (Mac).xcworkspace"), Action(info, "open-solution").OpenPath);
    }

    [Fact]
    public void Open_latest_log_is_disabled_until_there_is_one()
    {
        var (before, _, _) = NativeProject();
        Assert.False(Action(before, "open-log").IsEnabled);

        var log = _temp.Write("UE5/NightOwl/Saved/Logs/NightOwl.log", "LogInit: hello");
        var (after, _, _) = NativeProject();

        Assert.True(Action(after, "open-log").IsEnabled);
        Assert.Equal(log, Action(after, "open-log").OpenPath);
    }

    [Fact]
    public void Launch_editor_says_when_the_editor_isnt_built_yet()
    {
        ProjectToolFixtures.Engine(_temp, "Source/UE5", installed: false, editor: false);
        var project = ProjectToolFixtures.UProject(_temp, "Source/UE5/Game/Game.uproject", association: "");

        var info = _unreal.Describe(new ProjectCandidate(UnrealProvider.KindId, project, "Game"), ProjectToolFixtures.Context(_temp));

        Assert.False(Action(info, "launch-editor").IsEnabled);
        Assert.Contains("run Build editor first", Action(info, "launch-editor").DisabledReason, StringComparison.Ordinal);
        // Build and launch still works: the build makes the editor.
        Assert.True(Action(info, "build-and-launch").IsEnabled);
        Assert.True(Action(info, "build-and-launch").ThenOnSuccess!.IsEnabled);
    }

    [Fact]
    public void DebugGame_remembered_for_the_project_changes_the_labels_and_commands()
    {
        var project = _temp.Combine("UE5", "NightOwl", "NightOwl.uproject");
        var memory = new DictionaryMemory { Values = { [(project, UnrealProvider.ConfigurationKey)] = "DebugGame" } };

        var (info, _, _) = NativeProject(memory: memory);

        Assert.Equal("Launch editor (DebugGame)", Action(info, "launch-editor").Label);
        Assert.Equal("-debug", Action(info, "launch-editor").Process!.Arguments[^1]);
        Assert.Contains("DebugGame", Action(info, "build-editor").CommandText, StringComparison.Ordinal);
        Assert.Equal("DebugGame", info.Choice!.Selected);
        Assert.Equal(["Development", "DebugGame"], info.Choice.Options.Select(o => o.Value));
    }

    [Fact]
    public void The_default_configuration_comes_from_Settings()
    {
        var (info, _, _) = NativeProject(new ProjectToolSettings { UnrealConfiguration = UnrealConfiguration.DebugGame });

        Assert.Equal("DebugGame", info.Choice!.Selected);
        Assert.Equal("Launch editor (DebugGame)", Action(info, "launch-editor").Label);
    }

    [Fact]
    public void A_missing_engine_disables_the_engine_actions_and_offers_Choose_engine_folder()
    {
        var project = ProjectToolFixtures.UProject(_temp, "Game/Game.uproject", association: "5.9");

        var info = _unreal.Describe(new ProjectCandidate(UnrealProvider.KindId, project, "Game"), ProjectToolFixtures.Context(_temp, ToolOS.Windows));

        Assert.Equal("The engine for EngineAssociation \"5.9\" wasn't found on this machine.", info.Problem);
        Assert.Equal("UE 5.9", info.ShortVersion);
        Assert.Equal("Choose engine folder…", info.Fix!.Label);
        Assert.True(info.Fix.PickFolder);
        foreach (var id in (string[])["launch-editor", "generate-project-files", "build-editor", "build-and-launch"])
        {
            Assert.False(Action(info, id).IsEnabled);
        }
        Assert.Contains("wasn't found", info.SystemPromptNote, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Blueprint_only_project_has_no_project_files_to_generate()
    {
        var (info, _, _) = NativeProject(code: false);

        Assert.False(Action(info, "generate-project-files").IsEnabled);
        // A source build's editor can still be built for it.
        Assert.True(Action(info, "build-editor").IsEnabled);
        Assert.Contains(" UnrealEditor ", CommandLines.Display(Action(info, "build-editor").Process!), StringComparison.Ordinal);
    }

    // ---- Clean intermediates -----------------------------------------------------------------------------------------

    [Fact]
    public void Clean_lists_Binaries_and_Intermediate_of_the_project_and_every_plugin_and_nothing_else()
    {
        var root = _temp.CreateFolder("Game");
        foreach (var folder in (string[])[
            "Binaries/Win64", "Intermediate/Build", "Saved/Logs", "DerivedDataCache/x", "Content/Maps", "Config",
            "Plugins/Top/Binaries", "Plugins/Top/Intermediate", "Plugins/Top/Content",
            "Plugins/Marketplace/Nested/Binaries", "Plugins/Marketplace/Nested/Intermediate/Build",
            "Plugins/NotAPlugin/Binaries",
        ])
        {
            _temp.CreateFolder($"Game/{folder}");
        }
        _temp.Write("Game/Plugins/Top/Top.uplugin", "{}");
        _temp.Write("Game/Plugins/Marketplace/Nested/Nested.uplugin", "{}");

        var folders = UnrealProvider.CleanFolders(root).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'));

        Assert.Equal(["Binaries", "Intermediate", "Plugins/Marketplace/Nested/Binaries", "Plugins/Marketplace/Nested/Intermediate", "Plugins/Top/Binaries", "Plugins/Top/Intermediate"], folders);
    }

    [Fact]
    public void Deleting_the_clean_folders_removes_exactly_those_even_with_read_only_files()
    {
        var root = _temp.CreateFolder("Game");
        _temp.Write("Game/Binaries/Win64/Game.dll", "dll");
        var readOnly = _temp.Write("Game/Intermediate/Build/Makefile.bin", "bin");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        _temp.Write("Game/Plugins/Top/Top.uplugin", "{}");
        _temp.Write("Game/Plugins/Top/Intermediate/x.obj", "obj");
        _temp.Write("Game/Plugins/Top/Content/Mesh.uasset", "asset");
        _temp.Write("Game/Saved/Logs/Game.log", "log");
        _temp.Write("Game/Content/Map.umap", "map");
        _temp.Write("Game/Config/DefaultGame.ini", "ini");
        _temp.Write("Game/Game.uproject", "{}");
        var folders = UnrealProvider.CleanFolders(root);

        Assert.Equal(9, FolderCleaner.Measure(folders, TestContext.Current.CancellationToken));
        foreach (var folder in folders)
        {
            FolderCleaner.Delete(folder, TestContext.Current.CancellationToken);
        }

        var left = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order();
        Assert.Equal(["Config/DefaultGame.ini", "Content/Map.umap", "Game.uproject", "Plugins/Top/Content/Mesh.uasset", "Plugins/Top/Top.uplugin", "Saved/Logs/Game.log"], left);
        Assert.Empty(UnrealProvider.CleanFolders(root));
    }

    [Fact]
    public void Clean_is_destructive_and_disabled_when_there_is_nothing_to_clean()
    {
        var (clean, _, _) = NativeProject();
        Assert.Equal(ProjectActionKind.Destructive, Action(clean, "clean").Kind);
        Assert.False(Action(clean, "clean").IsEnabled);

        _temp.CreateFolder("UE5/NightOwl/Intermediate");
        var (dirty, _, project) = NativeProject();
        var delete = Assert.IsType<DeleteFolders>(Action(dirty, "clean").Destructive);
        Assert.True(Action(dirty, "clean").IsEnabled);
        Assert.Equal(project, delete.ProjectFile);
        Assert.Equal(UnrealProvider.EditorProcessNames, delete.EditorProcessNames);
        Assert.Single(delete.Folders());
    }

    [Fact]
    public void Sizes_read_as_people_say_them()
    {
        Assert.Equal("512 bytes", FolderCleaner.FormatSize(512));
        Assert.Equal("12 KB", FolderCleaner.FormatSize(12 * 1024));
        Assert.Equal("140 MB", FolderCleaner.FormatSize(140L * 1024 * 1024));
        Assert.Equal("3.2 GB", FolderCleaner.FormatSize((long)(3.2 * 1024 * 1024 * 1024)));
    }

    // ---- Kill all Unreal editors ---------------------------------------------------------------------------------------------

    [Fact]
    public void Kill_all_editors_is_disabled_when_none_is_running_and_names_every_editor()
    {
        var engine = ProjectToolFixtures.Engine(_temp, "UE5", installed: false);
        var project = ProjectToolFixtures.UProject(_temp, "UE5/NightOwl/NightOwl.uproject", association: "");
        var processes = new FakeSystemProcesses();
        ProjectInfo Describe() => _unreal.Describe(new ProjectCandidate(UnrealProvider.KindId, project, "NightOwl"), ProjectToolFixtures.Context(_temp, processes: processes));

        var idle = Action(Describe(), "kill-editors");
        Assert.Equal(ProjectActionKind.Destructive, idle.Kind);
        Assert.Equal("No Unreal editor is running", idle.DisabledReason);

        processes.Running.Add(new SystemProcess(10, "UnrealEditor", $"\"{engine}/Engine/Binaries/Linux/UnrealEditor\" \"{project}\" -debug"));
        processes.Running.Add(new SystemProcess(11, "UE4Editor-Cmd", @"C:\UE_4.27\UE4Editor-Cmd.exe D:\Old\Legacy.uproject -run=cook"));
        processes.Running.Add(new SystemProcess(12, "ShaderCompileWorker", "ShaderCompileWorker"));
        var busy = Action(Describe(), "kill-editors");

        Assert.True(busy.IsEnabled);
        var kill = Assert.IsType<KillProcesses>(busy.Destructive);
        Assert.Equal(["UnrealEditor", "UnrealEditor-Cmd", "UE4Editor", "UE4Editor-Cmd"], kill.Names);
        Assert.Equal("Unreal editor", kill.What);
        var found = processes.Find(kill.Names);
        Assert.Equal([10, 11], found.Select(p => p.Pid));
        Assert.Equal(["NightOwl", "Legacy"], found.Select(p => kill.ProjectOf!(p)));
        // Where processes can't be listed, it doesn't guess.
        Assert.True(Action(_unreal.Describe(new ProjectCandidate(UnrealProvider.KindId, project, "NightOwl"), ProjectToolFixtures.Context(_temp)), "kill-editors").IsEnabled);
    }

    [Theory]
    [InlineData(@"UnrealEditor ""D:\My Games\NightOwl\NightOwl.uproject""", ".uproject", "NightOwl")]
    [InlineData("UnrealEditor /home/matt/g/NightOwl.uproject -game", ".uproject", "NightOwl")]
    [InlineData("UnrealEditor -project=D:/g/Owl.uproject", ".uproject", "Owl")]
    [InlineData("UnrealEditor", ".uproject", null)]
    public void The_project_a_process_has_open_is_read_from_its_command_line(string commandLine, string extension, string? project)
    {
        Assert.Equal(project, SystemProcessNames.FileArgument(commandLine, extension));
    }

    [Theory]
    [InlineData(@"Unity.exe -projectPath ""D:\My Games\Owl"" -debugCodeOptimization", "-projectPath", "Owl")]
    [InlineData("Unity -projectPath /home/matt/owl/ -batchmode", "-projectPath", "owl")]
    [InlineData("godot --editor --path /home/matt/Owl Game", "--path", "Owl")]
    [InlineData("godot --editor", "--path", null)]
    public void The_folder_after_an_option_is_read_from_a_command_line(string commandLine, string option, string? folder)
    {
        Assert.Equal(folder, SystemProcessNames.FolderAfter(commandLine, option));
    }

    // ---- The note to Claude ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_note_says_what_the_project_is_where_the_engine_is_and_the_exact_commands()
    {
        var project = UnrealProject.Read(ProjectToolFixtures.UProject(_temp, "NightOwl/NightOwl.uproject"));
        var engine = new UnrealEngine(@"C:\Program Files\Epic Games\UE_5.4", UnrealEngineKind.Launcher, new UnrealBuildVersion(5, 4, 2, null));
        project = project with { Path = @"D:\Games\NightOwl\NightOwl.uproject" };

        var note = UnrealProvider.SystemPromptNote(project, engine, "5.4", UnrealConfiguration.Development, ProjectFileFormat.VisualStudio, ToolOS.Windows);

        Assert.Equal(
            """
            This is an Unreal Engine 5.4 project, NightOwl, at D:\Games\NightOwl\NightOwl.uproject.
            The engine is at C:\Program Files\Epic Games\UE_5.4 (a launcher install).
            To build the editor, run: "C:\Program Files\Epic Games\UE_5.4\Engine\Build\BatchFiles\Build.bat" NightOwlEditor Win64 Development -Project="D:\Games\NightOwl\NightOwl.uproject" -WaitMutex
            To regenerate project files, run: "C:\Program Files\Epic Games\UE_5.4\Engine\Build\BatchFiles\Build.bat" -projectfiles -project="D:\Games\NightOwl\NightOwl.uproject" -game -progress
            Don't start the editor or packaging unless asked.
            """.ReplaceLineEndings("\n"), note);
        Assert.True(note.Length < 650, $"The note is {note.Length} characters.");
    }

    [Fact]
    public void The_note_is_left_out_when_Settings_says_so()
    {
        Assert.NotNull(NativeProject().Info.SystemPromptNote);
        Assert.Null(NativeProject(new ProjectToolSettings { TellClaudeAboutUnreal = false }).Info.SystemPromptNote);
    }
}
