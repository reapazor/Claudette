using System.Diagnostics;
using Claudette.Core.Processes;
using Claudette.Core.Updates;
using Claudette.Platform.Tests.Support;
using Claudette.Platform.Updates.Mac;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Platform.Tests.Updates;

/// <summary>
/// Updating <c>Claudette.app</c> from a release's disk image (DESIGN.md §2, "Updating Claudette"). The macOS tools
/// (hdiutil, ditto, codesign, plutil) are scripted; the helper script that swaps the apps runs for real.
/// </summary>
public sealed class MacAppInstallerTests : IDisposable
{
    private static readonly AppVersion Version = new(1, 3, 0);

    private readonly string _root = Directory.CreateTempSubdirectory("claudette-mac-").FullName;

    private string Applications => Path.Combine(_root, "Applications");

    private string App => Path.Combine(Applications, "Claudette.app");

    private string Staged => Path.Combine(Applications, ".Claudette-update.app");

    private string Updates => Path.Combine(_root, "updates");

    public MacAppInstallerTests()
    {
        MakeApp(App, "old", Version.ToString());
        Directory.CreateDirectory(Updates);
    }

    /// <summary>An app bundle whose "Info.plist" is key=value lines, which the scripted plutil reads.</summary>
    private static void MakeApp(string path, string contents, string version, string bundleId = MacAppInstaller.BundleIdentifier)
    {
        Directory.CreateDirectory(Path.Combine(path, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(path, "Contents", "MacOS", "Claudette"), contents);
        File.WriteAllText(Path.Combine(path, "Contents", "Info.plist"), $"CFBundleIdentifier={bundleId}\nCFBundleShortVersionString={version}\n");
    }

    /// <summary>What the disk image holds, and how the scripted tools answer.</summary>
    private sealed class Tools
    {
        public string ImageVersion { get; set; } = "1.3.0";

        public string ImageBundleId { get; set; } = MacAppInstaller.BundleIdentifier;

        public int SignatureExitCode { get; set; }

        public string? RunningTeam { get; set; } = "TEAM123456";

        public string? ImageTeam { get; set; } = "TEAM123456";

        public List<string> Commands { get; } = [];
    }

    private MacAppInstaller Installer(Tools tools, string? app = null) =>
        new(new ScriptedLauncher(spec => Run(tools, spec)), new FakeTimeProvider(), app ?? App, Updates, processId: 4242);

    private (int, string, string) Run(Tools tools, ProcessStartSpec spec)
    {
        var args = spec.Arguments;
        tools.Commands.Add($"{Path.GetFileName(spec.FileName)} {args[0]}");
        switch (Path.GetFileName(spec.FileName), args[0])
        {
            case ("hdiutil", "attach"):
                MakeApp(Path.Combine(args[^2], "Claudette.app"), "new", tools.ImageVersion, tools.ImageBundleId);
                return (0, "", "");
            case ("hdiutil", "detach"):
                Directory.Delete(args[1], recursive: true);
                return (0, "", "");
            case ("ditto", _):
                CopyDirectory(args[0], args[1]);
                return (0, "", "");
            case ("codesign", "--verify"):
                return (tools.SignatureExitCode, "", tools.SignatureExitCode == 0 ? "" : $"{args[^1]}: a sealed resource is missing or invalid");
            case ("codesign", "-dv"):
                var team = args[^1] == App ? tools.RunningTeam : tools.ImageTeam;
                return (0, "", $"Executable={args[^1]}/Contents/MacOS/Claudette\nTeamIdentifier={team ?? "not set"}\n");
            case ("plutil", "-extract"):
                var line = File.ReadAllLines(args[^1]).FirstOrDefault(l => l.StartsWith(args[1] + "=", StringComparison.Ordinal));
                return line is null ? (1, "", "No value at that key path") : (0, line[(args[1].Length + 1)..] + "\n", "");
            default:
                return (127, "", "unexpected command");
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, target));
        }
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, target));
        }
    }

    [Fact]
    public void The_running_bundle_is_found_from_the_programs_folder()
    {
        Assert.Equal(App, MacAppInstaller.FindBundle(Path.Combine(App, "Contents", "MacOS") + Path.DirectorySeparatorChar));
        Assert.Null(MacAppInstaller.FindBundle(Path.Combine(_root, "bin", "Release", "net10.0")));
    }

    [Fact]
    public async Task A_new_version_that_checks_out_is_copied_next_to_the_running_one()
    {
        var tools = new Tools();

        var prepared = await Installer(tools).PrepareAsync("/downloads/Claudette-1.3.0-arm64.dmg", Version, TestContext.Current.CancellationToken);

        Assert.Equal(Staged, prepared.StagedPath);
        Assert.Equal("new", File.ReadAllText(Path.Combine(Staged, "Contents", "MacOS", "Claudette")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(App, "Contents", "MacOS", "Claudette")));
        Assert.Equal(["hdiutil", "ditto", "hdiutil"], tools.Commands.Take(3).Select(c => c.Split(' ')[0]));
        Assert.Empty(Directory.EnumerateDirectories(Updates, "mount-*"));
    }

    [Fact]
    public async Task A_new_version_from_another_developer_is_refused_and_removed()
    {
        var error = await Assert.ThrowsAsync<AppInstallException>(() =>
            Installer(new Tools { ImageTeam = "OTHERTEAM1" }).PrepareAsync("/downloads/x.dmg", Version, TestContext.Current.CancellationToken));

        Assert.Equal("The new version is signed by a different developer (OTHERTEAM1) than this one (TEAM123456), so it wasn't installed.", error.Message);
        Assert.False(Directory.Exists(Staged));
    }

    [Fact]
    public async Task A_broken_signature_another_app_or_the_wrong_version_is_refused()
    {
        var signature = await Assert.ThrowsAsync<AppInstallException>(() =>
            Installer(new Tools { SignatureExitCode = 1 }).PrepareAsync("/downloads/x.dmg", Version, TestContext.Current.CancellationToken));
        var other = await Assert.ThrowsAsync<AppInstallException>(() =>
            Installer(new Tools { ImageBundleId = "com.example.other" }).PrepareAsync("/downloads/x.dmg", Version, TestContext.Current.CancellationToken));
        var version = await Assert.ThrowsAsync<AppInstallException>(() =>
            Installer(new Tools { ImageVersion = "1.2.9" }).PrepareAsync("/downloads/x.dmg", Version, TestContext.Current.CancellationToken));

        Assert.StartsWith("The new version's signature didn't check out", signature.Message);
        Assert.Equal("The disk image holds a different app, not Claudette.", other.Message);
        Assert.Equal("The app in the disk image is version 1.2.9, not 1.3.0 as the release says.", version.Message);
        Assert.False(Directory.Exists(Staged));
    }

    [Fact]
    public async Task An_unsigned_running_app_accepts_any_valid_signature()
    {
        var prepared = await Installer(new Tools { RunningTeam = null, ImageTeam = "TEAM123456" }).PrepareAsync("/downloads/x.dmg", Version, TestContext.Current.CancellationToken);

        Assert.Equal(Staged, prepared.StagedPath);
    }

    [Fact]
    public async Task A_translocated_app_is_sent_to_install_by_hand()
    {
        // A macOS path, whatever OS runs the test: it's refused before anything touches the disk.
        const string translocated = "/private/var/folders/xy/T/AppTranslocation/ABC123/d/Claudette.app";

        var error = await Assert.ThrowsAsync<AppInstallException>(() =>
            Installer(new Tools(), translocated).PrepareAsync("/downloads/x.dmg", Version, TestContext.Current.CancellationToken));

        Assert.True(error.CanOpenPackage);
        Assert.Contains("Move Claudette to Applications", error.Message);
    }

    [Fact]
    public async Task Installing_starts_the_helper_detached_and_Claudette_closes()
    {
        var tools = new Tools();
        var launcher = new ScriptedLauncher(spec => Run(tools, spec));
        var installer = new MacAppInstaller(launcher, new FakeTimeProvider(), App, Updates, processId: 4242);
        var prepared = await installer.PrepareAsync("/downloads/x.dmg", Version, TestContext.Current.CancellationToken);

        var outcome = await installer.InstallAsync(prepared, ["--restore", "n1"], "/data/restart-ready", "n1", TestContext.Current.CancellationToken);

        Assert.Equal(InstallOutcome.ExitNow, outcome);
        var helper = launcher.Specs[^1];
        Assert.Equal("/bin/sh", helper.FileName);
        Assert.True(helper.Detached);
        Assert.Equal([Path.Combine(Updates, "update-helper.sh"), "4242", App, Staged, "/data/restart-ready", "n1", "open", "--restore", "n1"], helper.Arguments);
        Assert.Equal(MacAppInstaller.HelperScript(), File.ReadAllText(Path.Combine(Updates, "update-helper.sh")));
    }

    // ---- The helper script, run for real ----------------------------------------------------------------------------

    /// <summary>An executable that logs how it was started, and optionally says it's up.</summary>
    private void MakeExecutable(string app, string name, bool signalsReady)
    {
        var path = Path.Combine(app, "Contents", "MacOS", "Claudette");
        var log = Path.Combine(_root, "launches.log");
        var ready = signalsReady ? $"printf '%s' \"$2\" > '{Path.Combine(_root, "restart-ready")}'\n" : "";
        File.WriteAllText(path, $"#!/bin/sh\necho \"{name} $*\" >> '{log}'\n{ready}");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private async Task<(int ExitCode, string Log)> RunHelperAsync(int readyTicks)
    {
        var script = Path.Combine(Updates, "update-helper.sh");
        File.WriteAllText(script, MacAppInstaller.HelperScript());
        // Stands in for Claudette, quitting shortly after starting the helper.
        using var quitting = Process.Start(new ProcessStartInfo("sleep", "0.3"))!;
        var start = new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { script, quitting.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), App, Staged, Path.Combine(_root, "restart-ready"), "n1", "exec", "--restore", "n1" },
            Environment = { ["CLAUDETTE_UPDATE_READY_TICKS"] = readyTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            RedirectStandardError = true,
        };
        using var helper = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await helper.WaitForExitAsync(timeout.Token);
        // Let the last launch write its line.
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var log = Path.Combine(_root, "launches.log");
        return (helper.ExitCode, File.Exists(log) ? File.ReadAllText(log) : "");
    }

    [Fact]
    public async Task The_helper_swaps_in_the_new_version_once_Claudette_quits_and_it_starts()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The helper runs on macOS; it's tested on Unix.");
        MakeApp(Staged, "", Version.ToString());
        MakeExecutable(App, "old", signalsReady: true);
        MakeExecutable(Staged, "new", signalsReady: true);

        var (exitCode, log) = await RunHelperAsync(readyTicks: 40);

        Assert.Equal(0, exitCode);
        Assert.Equal("new --restore n1\n", log);
        Assert.Contains("echo \"new", File.ReadAllText(Path.Combine(App, "Contents", "MacOS", "Claudette")));
        Assert.False(Directory.Exists(Staged));
        Assert.False(Directory.Exists(App + ".previous"));
    }

    [Fact]
    public async Task When_the_new_version_does_not_start_the_old_one_comes_back_and_takes_the_tabs()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The helper runs on macOS; it's tested on Unix.");
        MakeApp(Staged, "", Version.ToString());
        MakeExecutable(App, "old", signalsReady: true);
        MakeExecutable(Staged, "new", signalsReady: false);

        var (exitCode, log) = await RunHelperAsync(readyTicks: 4);

        Assert.Equal(1, exitCode);
        Assert.Equal("new --restore n1\nold --restore n1\n", log);
        Assert.Contains("echo \"old", File.ReadAllText(Path.Combine(App, "Contents", "MacOS", "Claudette")));
        Assert.False(Directory.Exists(App + ".previous"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Runs each command to completion at once, with the output the test gives.</summary>
    private sealed class ScriptedLauncher(Func<ProcessStartSpec, (int ExitCode, string Output, string Error)> run) : IProcessLauncher
    {
        public List<ProcessStartSpec> Specs { get; } = [];

        public IRunningProcess Start(ProcessStartSpec spec)
        {
            Specs.Add(spec);
            var process = new FakeRunningProcess(Specs.Count);
            if (spec.Detached)
            {
                return process;
            }
            var (exitCode, output, error) = run(spec);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                process.WriteOutput(line);
            }
            foreach (var line in error.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                process.WriteError(line);
            }
            process.Exit(exitCode);
            return process;
        }
    }
}
