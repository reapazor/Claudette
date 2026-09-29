using Claudette.Core.ProjectTools;
using Claudette.Core.ProjectTools.Unity;
using Claudette.Core.Settings;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.ProjectTools;

/// <summary>Unity projects (DESIGN.md §18, "Unity"), in temporary folders. No real Unity is ever run.</summary>
public sealed class UnityProviderTests : IDisposable
{
    private const string Version = "2022.3.20f1";

    private readonly TempFolder _temp = new("claudette-unity");
    private readonly UnityProvider _unity = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>A Unity project with its version, settings and IDE packages.</summary>
    private string Project(string relative = "NightOwl", string? packages = null)
    {
        _temp.Write($"{relative}/ProjectSettings/ProjectVersion.txt", $"m_EditorVersion: {Version}\nm_EditorVersionWithRevision: {Version} (61c2feb0970d)\n");
        _temp.Write($"{relative}/ProjectSettings/ProjectSettings.asset", """
            %YAML 1.1
            %TAG !u! tag:unity3d.com,2011:
            --- !u!129 &1
            PlayerSettings:
              m_ObjectHideFlags: 0
              companyName: Owl Works
              productName: Night Owl
              defaultCursor: {fileID: 0}
            """);
        _temp.Write($"{relative}/Packages/manifest.json", packages ?? """{ "dependencies": { "com.jetbrains.rider": "3.0.28", "com.unity.ide.visualstudio": "2.0.22", "com.unity.test-framework": "1.1.33" } }""");
        return _temp.CreateFolder($"{relative}/Assets").Replace($"{Path.DirectorySeparatorChar}Assets", "");
    }

    /// <summary>A Hub-installed editor for <paramref name="os"/>, laid out as Hub does it.</summary>
    private string HubEditor(ToolOS os, string version = Version) => os switch
    {
        ToolOS.Windows => _temp.Write($"programfiles/Unity/Hub/Editor/{version}/Editor/Unity.exe", ""),
        ToolOS.MacOS => _temp.Write($"applications/Unity/Hub/Editor/{version}/Unity.app/Contents/MacOS/Unity", ""),
        _ => _temp.Write($"home/Unity/Hub/Editor/{version}/Editor/Unity", ""),
    };

    private ProjectToolContext Context(ToolOS os = ToolOS.Linux, ISystemProcesses? processes = null, IProjectMemory? memory = null, ProjectToolSettings? settings = null) => new()
    {
        OS = os,
        Settings = settings ?? new ProjectToolSettings(),
        Paths = new ProjectToolPaths(_temp.Combine("home"), _temp.Combine("appdata"), _temp.Combine("localappdata"), null, _temp.Combine("programfiles"), _temp.Combine("applications")),
        Memory = memory ?? NoProjectMemory.Instance,
        Processes = processes,
        JobsDirectory = _temp.Combine("jobs"),
    };

    private ProjectInfo Describe(string root, ProjectToolContext context) =>
        _unity.Describe(new ProjectCandidate(UnityProvider.KindId, root, Path.GetFileName(root)), context);

    private static ProjectAction Action(ProjectInfo info, string id) => info.Actions.Single(a => a.Id == id);

    // ---- Detecting ------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_Unity_project_needs_ProjectVersion_txt_and_Assets()
    {
        _temp.CreateFolder("repo/.git");
        var root = Project("repo/Game/NightOwl");
        _temp.Write("repo/NotUnity/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 2021.3.1f1");

        Assert.Equal([root], _unity.Find(_temp.Combine("repo"), Context()).Select(c => c.Path));
        Assert.Equal([root], _unity.Find(_temp.CreateFolder("repo/Game/NightOwl/Assets/Scripts"), Context()).Select(c => c.Path));
    }

    [Fact]
    public void The_version_revision_and_product_are_read()
    {
        Assert.Equal(("2022.3.20f1", "61c2feb0970d"), UnityProject.ParseProjectVersion("m_EditorVersion: 2022.3.20f1\r\nm_EditorVersionWithRevision: 2022.3.20f1 (61c2feb0970d)\r\n"));
        Assert.Equal(("6000.0.23f1", null), UnityProject.ParseProjectVersion("m_EditorVersion: 6000.0.23f1"));

        var project = UnityProject.Read(Project());

        Assert.Equal(("NightOwl", "Night Owl", "Owl Works"), (project.Name, project.ProductName, project.CompanyName));
        Assert.Equal(["com.jetbrains.rider", "com.unity.ide.visualstudio"], project.IdePackages);
        Assert.Equal("2022.3", UnityProvider.ShortVersion(Version));
    }

    // ---- Finding the editor ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ToolOS.Windows)]
    [InlineData(ToolOS.MacOS)]
    [InlineData(ToolOS.Linux)]
    public void The_editor_is_found_where_Unity_Hub_installs_it(ToolOS os)
    {
        var editor = HubEditor(os);

        var info = Describe(Project(), Context(os));

        Assert.Null(info.Problem);
        Assert.Equal(editor, Action(info, "open-in-unity").Process!.FileName);
        Assert.Equal("Unity 2022.3", info.ShortVersion);
    }

    [Fact]
    public void Hubs_custom_install_folder_and_its_lists_of_editors_are_read()
    {
        var root = Project();
        var secondary = _temp.Write($"D/Unity Editors/{Version}/Editor/Unity", "");
        _temp.Write("home/.config/UnityHub/secondaryInstallPath.json", $"\"{_temp.Combine("D", "Unity Editors").Replace("\\", "\\\\")}\"");
        Assert.Equal(secondary, UnityEditors.Find(UnityProject.Read(root), Context()));

        File.Delete(secondary);
        var added = _temp.Write("elsewhere/2022.3.20f1/Editor/Unity", "");
        _temp.Write("home/.config/UnityHub/editors-v2.json", $$"""
            { "schema_version": "v2", "data": [
                { "version": "2021.3.1f1", "location": ["/nowhere/Unity"], "manual": true },
                { "version": "{{Version}}", "location": ["{{_temp.Combine("elsewhere", Version).Replace("\\", "\\\\")}}"], "manual": true },
            ] }
            """);
        Assert.Equal(added, UnityEditors.Find(UnityProject.Read(root), Context()));

        File.Delete(Path.Combine(_temp.Path, "home", ".config", "UnityHub", "editors-v2.json"));
        var old = _temp.Write("old/Unity", "");
        _temp.Write("home/.config/UnityHub/editors.json", $$"""{ "{{Version}}": { "version": "{{Version}}", "location": "{{old.Replace("\\", "\\\\")}}", "manual": true } }""");
        Assert.Equal(old, UnityEditors.Find(UnityProject.Read(root), Context()));
    }

    [Fact]
    public void A_version_that_isnt_installed_says_so_and_offers_the_pick_which_is_remembered()
    {
        HubEditor(ToolOS.Linux, "2021.3.1f1");
        var root = Project();

        var missing = Describe(root, Context());

        Assert.Equal("Unity 2022.3.20f1 isn't installed, or Unity Hub doesn't list it. Choose its editor in this menu.", missing.Problem);
        Assert.Equal("Choose Unity editor…", missing.Fix!.Label);
        Assert.False(missing.Fix.PickFolder);
        Assert.False(Action(missing, "open-in-unity").IsEnabled);
        Assert.Null(missing.Fix.Validate(_temp.Write("notes.txt", "")).Path);

        var picked = _temp.Write("Custom/Unity 2022/Editor/Unity", "");
        Assert.Equal(picked, missing.Fix.Validate(_temp.Combine("Custom", "Unity 2022")).Path);
        var memory = new DictionaryMemory { Values = { [(root, UnityEditors.EditorKey)] = picked } };
        Assert.Equal(picked, Action(Describe(root, Context(memory: memory)), "open-in-unity").Process!.FileName);
    }

    // ---- Commands ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Open_in_Unity_is_detached_and_Debug_adds_debugCodeOptimization()
    {
        var editor = HubEditor(ToolOS.Linux);
        var root = Project();

        var release = Action(Describe(root, Context()), "open-in-unity");
        var memory = new DictionaryMemory { Values = { [(root, UnityProvider.OptimizationKey)] = "Debug" } };
        var debug = Describe(root, Context(memory: memory));

        Assert.Equal((editor, true), (release.Process!.FileName, release.Process.Detached));
        Assert.Equal(["-projectPath", root], release.Process.Arguments);
        Assert.Equal(["-projectPath", root, "-debugCodeOptimization"], Action(debug, "open-in-unity").Process!.Arguments);
        Assert.Equal("Open in Unity (Debug)", Action(debug, "open-in-unity").Label);
        Assert.Equal("Debug", debug.Choice!.Selected);
        Assert.Equal("Debug", Describe(root, Context(settings: new ProjectToolSettings { UnityCodeOptimization = UnityCodeOptimization.Debug })).Choice!.Selected);
    }

    [Fact]
    public void EditMode_tests_run_in_batch_mode_and_report_the_results()
    {
        HubEditor(ToolOS.Linux);
        var root = Project();

        var tests = Action(Describe(root, Context()), "run-editmode-tests");

        Assert.Equal(ProjectActionKind.Run, tests.Kind);
        var results = tests.ResultFile!;
        Assert.StartsWith(_temp.Combine("jobs"), results, StringComparison.Ordinal);
        Assert.Equal(["-batchmode", "-projectPath", root, "-runTests", "-testPlatform", "EditMode", "-testResults", results, "-logFile", "-"], tests.Process!.Arguments);
        Assert.Null(tests.Summarize!(0));
        _temp.Write(Path.GetRelativePath(_temp.Path, results).Replace('\\', '/'),
            """<?xml version="1.0"?><test-run id="2" testcasecount="14" result="Failed(Child)" total="14" passed="11" failed="1" inconclusive="0" skipped="2"></test-run>""");
        Assert.Equal("11 passed, 1 failed, 2 skipped.", tests.Summarize(2));
    }

    [Theory]
    [InlineData("""{ "dependencies": { "com.jetbrains.rider": "3.0.28" } }""", SolutionOpener.System, "Packages.Rider.Editor.RiderScriptEditor.SyncSolution")]
    [InlineData("""{ "dependencies": { "com.unity.ide.visualstudio": "2.0.22" } }""", SolutionOpener.Rider, "Microsoft.Unity.VisualStudio.Editor.VisualStudioEditor.SyncAll")]
    [InlineData("""{ "dependencies": { "com.jetbrains.rider": "3", "com.unity.ide.visualstudio": "2" } }""", SolutionOpener.Rider, "Packages.Rider.Editor.RiderScriptEditor.SyncSolution")]
    [InlineData("""{ "dependencies": { "com.jetbrains.rider": "3", "com.unity.ide.visualstudio": "2" } }""", SolutionOpener.VSCode, "Microsoft.Unity.VisualStudio.Editor.VisualStudioEditor.SyncAll")]
    [InlineData("""{ "dependencies": { "com.unity.ide.vscode": "1.2", "com.unity.ide.visualstudio": "2" } }""", SolutionOpener.VSCode, "Microsoft.Unity.VisualStudio.Editor.VisualStudioEditor.SyncAll")]
    public void Regenerating_the_solution_runs_the_IDE_packages_method(string manifest, SolutionOpener opener, string method)
    {
        HubEditor(ToolOS.Linux);
        var root = Project(packages: manifest);

        var sync = Action(Describe(root, Context(settings: new ProjectToolSettings { OpenSolutionsWith = opener })), "regenerate-solution");

        Assert.Equal(["-batchmode", "-quit", "-projectPath", root, "-executeMethod", method, "-logFile", "-"], sync.Process!.Arguments);
    }

    [Fact]
    public void Without_an_IDE_package_there_is_no_solution_to_regenerate()
    {
        HubEditor(ToolOS.Linux);

        var sync = Action(Describe(Project(packages: """{ "dependencies": { "com.unity.ide.vscode": "1.2" } }"""), Context()), "regenerate-solution");

        Assert.False(sync.IsEnabled);
        Assert.Contains("no IDE package", sync.DisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_solution_and_logs_are_disabled_until_they_exist()
    {
        HubEditor(ToolOS.Linux);
        var root = Project();
        var before = Describe(root, Context());
        Assert.Equal("Regenerate the C# solution first", Action(before, "open-solution").DisabledReason);
        Assert.False(Action(before, "open-editor-log").IsEnabled);
        Assert.False(Action(before, "open-player-log").IsEnabled);

        _temp.Write("NightOwl/NightOwl.sln", "");
        _temp.Write("home/.config/unity3d/Editor.log", "");
        _temp.Write("home/.config/unity3d/Owl Works/Night Owl/Player.log", "");
        var after = Describe(root, Context());

        Assert.True(Action(after, "open-solution").IsEnabled);
        Assert.True(Action(after, "open-solution").OpenWithIde);
        Assert.True(Action(after, "open-editor-log").IsEnabled);
        Assert.True(Action(after, "open-player-log").IsEnabled);
    }

    [Fact]
    public void The_logs_are_where_each_OS_keeps_them()
    {
        var paths = new ProjectToolPaths(@"C:\Users\matt", LocalAppData: @"C:\Users\matt\AppData\Local");

        Assert.Equal(@"C:\Users\matt\AppData\Local\Unity\Editor\Editor.log", UnityEditors.EditorLog(paths, ToolOS.Windows));
        Assert.Equal(@"C:\Users\matt\AppData\LocalLow\Owl Works\Night Owl\Player.log", UnityEditors.PlayerLog(paths, ToolOS.Windows, "Owl Works", "Night Owl"));
        var mac = new ProjectToolPaths("/Users/matt");
        Assert.Equal("/Users/matt/Library/Logs/Unity/Editor.log", UnityEditors.EditorLog(mac, ToolOS.MacOS));
        Assert.Equal("/Users/matt/Library/Logs/Owl Works/Night Owl/Player.log", UnityEditors.PlayerLog(mac, ToolOS.MacOS, "Owl Works", "Night Owl"));
        var linux = new ProjectToolPaths("/home/matt");
        Assert.Equal("/home/matt/.config/unity3d/Editor.log", UnityEditors.EditorLog(linux, ToolOS.Linux));
        Assert.Equal("/home/matt/.config/unity3d/Owl Works/Night Owl/Player.log", UnityEditors.PlayerLog(linux, ToolOS.Linux, "Owl Works", "Night Owl"));
    }

    // ---- The lock file ---------------------------------------------------------------------------------------------------

    [Fact]
    public void With_the_project_open_in_Unity_the_actions_that_need_it_closed_are_refused()
    {
        var editor = HubEditor(ToolOS.Linux);
        var root = Project();
        _temp.CreateFolder("NightOwl/Library");
        _temp.Write("NightOwl/Temp/UnityLockfile", "");
        var processes = new FakeSystemProcesses { Running = { new SystemProcess(7, "Unity", $"{editor} -projectpath {root} -useHub") } };

        var open = Describe(root, Context(processes: processes));

        Assert.Equal("Unity has this project open", Action(open, "open-in-unity").Label);
        Assert.False(Action(open, "open-in-unity").IsEnabled);
        foreach (var id in (string[])["run-editmode-tests", "regenerate-solution", "clean-library"])
        {
            Assert.StartsWith("Unity has this project open, and locks it", Action(open, id).DisabledReason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_lock_file_left_by_a_crash_doesnt_count_but_without_a_process_list_it_does()
    {
        HubEditor(ToolOS.Linux);
        var root = Project();
        _temp.Write("NightOwl/Temp/UnityLockfile", "");
        var processes = new FakeSystemProcesses { Running = { new SystemProcess(8, "Unity", "Unity -projectPath /another/Game") } };

        Assert.True(Action(Describe(root, Context(processes: processes)), "open-in-unity").IsEnabled);
        Assert.False(Action(Describe(root, Context(processes: null)), "open-in-unity").IsEnabled);
        File.Delete(_temp.Combine("NightOwl", "Temp", "UnityLockfile"));
        Assert.True(Action(Describe(root, Context(processes: null)), "open-in-unity").IsEnabled);
    }

    // ---- Clean and kill ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Clean_Library_deletes_Library_Temp_and_obj_and_nothing_else()
    {
        HubEditor(ToolOS.Linux);
        var root = Project();
        foreach (var file in (string[])["Library/ArtifactDB", "Temp/x", "obj/Debug/a.dll", "Logs/AssetImportWorker0.log", "UserSettings/Layouts.dwlt", "Assets/Player.cs", "Packages/manifest.json"])
        {
            _temp.Write($"NightOwl/{file}", "x");
        }

        var clean = Action(Describe(root, Context()), "clean-library");
        var delete = Assert.IsType<DeleteFolders>(clean.Destructive);
        Assert.Contains("reimports every asset", delete.Warning, StringComparison.Ordinal);
        foreach (var folder in delete.Folders())
        {
            FolderCleaner.Delete(folder, TestContext.Current.CancellationToken);
        }

        Assert.Equal(["Assets", "Logs", "Packages", "ProjectSettings", "UserSettings"], Directory.EnumerateDirectories(root).Select(Path.GetFileName).Order());
        Assert.False(Action(Describe(root, Context()), "clean-library").IsEnabled);
    }

    [Fact]
    public void Kill_all_Unity_editors_leaves_Unity_Hub_alone()
    {
        HubEditor(ToolOS.Linux);
        var processes = new FakeSystemProcesses();
        var root = Project();
        Assert.Equal("No Unity editor is running", Action(Describe(root, Context(processes: processes)), "kill-editors").DisabledReason);

        processes.Running.Add(new SystemProcess(1, "Unity Hub", "Unity Hub"));
        processes.Running.Add(new SystemProcess(2, "UnityShaderCompiler", "UnityShaderCompiler"));
        Assert.False(Action(Describe(root, Context(processes: processes)), "kill-editors").IsEnabled);

        processes.Running.Add(new SystemProcess(3, "Unity", @"Unity.exe -projectPath ""D:\My Games\Owl"""));
        var kill = Action(Describe(root, Context(processes: processes)), "kill-editors");
        Assert.True(kill.IsEnabled);
        var destructive = Assert.IsType<KillProcesses>(kill.Destructive);
        Assert.Equal("Owl", destructive.ProjectOf!(processes.Find(destructive.Names).Single()));
    }

    // ---- The note to Claude -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_note_gives_the_version_the_test_command_and_the_rules()
    {
        var project = new UnityProject("/g/NightOwl", "NightOwl", Version, null, null, null, []);

        var note = UnityProvider.SystemPromptNote(project, "/opt/Unity/Hub/Editor/2022.3.20f1/Editor/Unity", ToolOS.Linux);

        Assert.Equal(
            """
            This is a Unity 2022.3.20f1 project, NightOwl, at /g/NightOwl.
            The editor is at /opt/Unity/Hub/Editor/2022.3.20f1/Editor/Unity.
            To run the EditMode tests, with the editor closed, run: /opt/Unity/Hub/Editor/2022.3.20f1/Editor/Unity -batchmode -projectPath /g/NightOwl -runTests -testPlatform EditMode -testResults /g/NightOwl/Logs/EditModeResults.xml -logFile -
            Library/, Temp/ and obj/ are generated: don't edit them.
            A .meta file must move and be renamed with its asset.
            Don't open the editor unless asked.
            """.ReplaceLineEndings("\n"), note);
        HubEditor(ToolOS.Linux);
        Assert.Null(Describe(Project(), Context(settings: new ProjectToolSettings { TellClaudeAboutUnity = false })).SystemPromptNote);
    }
}
