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
    private (ProjectInfo Info, string Engine, string Project) NativeProject(ProjectToolSettings? settings = null, IProjectMemory? memory = null, bool code = true)
    {
        var engine = ProjectToolFixtures.Engine(_temp, "UE5", installed: false);
        var project = ProjectToolFixtures.UProject(_temp, "UE5/NightOwl/NightOwl.uproject", association: "", code: code);
        var context = ProjectToolFixtures.Context(_temp, settings: settings, memory: memory);
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
        Assert.Equal(["launch-editor", "generate-project-files", "build-editor", "build-and-launch", "open-solution", "open-log", "clean"], info.Actions.Select(a => a.Id));
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
