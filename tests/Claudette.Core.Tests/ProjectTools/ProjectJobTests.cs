using Claudette.Core.Diffs;
using Claudette.Core.Processes;
using Claudette.Core.ProjectTools;
using Claudette.Core.Settings;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.ProjectTools;

/// <summary>Jobs, custom actions and opening solutions (DESIGN.md §18, "Project tools"), with a fake launcher.</summary>
public sealed class ProjectJobTests : IDisposable
{
    private readonly TempFolder _temp = new("claudette-jobs");

    public void Dispose() => _temp.Dispose();

    // ---- Jobs ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_jobs_output_arrives_in_order_and_exit_code_0_is_success()
    {
        var launcher = new FakeProcessLauncher();
        var job = ProjectJob.Start("Build editor", launcher, new ProcessStartSpec("build", []));
        var lines = new List<string>();
        job.Output += batch =>
        {
            lock (lines)
            {
                lines.AddRange(batch);
            }
        };
        var process = launcher.Processes.Single();
        Assert.True(process.StandardInputClosed);

        job.Begin();
        process.WriteOutput("Building NightOwlEditor...\n[1/3] Compile Module.NightOwl.cpp");
        process.WriteError("warning C4996: something old");
        process.Exit(0);
        var result = await job.Completion;

        Assert.Equal(new ProjectJobResult(ProjectJobState.Succeeded, 0), result);
        Assert.Contains("[1/3] Compile Module.NightOwl.cpp", lines);
        Assert.Contains("warning C4996: something old", lines);
        Assert.True(lines.IndexOf("Building NightOwlEditor...") < lines.IndexOf("[1/3] Compile Module.NightOwl.cpp"));
        await process.Disposed;
    }

    [Fact]
    public async Task A_nonzero_exit_code_is_a_failure()
    {
        var launcher = new FakeProcessLauncher { Respond = _ => new ProcessResult(6, "", "error: it broke") };
        var job = ProjectJob.Start("Build editor", launcher, new ProcessStartSpec("build", []));

        job.Begin();

        Assert.Equal(new ProjectJobResult(ProjectJobState.Failed, 6), await job.Completion);
    }

    [Fact]
    public async Task Stop_ends_the_whole_tree_then_the_process()
    {
        var launcher = new FakeProcessLauncher();
        var job = ProjectJob.Start("Build editor", launcher, new ProcessStartSpec("build", []));
        var treeKilled = false;
        job.UseTreeKiller(() => treeKilled = true);
        job.Begin();

        job.Stop();
        job.Stop();
        var result = await job.Completion;

        Assert.True(treeKilled);
        Assert.True(launcher.Processes.Single().Killed);
        Assert.Equal(ProjectJobState.Stopped, result.State);
    }

    [Fact]
    public async Task Work_done_in_Claudette_reports_lines_and_can_be_stopped()
    {
        var lines = new List<string>();
        var ok = ProjectJob.Run("Clean", (log, _) =>
        {
            log("Deleting Intermediate");
            return Task.FromResult(true);
        });
        ok.Output += batch => lines.AddRange(batch);
        ok.Begin();
        Assert.Equal(new ProjectJobResult(ProjectJobState.Succeeded, null), await ok.Completion);
        Assert.Equal(["Deleting Intermediate"], lines);

        var started = new TaskCompletionSource();
        var stopped = ProjectJob.Run("Clean", async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return true;
        });
        stopped.Begin();
        await started.Task;
        stopped.Stop();
        Assert.Equal(ProjectJobState.Stopped, (await stopped.Completion).State);
    }

    // ---- Custom actions ------------------------------------------------------------------------------------------------

    [Fact]
    public void What_project_tools_remember_survives_a_state_round_trip()
    {
        var state = new AppState();
        var folder = _temp.CreateFolder("game");
        state.ProjectTools.Set(Path.Combine(folder, "Game.uproject"), "configuration", "DebugGame");
        state.ProjectTools.ChooseProject(folder, Path.Combine(folder, "Game.uproject"));
        state.LinksCollapsed = true;

        var json = JsonFileStore<AppState>.Serialize(state);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppState>(json, JsonFileStore<AppState>.Options)!;

        Assert.Equal("DebugGame", restored.ProjectTools.Get(Path.Combine(folder, "Game.uproject"), "configuration"));
        Assert.Equal(Path.Combine(folder, "Game.uproject"), restored.ProjectTools.ChosenProjectFor(folder + Path.DirectorySeparatorChar));
        Assert.True(restored.LinksCollapsed);

        restored.ProjectTools.Set(Path.Combine(folder, "Game.uproject"), "configuration", null);
        Assert.Empty(restored.ProjectTools.Projects);
    }

    [Fact]
    public void A_custom_action_runs_through_the_shell_in_its_working_folder()
    {
        var folder = _temp.CreateFolder("game");
        var tests = _temp.CreateFolder("game/Tests");
        var custom = new CustomProjectAction { Id = "t", Name = "Run tests", Command = "dotnet test", WorkingFolder = "Tests" };

        var windows = custom.ToAction(folder, ToolOS.Windows, shell: null);
        var mac = custom.ToAction(folder, ToolOS.MacOS, shell: "/bin/zsh");

        Assert.Equal(("custom:t", "Run tests", ProjectActionKind.Run), (windows.Id, windows.Label, windows.Kind));
        Assert.Equal("cmd.exe", windows.Process!.FileName);
        Assert.Equal("/d /s /c \"dotnet test\"", windows.Process.CommandLine);
        Assert.Equal(tests, windows.Process.WorkingDirectory);
        Assert.Equal(("/bin/zsh", "-c", "dotnet test"), (mac.Process!.FileName, mac.Process.Arguments[0], mac.Process.Arguments[1]));
        Assert.Equal(tests, mac.Process.WorkingDirectory);
        Assert.True(windows.IsEnabled);
        Assert.Equal(folder, new CustomProjectAction { Command = "make" }.ToAction(folder, ToolOS.Linux, null).Process!.WorkingDirectory);
    }

    [Fact]
    public void Launch_and_forget_starts_detached_and_a_missing_folder_disables_it()
    {
        var folder = _temp.CreateFolder("game");

        var server = new CustomProjectAction { Name = "Server", Command = "./server", Mode = CustomActionMode.LaunchAndForget }.ToAction(folder, ToolOS.Linux, null);
        var lost = new CustomProjectAction { Name = "Lost", Command = "ls", WorkingFolder = "gone" }.ToAction(folder, ToolOS.Linux, null);

        Assert.Equal(ProjectActionKind.Launch, server.Kind);
        Assert.True(server.Process!.Detached);
        Assert.False(lost.IsEnabled);
        Assert.Contains("doesn't exist", lost.DisabledReason, StringComparison.Ordinal);
    }

    // ---- Opening solutions -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_OSs_app_opens_a_solution_unless_Settings_names_an_IDE()
    {
        var paths = new ProjectToolPaths(_temp.Combine("home"));
        var none = new NoFiles();

        Assert.Null(SolutionOpeners.For(SolutionOpener.System, "/g/Game.sln", ToolOS.Linux, paths, none).Spec);

        var custom = SolutionOpeners.For(SolutionOpener.Custom, "/g/Game.sln", ToolOS.Linux, paths, none, "/opt/ide/bin/ide").Spec!;
        Assert.Equal(("/opt/ide/bin/ide", "/g/Game.sln", true), (custom.FileName, custom.Arguments.Single(), custom.Detached));

        var rider = SolutionOpeners.For(SolutionOpener.Rider, "/g/Game (Mac).xcworkspace", ToolOS.MacOS, paths, none).Spec!;
        Assert.Equal("/usr/bin/open", rider.FileName);
        Assert.Equal(["-a", "Rider", "/g/Game (Mac).xcworkspace"], rider.Arguments);

        var missing = SolutionOpeners.For(SolutionOpener.VSCode, "/g/Game.code-workspace", ToolOS.Linux, paths, none);
        Assert.Null(missing.Spec);
        Assert.Contains("VS Code wasn't found", missing.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void On_Windows_VS_Codes_batch_file_runs_through_cmd()
    {
        var paths = new ProjectToolPaths(_temp.Combine("home"));
        var probe = new NoFiles { OnPath = { ["code.cmd"] = @"C:\Code\bin\code.cmd" } };

        var spec = SolutionOpeners.For(SolutionOpener.VSCode, @"D:\My Game\Game.code-workspace", ToolOS.Windows, paths, probe).Spec!;

        Assert.Equal("cmd.exe", spec.FileName);
        Assert.Equal("""/d /s /c ""C:\Code\bin\code.cmd" "D:\My Game\Game.code-workspace"" """.TrimEnd(), spec.CommandLine);
    }

    private sealed class NoFiles : IFileProbe
    {
        public Dictionary<string, string> OnPath { get; } = [];

        public bool FileExists(string path) => OnPath.ContainsValue(path);

        public string? FindOnPath(string fileName) => OnPath.GetValueOrDefault(fileName);

        public string ExpandEnvironmentVariables(string path) => path;
    }
}
