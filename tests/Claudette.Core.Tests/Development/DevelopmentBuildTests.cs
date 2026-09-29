using Claudette.Core.Development;
using Claudette.Core.Settings;
using Claudette.Core.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Core.Tests.Development;

/// <summary>Running a source build of Claudette and restarting into its new builds (DESIGN.md §9, "Working on Claudette").</summary>
public class DevelopmentBuildTests
{
    private static readonly DateTime Built = new(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>A build output: the app's own files, a subfolder, and native libraries for two platforms.</summary>
    private static string MakeBuild(TempFolder temp, DateTime stamp)
    {
        var output = temp.Combine("repo", "src", "Claudette.App", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(Path.Combine(output, "Assets"));
        Directory.CreateDirectory(Path.Combine(output, "runtimes", "linux-x64", "native"));
        Directory.CreateDirectory(Path.Combine(output, "runtimes", "win-x64", "native"));
        File.WriteAllText(temp.Combine("repo", SourceBuild.SolutionFileName), "<Solution />");
        File.WriteAllText(Path.Combine(output, "Assets", "claudette.ico"), "icon");
        File.WriteAllText(Path.Combine(output, "runtimes", "linux-x64", "native", "libSkiaSharp.so"), "linux");
        File.WriteAllText(Path.Combine(output, "runtimes", "win-x64", "native", "libSkiaSharp.dll"), "windows");
        foreach (var name in new[] { "Claudette.dll", "Claudette.deps.json", "Claudette.runtimeconfig.json" })
        {
            File.WriteAllText(Path.Combine(output, name), name);
        }
        Touch(output, stamp);
        return output;
    }

    private static void Touch(string output, DateTime stamp)
    {
        foreach (var file in Directory.EnumerateFiles(output))
        {
            File.SetLastWriteTimeUtc(file, stamp);
        }
    }

    // ---- Finding a source build ----------------------------------------------------------------------------------

    [Fact]
    public void A_build_output_inside_a_checkout_is_a_source_build()
    {
        using var temp = new TempFolder();
        var output = MakeBuild(temp, Built);

        var build = SourceBuild.Detect(output + Path.DirectorySeparatorChar);

        Assert.NotNull(build);
        Assert.Equal(Path.GetFullPath(output), build.OutputDirectory);
        Assert.Equal(temp.Combine("repo"), build.RepositoryRoot);
    }

    [Fact]
    public void An_installed_Claudette_is_not_a_source_build()
    {
        using var temp = new TempFolder();
        MakeBuild(temp, Built);
        // Next to the solution, but not a build output.
        var notBin = temp.Combine("repo", "tools", "Claudette");
        Directory.CreateDirectory(notBin);
        // A bin folder with no checkout above it.
        var elsewhere = temp.Combine("elsewhere", "bin", "Release");
        Directory.CreateDirectory(elsewhere);

        Assert.Null(SourceBuild.Detect(notBin));
        Assert.Null(SourceBuild.Detect(elsewhere));
    }

    // ---- Copies of builds ----------------------------------------------------------------------------------------

    [Fact]
    public void A_copy_has_the_app_and_only_this_platforms_native_libraries()
    {
        using var temp = new TempFolder();
        var output = MakeBuild(temp, Built);
        var copies = new BuildCopies(temp.Combine("builds"));

        var copy = copies.Create(output, "linux-x64");

        Assert.NotNull(copy);
        Assert.Equal(Built, copy.Stamp);
        Assert.Equal(Path.GetFullPath(output), copy.SourceOutput);
        Assert.True(File.Exists(Path.Combine(copy.Directory, "Claudette.dll")));
        Assert.True(File.Exists(Path.Combine(copy.Directory, "Assets", "claudette.ico")));
        Assert.True(File.Exists(Path.Combine(copy.Directory, "runtimes", "linux-x64", "native", "libSkiaSharp.so")));
        Assert.False(Directory.Exists(Path.Combine(copy.Directory, "runtimes", "win-x64")));
        Assert.Equal(copy, BuildCopies.Read(copy.Directory));
    }

    [Fact]
    public void The_same_build_is_copied_once()
    {
        using var temp = new TempFolder();
        var output = MakeBuild(temp, Built);
        var copies = new BuildCopies(temp.Combine("builds"));
        var first = copies.Create(output, "linux-x64")!;
        File.WriteAllText(Path.Combine(first.Directory, "Claudette.dll"), "already here");

        var second = copies.Create(output, "linux-x64");

        Assert.Equal(first.Directory, second?.Directory);
        Assert.Equal("already here", File.ReadAllText(Path.Combine(first.Directory, "Claudette.dll")));
    }

    [Fact]
    public void Cleaning_up_keeps_the_running_copy_and_the_one_before()
    {
        using var temp = new TempFolder();
        var output = MakeBuild(temp, Built);
        var copies = new BuildCopies(temp.Combine("builds"));
        var oldest = copies.Create(output, "linux-x64")!;
        Touch(output, Built.AddMinutes(1));
        var previous = copies.Create(output, "linux-x64")!;
        Touch(output, Built.AddMinutes(2));
        var running = copies.Create(output, "linux-x64")!;
        var unfinished = Directory.CreateDirectory(temp.Combine("builds", "20260929-100300-0000000.partial")).FullName;

        copies.CleanUp(running.Directory);

        Assert.True(Directory.Exists(running.Directory));
        Assert.True(Directory.Exists(previous.Directory));
        Assert.False(Directory.Exists(oldest.Directory));
        Assert.False(Directory.Exists(unfinished));
    }

    [Theory]
    [InlineData("win-x64", "win-x64", true)]
    [InlineData("win", "win-x64", true)]
    [InlineData("win-x86", "win-x64", false)]
    [InlineData("osx", "osx-arm64", true)]
    [InlineData("osx-x64", "osx-arm64", false)]
    [InlineData("maccatalyst-arm64", "osx-arm64", false)]
    [InlineData("linux-musl-x64", "linux-x64", false)]
    [InlineData("linux", "linux-musl-x64", true)]
    [InlineData("unix", "linux-x64", true)]
    [InlineData("unix", "win-x64", false)]
    public void Native_libraries_are_kept_for_this_platform_and_the_ones_it_builds_on(string folder, string runtime, bool kept) =>
        Assert.Equal(kept, BuildCopies.IsForPlatform(folder, runtime));

    // ---- Noticing a new build ------------------------------------------------------------------------------------

    [Fact]
    public void A_new_build_is_announced_once_it_stops_changing()
    {
        var time = new FakeTimeProvider();
        DateTime? stamp = Built;
        using var watcher = new BuildWatcher("unused", "Claudette", Built, time, () => stamp);
        var ready = new List<DateTime>();
        watcher.BuildReady += ready.Add;

        time.Advance(BuildWatcher.PollInterval);
        Assert.Empty(ready);

        // Still being written: each check sees something newer.
        stamp = Built.AddSeconds(10);
        time.Advance(BuildWatcher.PollInterval);
        stamp = Built.AddSeconds(11);
        time.Advance(BuildWatcher.PollInterval);
        Assert.Empty(ready);

        time.Advance(BuildWatcher.PollInterval);
        Assert.Equal([Built.AddSeconds(11)], ready);

        time.Advance(BuildWatcher.PollInterval * 3);
        Assert.Single(ready);
    }

    [Fact]
    public void An_older_or_missing_build_is_not_announced()
    {
        var time = new FakeTimeProvider();
        DateTime? stamp = Built.AddMinutes(-5);
        using var watcher = new BuildWatcher("unused", "Claudette", Built, time, () => stamp);
        var ready = new List<DateTime>();
        watcher.BuildReady += ready.Add;

        time.Advance(BuildWatcher.PollInterval * 3);
        stamp = null;
        time.Advance(BuildWatcher.PollInterval * 3);

        Assert.Empty(ready);
    }

    [Fact]
    public void The_watcher_reads_a_real_build_output()
    {
        using var temp = new TempFolder();
        var output = MakeBuild(temp, Built);
        var time = new FakeTimeProvider();
        using var watcher = new BuildWatcher(output, "Claudette", Built, time);
        var ready = new List<DateTime>();
        watcher.BuildReady += ready.Add;

        Touch(output, Built.AddMinutes(1));
        watcher.Poll();
        watcher.Poll();

        Assert.Equal([Built.AddMinutes(1)], ready);
    }

    // ---- Handing over ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_snapshot_is_read_back_only_for_its_restart_and_while_fresh()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("restart.json");
        var now = new DateTimeOffset(Built);
        new RestartSnapshot
        {
            Nonce = "abc",
            CreatedAt = now,
            Tabs = [new TabState { Id = "t1", Folder = "/work/api", UserName = "refactor" }],
            SelectedTabId = "t1",
            Drafts = { ["t1"] = new TabDraft("half-typed", ["clarify"], [new DraftImage("Pasted image", [0x89, 0x50, 0x4E, 0x47])]), ["t2"] = new TabDraft("text only", []) },
            RunningTabIds = ["t1"],
            Window = new WindowPlacement(10, 20, 1200, 800, IsMaximized: false),
        }.Save(path);

        var loaded = RestartSnapshot.Load(path, "abc", now.AddMinutes(1));

        Assert.NotNull(loaded);
        Assert.Equal("refactor", loaded.Tabs.Single().UserName);
        Assert.Equal("half-typed", loaded.Drafts["t1"].Text);
        Assert.Equal(["clarify"], loaded.Drafts["t1"].SuffixIds);
        var image = Assert.Single(loaded.Drafts["t1"].Images!);
        Assert.Equal("Pasted image", image.Name);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], image.Data);
        Assert.Null(loaded.Drafts["t2"].Images);
        Assert.Equal(["t1"], loaded.RunningTabIds);
        Assert.Equal(new WindowPlacement(10, 20, 1200, 800, false), loaded.Window);
        Assert.Null(RestartSnapshot.Load(path, "other", now));
        Assert.Null(RestartSnapshot.Load(path, "abc", now + RestartSnapshot.MaxAge + TimeSpan.FromSeconds(1)));

        File.WriteAllText(path, "{ not json");
        Assert.Null(RestartSnapshot.Load(path, "abc", now));
    }

    [Fact]
    public void An_updates_snapshot_waits_longer_and_is_taken_by_a_launch_without_its_nonce()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("restart.json");
        var now = new DateTimeOffset(Built);
        new RestartSnapshot { Nonce = "abc", CreatedAt = now, Update = new AppUpdateHandover("1.2.0", "1.3.0") }.Save(path);

        // Windows didn't start the new version, and the user opens Claudette hours later.
        var later = now.AddHours(5);
        Assert.Equal(new AppUpdateHandover("1.2.0", "1.3.0"), RestartSnapshot.Load(path, null, later)?.Update);
        Assert.NotNull(RestartSnapshot.Load(path, "abc", later));
        Assert.Null(RestartSnapshot.Load(path, null, now + RestartSnapshot.UpdateMaxAge + TimeSpan.FromSeconds(1)));

        // A source build's snapshot is only for the launch it names.
        new RestartSnapshot { Nonce = "abc", CreatedAt = now }.Save(path);
        Assert.Null(RestartSnapshot.Load(path, null, now));
    }

    [Fact]
    public void The_new_build_says_it_is_up_for_its_own_restart()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("data", "restart-ready");

        Assert.False(RestartHandshake.IsReady(path, "abc"));
        RestartHandshake.SignalReady(path, "abc");

        Assert.True(RestartHandshake.IsReady(path, "abc"));
        Assert.False(RestartHandshake.IsReady(path, "other"));
    }
}
