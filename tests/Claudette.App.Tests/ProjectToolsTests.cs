using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Diffs;
using Claudette.Core.ProjectTools;
using Claudette.Core.ProjectTools.Unreal;
using Claudette.Core.Settings;
using Claudette.Testing;

namespace Claudette.App.Tests;

/// <summary>
/// Project tools in a tab (DESIGN.md §18): the chip, its menu, the Project page, jobs, custom actions and the note to
/// Claude. The "engine" is a few files in a temporary folder, and every process is a fake.
/// </summary>
public class ProjectToolsTests
{
    /// <summary>
    /// A tab folder holding NightOwl.uproject, with an engine in the folder above it (an empty EngineAssociation), and
    /// processes the test controls.
    /// </summary>
    internal static (TabTestHarness H, FakeLauncher Launcher, string UProject) UnrealHarness(Action<AppSettings>? configure = null, bool code = true)
    {
        var launcher = new FakeLauncher();
        var h = new TabTestHarness(configure, launcher: launcher);
        var uproject = WriteUnrealProject(h.Root, h.WorkFolder, code);
        return (h, launcher, uproject);
    }

    internal static string WriteUnrealProject(string engineRoot, string folder, bool code = true) => UnrealFixture.Write(engineRoot, folder, code);

    private static void Write(string path, string text) => UnrealFixture.WriteFile(path, text);

    private static async Task<TabViewModel> OpenWithProjectAsync(TabTestHarness h)
    {
        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => tab.Project is not null, "the project");
        return tab;
    }

    private static ProjectAction Action(TabViewModel tab, string id) => InlineDispatcher.Read(() => tab.ProjectActions.Single(a => a.Id == id));

    private static ProjectMenuEntry Entry(TabViewModel tab, string label) => InlineDispatcher.Read(() => tab.ProjectMenu.Single(e => e.Label == label));

    /// <summary>The newest run: the one the last job made.</summary>
    internal static ProjectRunViewModel? LastRun(TabViewModel tab) => InlineDispatcher.Read(() => tab.ProjectRuns.LastOrDefault());

    // ---- The chip and its menu --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_detected_project_gets_a_chip_with_its_name_and_engine_version()
    {
        var (h, _, uproject) = UnrealHarness();
        await using var _h = h;

        var tab = await OpenWithProjectAsync(h);

        Assert.True(tab.HasProjectTools);
        Assert.False(tab.OffersFirstProjectAction);
        Assert.Equal("NightOwl · UE 5.4", tab.ProjectButtonText);
        Assert.Equal("NightOwl (UE 5.4)", tab.ProjectMenuTitle);
        Assert.Equal("NightOwl · Unreal Engine", tab.ProjectHeaderTitle);
        Assert.Equal(["Unreal Engine 5.4.2 · engine in a parent folder", h.Root], tab.ProjectHeaderLines);
        Assert.Contains(tab.ProjectDetails, d => d.Label == "Project" && d.Value == uproject);
        Assert.Contains("Ctrl+Shift+E: Launch editor", tab.ProjectButtonTip.Replace("⇧⌘E", "Ctrl+Shift+E"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_project_or_actions_there_is_no_chip_and_the_tab_menu_offers_the_first_action()
    {
        await using var h = new TabTestHarness();

        var tab = await h.OpenTabAsync();

        Assert.False(tab.HasProjectTools);
        Assert.True(tab.OffersFirstProjectAction);
        Assert.Null(tab.Project);
        Assert.DoesNotContain(tab.InfoRows, r => r.Label == "Project");
    }

    [Fact]
    public async Task The_menu_lists_the_actions_then_the_configuration_then_the_menus_own_commands()
    {
        var (h, _, _) = UnrealHarness(s => s.ProjectTools.ProjectFileFormat = ProjectFileFormat.VisualStudio);
        await using var _h = h;
        var tab = await OpenWithProjectAsync(h);

        var menu = InlineDispatcher.Read(() => tab.ProjectMenu.Select(e => e.IsSeparator ? "-" : e.Label).ToArray());

        Assert.Equal(
        [
            "Launch editor", "Generate project files", "Build editor", "Build and launch", "Open solution", "Open latest log", "Clean intermediates…", "Kill all Unreal editors…",
            "-", "Editor configuration", "Development", "DebugGame",
            "-", "Choose another engine folder…",
            "-", "Show output…", "Add an action…", "Refresh",
        ], menu);
        Assert.True(Entry(tab, "Development").IsChecked);
        Assert.True(Entry(tab, "Development").IsOption);
        Assert.True(Entry(tab, "Editor configuration").IsHeader);
        Assert.False(Entry(tab, "Editor configuration").IsMenuEnabled);
    }

    [Fact]
    public async Task Open_solution_is_disabled_until_there_is_a_solution()
    {
        var (h, _, _) = UnrealHarness(s => s.ProjectTools.ProjectFileFormat = ProjectFileFormat.VisualStudio);
        await using var _h = h;
        var tab = await OpenWithProjectAsync(h);

        Assert.False(Entry(tab, "Open solution").IsEnabled);
        Assert.Equal("Generate project files first", Entry(tab, "Open solution").Tip);

        File.WriteAllText(Path.Combine(h.WorkFolder, "NightOwl.sln"), "");
        await tab.RefreshProjectCommand.ExecuteAsync(null);

        Assert.True(Entry(tab, "Open solution").IsEnabled);
        await tab.RunProjectActionCommand.ExecuteAsync(Action(tab, "open-solution"));
        Assert.Equal([Path.Combine(h.WorkFolder, "NightOwl.sln")], h.Platform.OpenedFiles);
    }

    /// <summary>With Rider chosen, <b>Open in Rider</b> gives Rider the .uproject, with no solution needed (GitHub issue #6).</summary>
    [Fact]
    public async Task Open_in_Rider_opens_the_uproject_in_Rider_without_a_solution()
    {
        var (h, launcher, uproject) = UnrealHarness(s => s.ProjectTools.OpenSolutionsWith = SolutionOpener.Rider);
        await using var _h = h;
        h.Services.ProjectTools.Probe = new RiderProbe(installed: true);
        var tab = await OpenWithProjectAsync(h);

        Assert.True(Entry(tab, "Open in Rider").IsEnabled);
        Assert.DoesNotContain(tab.ProjectMenu, e => e.Label == "Open solution");
        await tab.RunProjectActionCommand.ExecuteAsync(Action(tab, "open-solution"));

        Assert.Contains(uproject, launcher.Started.Last().Arguments);
        Assert.Empty(h.Platform.OpenedFiles);
    }

    /// <summary>
    /// Without Rider, the .uproject isn't handed to the OS's app, which would start the Unreal editor: a note says why.
    /// </summary>
    [Fact]
    public async Task Open_in_Rider_without_Rider_says_so_rather_than_starting_the_editor()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "On macOS, open -a finds Rider itself, so Claudette never decides it's missing.");
        var (h, launcher, _) = UnrealHarness(s => s.ProjectTools.OpenSolutionsWith = SolutionOpener.Rider);
        await using var _h = h;
        h.Services.ProjectTools.Probe = new RiderProbe(installed: false);
        var tab = await OpenWithProjectAsync(h);
        var started = launcher.Started.Count;

        await tab.RunProjectActionCommand.ExecuteAsync(Action(tab, "open-solution"));

        Assert.Empty(h.Platform.OpenedFiles);
        Assert.Equal(started, launcher.Started.Count);
        Assert.Contains(tab.Items, i => i is Conversation.NoteItem note && note.Text.StartsWith("Rider wasn't found, so the project wasn't opened", StringComparison.Ordinal));
    }

    /// <summary>Finds Rider on the PATH, or nothing, whichever OS runs the test.</summary>
    private sealed class RiderProbe(bool installed) : IFileProbe
    {
        public bool FileExists(string path) => installed && Path.GetFileName(path).StartsWith("rider", StringComparison.OrdinalIgnoreCase);

        public string? FindOnPath(string fileName) =>
            installed && fileName.StartsWith("rider", StringComparison.OrdinalIgnoreCase) ? Path.Combine(Path.GetTempPath(), "rider", "bin", fileName) : null;

        public string ExpandEnvironmentVariables(string path) => path;
    }

    [Fact]
    public async Task The_configuration_is_chosen_per_project_and_remembered_on_this_machine()
    {
        var (h, launcher, uproject) = UnrealHarness();
        await using var _h = h;
        var tab = await OpenWithProjectAsync(h);

        await tab.ChooseProjectOptionCommand.ExecuteAsync(new ProjectChoiceOption("DebugGame", "DebugGame"));

        Assert.Equal("DebugGame", h.Services.State.ProjectTools.Get(uproject, UnrealProvider.ConfigurationKey));
        Assert.True(Entry(tab, "DebugGame").IsChecked);
        Assert.Equal("Launch editor (DebugGame)", Action(tab, "launch-editor").Label);
        await tab.RunProjectActionCommand.ExecuteAsync(Action(tab, "launch-editor"));
        var launch = launcher.Started.Last();
        Assert.Equal(UnrealCommands.EditorPath(h.Root, "UnrealEditor", ToolOSExtensions.Current), launch.FileName);
        Assert.Equal([uproject, "-debug"], launch.Arguments);
        Assert.True(launch.Detached);
        Assert.NotNull(launch.Environment);

        // Another tab in the same folder uses the project's choice; Settings' default is only for projects without one.
        var other = new TabViewModel(h.Services, h.Shell, new TabState { Folder = h.WorkFolder }, isRestored: false);
        await other.RefreshProjectAsync();
        Assert.Equal("DebugGame", other.Project!.Choice!.Selected);
        Assert.Equal(UnrealConfiguration.Development, h.Services.Settings.ProjectTools.UnrealConfiguration);
    }

    [Fact]
    public async Task The_main_action_shortcut_launches_the_editor()
    {
        var (h, launcher, uproject) = UnrealHarness();
        await using var _h = h;
        var tab = await OpenWithProjectAsync(h);

        Assert.Equal("Primary+Shift+E", KeyboardShortcuts.Resolve(h.Services.Settings.Keyboard, KeyboardShortcuts.RunProjectAction)!.ToString());
        await tab.RunMainProjectActionCommand.ExecuteAsync(null);

        Assert.Equal([uproject], launcher.Started.Last().Arguments);
    }

    // ---- Telling Claude ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_session_gets_a_note_about_the_project_and_the_info_card_says_so()
    {
        var (h, _, uproject) = UnrealHarness();
        await using var _h = h;

        var tab = await OpenWithProjectAsync(h);

        var note = h.Factory.Launches.Single().AppendSystemPrompt!;
        Assert.StartsWith($"This is an Unreal Engine 5.4 project, NightOwl, at {uproject}.", note, StringComparison.Ordinal);
        Assert.Contains("To build the editor, run: ", note, StringComparison.Ordinal);
        Assert.Contains("NightOwlEditor", note, StringComparison.Ordinal);
        Assert.EndsWith("Don't start the editor or packaging unless asked.", note, StringComparison.Ordinal);
        Assert.Contains(tab.InfoRows, r => r.Label == "Project" && r.Value.EndsWith("Claude was told how to build it.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_note_when_Settings_turns_it_off()
    {
        var (h, _, _) = UnrealHarness(s => s.ProjectTools.TellClaudeAboutUnreal = false);
        await using var _h = h;

        var tab = await OpenWithProjectAsync(h);

        Assert.Null(h.Factory.Launches.Single().AppendSystemPrompt);
        Assert.DoesNotContain("told", tab.InfoRows.Single(r => r.Label == "Project").Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_project_note_comes_first_and_Perforces_after_it()
    {
        var p4 = new FakeP4();
        await using var h = new TabTestHarness(s => s.Perforce.Enabled = true, launcher: p4);
        p4.Time = h.Time;
        p4.Root = h.WorkFolder;
        p4.TicketExpires = h.Time.GetUtcNow().AddHours(11);
        WriteUnrealProject(h.Root, h.WorkFolder);

        await OpenWithProjectAsync(h);

        var prompt = h.Factory.Launches.Single().AppendSystemPrompt!;
        var parts = prompt.Split("\n\n");
        Assert.Equal(2, parts.Length);
        Assert.StartsWith("This is an Unreal Engine 5.4 project", parts[0], StringComparison.Ordinal);
        Assert.Contains("Perforce workspace", parts[1], StringComparison.Ordinal);
    }

    // ---- Jobs ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_jobs_output_reaches_the_Project_page_and_its_exit_code_says_how_it_went()
    {
        var (h, launcher, _) = UnrealHarness();
        await using var _h = h;
        var tab = await OpenWithProjectAsync(h);

        await tab.RunProjectActionCommand.ExecuteAsync(Action(tab, "build-editor"));
        var spec = launcher.Started.Last();
        var process = launcher.Processes.Last();

        Assert.True(spec.TrackProcessTree);
        Assert.False(spec.Detached);
        Assert.NotNull(spec.Environment);
        Assert.True(tab.IsProjectJobRunning);
        Assert.Equal("Build editor…", tab.ProjectButtonText);
        Assert.False(Entry(tab, "Generate project files").IsEnabled);
        Assert.Contains("Stop it first", Entry(tab, "Generate project files").Tip, StringComparison.Ordinal);
        var build = LastRun(tab)!;
        Assert.Same(build, tab.SelectedProjectRun);
        process.WriteOutput("Building NightOwlEditor...");
        process.WriteError("warning: deprecated");
        await TabTestHarness.Eventually(() => build.Output.Contains("warning: deprecated"), "the output");
        Assert.StartsWith("$ ", build.Output[0], StringComparison.Ordinal);

        process.Exit(0);
        await TabTestHarness.Eventually(() => !tab.IsProjectJobRunning, "the end");

        Assert.Equal("Build editor succeeded.", build.Status);
        Assert.False(build.Failed);
        Assert.Equal("NightOwl · UE 5.4", tab.ProjectButtonText);

        await tab.RunProjectActionCommand.ExecuteAsync(Action(tab, "generate-project-files"));
        launcher.Processes.Last().Exit(6);
        var generate = LastRun(tab)!;
        await TabTestHarness.Eventually(() => generate.State == ProjectJobState.Failed, "the failure");

        Assert.Equal("Generate project files failed (exit code 6).", generate.Status);
        Assert.True(generate.Failed);
        Assert.DoesNotContain("Building NightOwlEditor...", generate.Output);
    }

    [Fact]
    public async Task Stop_ends_the_jobs_whole_process_tree()
    {
        var (h, launcher, _) = UnrealHarness();
        await using var _h = h;
        var tab = await OpenWithProjectAsync(h);
        await tab.RunProjectActionCommand.ExecuteAsync(Action(tab, "build-editor"));
        var process = launcher.Processes.Last();

        var run = tab.SelectedProjectRun!;

        Assert.True(run.StopCommand.CanExecute(null));
        run.StopCommand.Execute(null);
        await TabTestHarness.Eventually(() => run.State == ProjectJobState.Stopped, "the stop");

        Assert.True(h.Trees.Trees[process.Id].Killed);
        Assert.True(process.Killed);
        Assert.Equal("Build editor was stopped.", run.Status);
        Assert.False(run.StopCommand.CanExecute(null));
        Assert.Null(h.Notifier.Last("ProjectAction:"));
    }

    [Fact]
    public async Task A_finished_job_notifies_only_while_Claudette_isnt_in_front_and_a_click_goes_to_its_tab()
    {
        var (h, launcher, _) = UnrealHarness();
        await using var _h = h;
        var tab = await OpenWithProjectAsync(h);
        NotificationTarget? clicked = null;
        h.Services.Notifications.Activated += target => clicked = target;

        await tab.RunProjectActionCommand.ExecuteAsync(Action(tab, "build-editor"));
        launcher.Processes.Last().Exit(1);
        await TabTestHarness.Eventually(() => h.Notifier.Last("ProjectAction:") is not null, "the notification");

        var shown = h.Notifier.Last("ProjectAction:")!;
        Assert.Equal((tab.DisplayName, "Build editor failed (exit code 1)."), (shown.Title, shown.Body));
        h.Notifier.Click(shown.Id);
        Assert.Equal(new NotificationTarget(NotificationKind.ProjectAction, tab.Id), clicked);

        // In front, on another tab even: the chip says it, so no notification.
        h.Services.Notifications.SetAppActive(true);
        var count = h.Notifier.Shown.Count;
        await tab.RunProjectActionCommand.ExecuteAsync(Action(tab, "build-editor"));
        launcher.Processes.Last().Exit(0);
        await TabTestHarness.Eventually(() => LastRun(tab)!.Succeeded, "the end");
        Assert.Equal(count, h.Notifier.Shown.Count);

        h.Services.Settings.Notifications.ProjectActions = false;
        Assert.False(h.Services.Notifications.IsEnabled(NotificationKind.ProjectAction));
        Assert.True(new NotificationSettings().ProjectActions);
    }

    [Fact]
    public async Task Build_and_launch_launches_the_editor_only_after_a_build_that_worked()
    {
        var (h, launcher, uproject) = UnrealHarness();
        await using var _h = h;
        var tab = await OpenWithProjectAsync(h);
        var editor = UnrealCommands.EditorPath(h.Root, "UnrealEditor", ToolOSExtensions.Current);

        await tab.RunProjectActionCommand.ExecuteAsync(Action(tab, "build-and-launch"));
        launcher.Processes.Last().Exit(2);
        await TabTestHarness.Eventually(() => LastRun(tab)!.Failed, "the failed build");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(launcher.Started, s => s.FileName == editor);

        await tab.RunProjectActionCommand.ExecuteAsync(Action(tab, "build-and-launch"));
        launcher.Processes.Last().Exit(0);
        await TabTestHarness.Eventually(() => launcher.Started.Any(s => s.FileName == editor), "the launch");
        Assert.Equal([uproject], launcher.Started.Single(s => s.FileName == editor).Arguments);
    }

    [Fact]
    public async Task The_output_keeps_the_last_5000_lines()
    {
        var (h, launcher, _) = UnrealHarness();
        await using var _h = h;
        var tab = await OpenWithProjectAsync(h);
        await tab.RunProjectActionCommand.ExecuteAsync(Action(tab, "build-editor"));
        var process = launcher.Processes.Last();

        for (var i = 1; i <= 5200; i++)
        {
            process.WriteOutput($"line {i}");
        }
        var run = tab.SelectedProjectRun!;
        await TabTestHarness.Eventually(() => run.Output.LastOrDefault() == "line 5200", "the output");

        Assert.Equal(ProjectRunViewModel.MaxOutputLines, run.Output.Count);
        Assert.Equal("line 201", run.Output[0]);
        Assert.Equal(201, run.OutputDropped);
        Assert.Contains("5,000", run.OutputNote!.Replace(".", ",", StringComparison.Ordinal).Replace(" ", ",", StringComparison.Ordinal), StringComparison.Ordinal);
        await tab.CopyProjectOutputCommand.ExecuteAsync(null);
        Assert.EndsWith("line 5200", h.Platform.Clipboard, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_output_opens_the_Project_page_and_the_pages_take_turns()
    {
        var (h, _, _) = UnrealHarness();
        await using var _h = h;
        var tab = await OpenWithProjectAsync(h);

        Entry(tab, "Show output…").Command!.Execute(null);

        Assert.True(tab.IsSidePanelOpen);
        Assert.True(tab.IsProjectPage);
        Assert.False(tab.IsFilesPage);
        tab.ShowAgentsPageCommand.Execute(null);
        Assert.False(tab.IsProjectPage);
        tab.ShowProjectPageCommand.Execute(null);
        Assert.False(tab.IsAgentsPage);
        tab.ShowFilesPageCommand.Execute(null);
        Assert.True(tab.IsFilesPage);
    }

    // ---- Clean ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Clean_confirms_with_the_folders_and_their_size_then_deletes_them()
    {
        var (h, _, _) = UnrealHarness();
        await using var _h = h;
        Write(Path.Combine(h.WorkFolder, "Intermediate", "Build", "Makefile.bin"), new string('x', 2048));
        Write(Path.Combine(h.WorkFolder, "Plugins", "Owl", "Owl.uplugin"), "{}");
        Write(Path.Combine(h.WorkFolder, "Plugins", "Owl", "Binaries", "Owl.dll"), "x");
        Write(Path.Combine(h.WorkFolder, "Saved", "Logs", "NightOwl.log"), "keep");
        var tab = await OpenWithProjectAsync(h);

        await tab.RunProjectActionCommand.ExecuteAsync(Action(tab, "clean"));

        var confirmation = h.Shell.Confirmation!;
        Assert.Equal("Clean intermediates?", confirmation.Title);
        Assert.Contains("2 folders, 2 KB in all", confirmation.Message, StringComparison.Ordinal);
        Assert.Contains("• Intermediate", confirmation.Message, StringComparison.Ordinal);
        Assert.Contains($"• {Path.Combine("Plugins", "Owl", "Binaries")}", confirmation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Saved", confirmation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("open", confirmation.Message, StringComparison.Ordinal);

        await confirmation.ConfirmCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => LastRun(tab) is { Succeeded: true }, "the clean");

        Assert.False(Directory.Exists(Path.Combine(h.WorkFolder, "Intermediate")));
        Assert.False(Directory.Exists(Path.Combine(h.WorkFolder, "Plugins", "Owl", "Binaries")));
        Assert.True(File.Exists(Path.Combine(h.WorkFolder, "Saved", "Logs", "NightOwl.log")));
        Assert.True(File.Exists(Path.Combine(h.WorkFolder, "Plugins", "Owl", "Owl.uplugin")));
        Assert.Contains("Deleted 2 folders.", LastRun(tab)!.Output);
        await TabTestHarness.Eventually(() => !Action(tab, "clean").IsEnabled, "nothing left to clean");
    }

    [Fact]
    public async Task Clean_warns_when_the_editor_seems_to_have_the_project_open()
    {
        var (h, _, uproject) = UnrealHarness();
        await using var _h = h;
        var processes = new FakeSystemProcesses();
        processes.Running.Add(new SystemProcess(77, "UnrealEditor", $"\"{UnrealCommands.EditorPath(h.Root, "UnrealEditor", ToolOSExtensions.Current)}\" \"{uproject}\""));
        processes.Running.Add(new SystemProcess(78, "UnrealEditor", "UnrealEditor /other/Game.uproject"));
        h.Services.ProjectTools.Processes = processes;
        Directory.CreateDirectory(Path.Combine(h.WorkFolder, "Binaries"));
        var tab = await OpenWithProjectAsync(h);

        await tab.RunProjectActionCommand.ExecuteAsync(Action(tab, "clean"));

        Assert.Contains("The editor seems to have this project open.", h.Shell.Confirmation!.Message, StringComparison.Ordinal);
        h.Shell.Confirmation.CancelCommand.Execute(null);
        Assert.True(Directory.Exists(Path.Combine(h.WorkFolder, "Binaries")));
    }

    [Fact]
    public async Task Kill_all_editors_confirms_with_each_editor_and_its_project_then_ends_their_trees()
    {
        var (h, _, uproject) = UnrealHarness();
        await using var _h = h;
        var processes = new FakeSystemProcesses();
        h.Services.ProjectTools.Processes = processes;
        var tab = await OpenWithProjectAsync(h);
        Assert.Equal("No Unreal editor is running", Entry(tab, "Kill all Unreal editors…").Tip);

        processes.Running.Add(new SystemProcess(501, "UnrealEditor", $"UnrealEditor \"{uproject}\""));
        processes.Running.Add(new SystemProcess(502, "UnrealEditor-Cmd", "UnrealEditor-Cmd /g/Other.uproject -run=cook"));
        await tab.RefreshProjectCommand.ExecuteAsync(null);
        Assert.True(Entry(tab, "Kill all Unreal editors…").IsEnabled);
        await tab.RunProjectActionCommand.ExecuteAsync(Action(tab, "kill-editors"));

        var confirmation = h.Shell.Confirmation!;
        Assert.Equal("End 2 Unreal editors?", confirmation.Title);
        Assert.Contains("• UnrealEditor (PID 501): NightOwl", confirmation.Message, StringComparison.Ordinal);
        Assert.Contains("• UnrealEditor-Cmd (PID 502): Other", confirmation.Message, StringComparison.Ordinal);
        Assert.Empty(processes.Killed);

        await confirmation.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal([501, 502], processes.Killed);
        Assert.Contains(tab.Items, i => i is Conversation.NoteItem { Text: "Ended 2 Unreal editors." });
        await TabTestHarness.Eventually(() => !Entry(tab, "Kill all Unreal editors…").IsEnabled, "none left");
    }

    // ---- Unity ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_Unity_project_gets_its_chip_and_note_and_its_tests_report_their_counts()
    {
        var launcher = new FakeLauncher();
        await using var h = new TabTestHarness(launcher: launcher);
        h.Services.ProjectTools.OS = ToolOSExtensions.Current == ToolOS.Windows ? ToolOS.Windows : ToolOS.Linux;
        h.Services.ProjectTools.Paths = new ProjectToolPaths(Path.Combine(h.Root, "home"), ProgramFiles: Path.Combine(h.Root, "programs"));
        Write(Path.Combine(h.WorkFolder, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.20f1\n");
        Directory.CreateDirectory(Path.Combine(h.WorkFolder, "Assets"));
        var editor = OperatingSystem.IsWindows()
            ? Path.Combine(h.Root, "programs", "Unity", "Hub", "Editor", "2022.3.20f1", "Editor", "Unity.exe")
            : Path.Combine(h.Root, "home", "Unity", "Hub", "Editor", "2022.3.20f1", "Editor", "Unity");
        Write(editor, "");

        var tab = await OpenWithProjectAsync(h);

        Assert.Equal("work · Unity 2022.3", tab.ProjectButtonText);
        Assert.StartsWith("This is a Unity 2022.3.20f1 project, work, at ", h.Factory.Launches.Single().AppendSystemPrompt, StringComparison.Ordinal);
        await tab.RunProjectActionCommand.ExecuteAsync(Action(tab, "run-editmode-tests"));
        var spec = launcher.Started.Last();
        Assert.Equal(editor, spec.FileName);
        var results = Action(tab, "run-editmode-tests").ResultFile!;
        Assert.StartsWith(h.Services.Paths.ProjectJobsDirectory, results, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.GetDirectoryName(results)));
        File.WriteAllText(results, """<test-run total="12" passed="11" failed="1" skipped="0" />""");
        launcher.Processes.Last().Exit(2);
        await TabTestHarness.Eventually(() => LastRun(tab)!.Failed, "the tests");

        Assert.Equal("Run EditMode tests failed (exit code 2). 11 passed, 1 failed.", LastRun(tab)!.Status);
    }

    // ---- Custom actions (claudette.json and claudette.local.json) ----------------------------------------------------------

    [Fact]
    public async Task Custom_actions_alone_show_the_chip_and_run_through_the_shell_in_their_folder()
    {
        var launcher = new FakeLauncher();
        await using var h = new TabTestHarness(launcher: launcher);
        var tests = Directory.CreateDirectory(Path.Combine(h.WorkFolder, "tests")).FullName;
        Write(Path.Combine(h.WorkFolder, ProjectFile.LocalName), """{ "actions": [ { "name": "Run tests", "command": "make test", "folder": "tests" } ] }""");

        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => tab.HasProjectTools, "the actions");

        Assert.True(tab.HasOnlyCustomActions);
        Assert.Equal("Actions", tab.ProjectButtonText);
        Assert.Equal("Actions", tab.ProjectMenuTitle);
        Assert.Equal(["Run tests", "-", "Show output…", "Add an action…", "Refresh"], tab.ProjectMenu.Select(e => e.IsSeparator ? "-" : e.Label));
        Assert.Equal("Run tests", tab.MainProjectAction!.Label);

        // The local file is the user's own: it runs without asking.
        await tab.RunProjectActionCommand.ExecuteAsync(tab.ProjectActions.Single());

        Assert.Null(h.Shell.Confirmation);
        var spec = launcher.Started.Last();
        Assert.Equal(tests, spec.WorkingDirectory);
        Assert.True(spec.TrackProcessTree);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(("cmd.exe", "/d /s /c \"make test\""), (spec.FileName, spec.CommandLine));
        }
        else
        {
            Assert.Equal(["-c", "make test"], spec.Arguments);
        }
        launcher.Processes.Last().WriteOutput("ok 12 tests");
        launcher.Processes.Last().Exit(0);
        await TabTestHarness.Eventually(() => LastRun(tab)!.Status == "Run tests succeeded.", "the end");
        Assert.Contains("ok 12 tests", LastRun(tab)!.Output);
    }

    [Fact]
    public async Task Edits_to_the_file_show_when_the_tab_is_looked_at_again()
    {
        await using var h = new TabTestHarness(launcher: new FakeLauncher());
        var tab = await h.OpenTabAsync();
        Assert.False(tab.HasProjectTools);

        Write(Path.Combine(h.WorkFolder, ProjectFile.SharedName), """{ "actions": [ { "name": "Lint", "command": "npm run lint" }, { "command": "nameless" } ] }""");
        tab.IsSelected = false;
        tab.IsSelected = true;
        await TabTestHarness.Eventually(() => tab.HasProjectTools, "the new action");

        Assert.Equal(["Lint"], tab.ProjectActions.Select(a => a.Label));
        Assert.Equal(["claudette.json: actions[1] has no name, so it was skipped."], tab.ProjectFileProblems);
    }

    [Fact]
    public async Task A_shared_action_runs_on_a_click_without_asking()
    {
        var launcher = new FakeLauncher();
        await using var h = new TabTestHarness(launcher: launcher);
        Write(Path.Combine(h.WorkFolder, ProjectFile.SharedName), """{ "actions": [ { "name": "Run tests", "command": "make test" } ] }""");
        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => tab.HasProjectTools, "the actions");
        var started = launcher.Started.Count;

        await tab.RunProjectActionCommand.ExecuteAsync(tab.ProjectActions[0]);

        // The user's choice (DESIGN.md §18): claudette.json's actions run like the local file's, with no confirmation.
        Assert.Null(h.Shell.Confirmation);
        Assert.Equal(started + 1, launcher.Started.Count);
        launcher.Processes.Last().Exit(0);
        await TabTestHarness.Eventually(() => !tab.IsProjectJobRunning, "the end");
    }

    // ---- Links --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Links_follow_the_selected_tab_and_open_only_when_allowed()
    {
        await using var h = new TabTestHarness();
        Directory.CreateDirectory(Path.Combine(h.WorkFolder, ".git"));
        File.WriteAllText(Path.Combine(h.WorkFolder, ".git", "HEAD"), "ref: refs/heads/owl/eyes\n");
        Write(Path.Combine(h.WorkFolder, ProjectFile.SharedName), """
            { "links": [
                { "name": "Pull request", "url": "https://github.com/org/repo/compare/{branch}?expand=1" },
                { "name": "Changelist", "url": "https://swarm.example/changes/{changelist}" },
                { "name": "Local file", "url": "file:///etc/hosts" },
            ] }
            """);
        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => h.Shell.HasLinks, "the links");

        var links = h.Shell.Links;
        Assert.Equal("https://github.com/org/repo/compare/owl%2Feyes?expand=1", links[0].Url);
        Assert.Equal("No Perforce changelist", links[1].Problem);
        Assert.False(links[2].IsEnabled);
        Assert.False(tab.HasProjectTools);
        await h.Shell.OpenLinkCommand.ExecuteAsync(links[0]);
        await h.Shell.OpenLinkCommand.ExecuteAsync(links[2]);
        Assert.Equal(["https://github.com/org/repo/compare/owl%2Feyes?expand=1"], h.Platform.OpenedUrls);

        // Another tab, in a folder without links: the section goes. (Its folder is gone, so it doesn't start a session.)
        var other = new TabViewModel(h.Services, h.Shell, new TabState { Folder = Path.Combine(h.Root, "elsewhere") }, isRestored: false);
        var group = new TabGroupViewModel(other.Folder, 1, isCollapsed: false);
        group.Tabs.Add(other);
        h.Shell.Groups.Add(group);
        h.Shell.SelectedTab = other;
        Assert.False(h.Shell.HasLinks);
        Assert.Empty(h.Shell.Links);
        h.Shell.SelectedTab = tab;
        Assert.True(h.Shell.HasLinks);

        Assert.False(h.Shell.IsLinksCollapsed);
        h.Shell.ToggleLinksCommand.Execute(null);
        Assert.True(h.Services.State.LinksCollapsed);
    }

    // ---- The engine folder --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Choose_engine_folder_checks_the_pick_and_remembers_it_for_the_project()
    {
        var launcher = new FakeLauncher();
        await using var h = new TabTestHarness(launcher: launcher);
        var uproject = Path.Combine(h.WorkFolder, "Game.uproject");
        File.WriteAllText(uproject, """{ "EngineAssociation": "5.9" }""");
        var tab = await OpenWithProjectAsync(h);
        Assert.Equal("Choose engine folder…", tab.Project!.Fix!.Label);
        Assert.False(Action(tab, "launch-editor").IsEnabled);

        h.Platform.FolderToPick = h.WorkFolder;
        await tab.FixProjectCommand.ExecuteAsync(null);
        Assert.Null(h.Services.State.ProjectTools.Get(uproject, UnrealEngineLocator.EngineKey));
        Assert.Contains(tab.Items, i => i is Conversation.NoteItem note && note.Text.Contains("isn't an Unreal Engine folder", StringComparison.Ordinal));

        var engine = Path.Combine(h.Root, "Engines", "UE_5.9");
        WriteUnrealProject(engine, Path.Combine(h.Root, "unused"));
        h.Platform.FolderToPick = Path.Combine(engine, "Engine");
        await tab.FixProjectCommand.ExecuteAsync(null);

        Assert.Equal(engine, h.Services.State.ProjectTools.Get(uproject, UnrealEngineLocator.EngineKey));
        Assert.True(Action(tab, "launch-editor").IsEnabled);
        Assert.Null(tab.ProjectProblem);
        Assert.Contains("chosen by you", tab.ProjectHeaderLines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_folder_with_several_projects_lets_the_user_pick_one_and_remembers_it()
    {
        var launcher = new FakeLauncher();
        await using var h = new TabTestHarness(launcher: launcher);
        WriteUnrealProject(h.Root, Path.Combine(h.WorkFolder, "Alpha"));
        File.Move(Path.Combine(h.WorkFolder, "Alpha", "NightOwl.uproject"), Path.Combine(h.WorkFolder, "Alpha", "Alpha.uproject"));
        File.WriteAllText(Path.Combine(h.WorkFolder, "Beta.uproject"), """{ "EngineAssociation": "" }""");
        var tab = await OpenWithProjectAsync(h);
        Assert.Equal("Beta", tab.Project!.Name);
        Assert.True(Entry(tab, "Projects in this folder").IsHeader);

        await tab.ChooseProjectCommand.ExecuteAsync(Entry(tab, "Alpha").Parameter);

        Assert.Equal("Alpha", tab.Project!.Name);
        Assert.True(Entry(tab, "Alpha").IsChecked);
        Assert.Equal(Path.Combine(h.WorkFolder, "Alpha", "Alpha.uproject"), h.Services.State.ProjectTools.ChosenProjectFor(h.WorkFolder));
    }

    private sealed class FakeSystemProcesses : ISystemProcesses
    {
        public List<SystemProcess> Running { get; } = [];

        public List<int> Killed { get; } = [];

        public IReadOnlyList<SystemProcess> Find(IReadOnlyCollection<string> names)
        {
            lock (Running)
            {
                return Running.Where(p => SystemProcessNames.Matches(p.Name, names)).ToArray();
            }
        }

        public void KillTree(int pid)
        {
            lock (Running)
            {
                Killed.Add(pid);
                Running.RemoveAll(p => p.Pid == pid);
            }
        }
    }
}
