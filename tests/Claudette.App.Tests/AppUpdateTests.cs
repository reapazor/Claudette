using System.Net;
using System.Security.Cryptography;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Development;
using Claudette.Core.Settings;
using Claudette.Core.Updates;

namespace Claudette.App.Tests;

/// <summary>
/// Claudette updating itself from its GitHub releases, and handing its tabs to the new version the way a source build
/// hands them to a new build (DESIGN.md §2, "Updating Claudette"). GitHub and the installer are fakes.
/// </summary>
public class AppUpdateTests
{
    private const string ReleasesUrl = "https://api.github.com/repos/reapazor/Claudette/releases?per_page=20";

    private static readonly byte[] Package = [.. Enumerable.Range(0, 5000).Select(i => (byte)i)];

    private static readonly AppVersion Current = new(1, 2, 0);

    private static string Release(string version, bool prerelease = false, string arch = "x64", bool digest = true) => $$"""
        {
          "tag_name": "v{{version}}", "name": "Claudette {{version}}", "draft": false, "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/reapazor/Claudette/releases/tag/v{{version}}", "published_at": "2026-09-20T09:30:00Z",
          "body": "- Better things",
          "assets": [ {
            "name": "Claudette-{{version}}-{{arch}}.msix", "browser_download_url": "https://example.test/{{version}}.msix", "size": {{Package.Length}},
            "digest": {{(digest ? $"\"sha256:{Convert.ToHexStringLower(SHA256.HashData(Package))}\"" : "null")}}
          } ]
        }
        """;

    /// <summary>GitHub: whatever releases the test says, and their packages.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public List<string> Releases { get; } = [];

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests)
            {
                Requests.Add(url);
            }
            return Task.FromResult(url == ReleasesUrl
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"[{string.Join(',', Releases)}]") }
                : url.StartsWith("https://example.test/", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Package) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class FakeInstaller(AppInstallKind kind = AppInstallKind.Msix) : IAppInstaller
    {
        public AppInstallKind Kind => kind;

        public List<string> Prepared { get; } = [];

        public List<IReadOnlyList<string>> Installed { get; } = [];

        public AppInstallException? PrepareFailure { get; set; }

        public AppInstallException? InstallFailure { get; set; }

        public InstallOutcome Outcome { get; set; } = InstallOutcome.ExitNow;

        /// <summary>What the new version does when it starts: given the ready file and the nonce.</summary>
        public Action<string, string>? NewVersionStarts { get; set; }

        public Task<PreparedInstall> PrepareAsync(string packagePath, AppVersion version, CancellationToken cancellationToken = default)
        {
            Prepared.Add(packagePath);
            return PrepareFailure is { } failure ? Task.FromException<PreparedInstall>(failure) : Task.FromResult(new PreparedInstall(version, packagePath));
        }

        public Task<InstallOutcome> InstallAsync(PreparedInstall prepared, IReadOnlyList<string> restartArguments, string readyFile, string nonce, CancellationToken cancellationToken = default)
        {
            Installed.Add(restartArguments);
            if (InstallFailure is { } failure)
            {
                return Task.FromException<InstallOutcome>(failure);
            }
            NewVersionStarts?.Invoke(readyFile, nonce);
            return Task.FromResult(Outcome);
        }
    }

    /// <summary>The window's side, over the harness's shell.</summary>
    private sealed class ShellHost(ShellViewModel shell) : IRestartHost
    {
        public bool Exited { get; private set; }

        public string? ClosedWith { get; private set; }

        public bool AnyTabWorking => shell.AnyTabWorking;

        public event Action? TabsChanged
        {
            add => shell.TabStatusChanged += value;
            remove => shell.TabStatusChanged -= value;
        }

        public RestartSnapshot Capture() => shell.CaptureForRestart();

        public Task CloseTabsAsync(string message)
        {
            ClosedWith = message;
            return shell.CloseTabsForRestartAsync();
        }

        public void Recover(RestartSnapshot snapshot) => shell.Restore(null, snapshot);

        public void Exit() => Exited = true;
    }

    private sealed class Setup(TabTestHarness harness, FakeGitHub gitHub, FakeInstaller installer, ShellHost host, AppUpdateService updates, AppUpdateViewModel badge) : IAsyncDisposable
    {
        public TabTestHarness H => harness;
        public FakeGitHub GitHub => gitHub;
        public FakeInstaller Installer => installer;
        public ShellHost Host => host;
        public AppUpdateService Updates => updates;
        public AppUpdateViewModel Badge => badge;

        public async ValueTask DisposeAsync()
        {
            badge.Dispose();
            updates.Dispose();
            await harness.DisposeAsync();
        }
    }

    private static Setup Create(AppInstallKind kind = AppInstallKind.Msix, bool isSourceBuild = false, AppVersion? version = null, params string[] releases)
    {
        var gitHub = new FakeGitHub();
        gitHub.Releases.AddRange(releases.Length > 0 ? releases : [Release("1.3.0")]);
        var installer = new FakeInstaller(kind);
        IAppInstaller appInstaller = kind == AppInstallKind.Other ? new NoAppInstaller() : installer;
        var h = new TabTestHarness(appInstaller: appInstaller, http: gitHub, appVersion: version ?? Current);
        var host = new ShellHost(h.Shell);
        // The releases here have x64 packages, whatever machine runs the tests.
        var updates = new AppUpdateService(h.Services, host, isSourceBuild, architecture: System.Runtime.InteropServices.Architecture.X64);
        return new Setup(h, gitHub, installer, host, updates, new AppUpdateViewModel(updates));
    }

    /// <summary>A pinned tab, selected and running, and an unpinned one with a half-typed message.</summary>
    private static async Task<(TabViewModel Pinned, TabViewModel Unpinned)> OpenTabsAsync(TabTestHarness h)
    {
        h.Services.Settings.Sessions.RestoreUnpinnedTabs = true;
        h.Services.State.Tabs = [new TabState { Folder = h.WorkFolder, IsPinned = true, UserName = "pinned" }, new TabState { Folder = h.WorkFolder, UserName = "unpinned" }];
        h.Shell.Restore(null);
        h.Services.Settings.Sessions.RestoreUnpinnedTabs = false;
        var (pinned, unpinned) = (h.Shell.AllTabs.First(), h.Shell.AllTabs.Last());
        await TabTestHarness.Eventually(() => pinned.Status == TabStatus.Idle && pinned.IsSettled, "the selected tab to start");
        unpinned.ComposerText = "half-typed";
        return (pinned, unpinned);
    }

    [Fact]
    public async Task A_newer_release_is_announced_and_skipping_it_waits_for_a_newer_one()
    {
        await using var s = Create();
        Assert.False(s.Badge.HasBadge);

        await s.Updates.CheckNowAsync();

        Assert.Equal("1.3.0", s.Updates.Available?.Version.ToString());
        Assert.True(s.Badge.HasBadge);
        Assert.Equal("Claudette 1.3.0 is available", s.Badge.BadgeText);
        Assert.Equal("- Better things", s.Badge.NotesText);
        Assert.Equal("You have Claudette 1.2.0.", s.Badge.CurrentVersionText);
        Assert.True(s.Badge.CanRestartToUpdate);
        Assert.Equal("Download and restart", s.Badge.RestartLabel);

        s.Badge.SkipCommand.Execute(null);
        Assert.False(s.Badge.HasBadge);
        Assert.Equal("1.3.0", s.H.Services.State.SkippedAppUpdate);
        Assert.Equal("Claudette 1.3.0 is available; you skipped it.", s.Badge.CheckStatusText);

        s.GitHub.Releases.Insert(0, Release("1.3.1"));
        await s.Updates.CheckNowAsync();
        Assert.True(s.Badge.HasBadge);
        Assert.Equal("Claudette 1.3.1 is available", s.Badge.BadgeText);
    }

    [Fact]
    public async Task Up_to_date_says_so_and_pre_releases_count_only_when_asked_for()
    {
        await using var s = Create(releases: [Release("1.3.0-beta.1", prerelease: true), Release("1.2.0")]);

        await s.Updates.CheckNowAsync();
        Assert.Null(s.Updates.Available);
        Assert.False(s.Badge.HasBadge);
        Assert.StartsWith("Claudette is up to date.", s.Badge.CheckStatusText);

        s.H.Services.Settings.General.IncludePrereleases = true;
        await s.Updates.CheckNowAsync();
        Assert.Equal("1.3.0-beta.1", s.Updates.Available?.Version.ToString());
    }

    [Fact]
    public async Task A_source_build_never_asks_GitHub()
    {
        await using var s = Create(isSourceBuild: true);

        s.Updates.Start();
        await s.Updates.CheckNowAsync();

        Assert.Empty(s.GitHub.Requests);
        Assert.False(s.Badge.HasBadge);
        Assert.False(s.Badge.CanCheck);
        Assert.Contains("source build", s.Badge.CheckStatusText);
    }

    [Fact]
    public async Task Checks_run_at_launch_only_while_the_setting_is_on()
    {
        await using var s = Create();
        s.H.Services.Settings.General.CheckForAppUpdates = false;

        s.Updates.Start();
        s.H.Time.Advance(AppUpdateService.CheckInterval * 2);
        Assert.DoesNotContain(ReleasesUrl, s.GitHub.Requests);

        s.H.Services.Settings.General.CheckForAppUpdates = true;
        s.Updates.OnSettingsChanged();
        await TabTestHarness.Eventually(() => s.Updates.Available is not null, "the check at launch");
        // A tick while a check is still finishing joins it instead of starting another, so let it finish first.
        await s.Updates.LastCheck!;
        var checks = s.GitHub.Requests.Count(r => r == ReleasesUrl);

        s.H.Time.Advance(AppUpdateService.CheckInterval);
        await TabTestHarness.Eventually(() => s.GitHub.Requests.Count(r => r == ReleasesUrl) > checks, "the next check");
    }

    [Fact]
    public async Task Restarting_to_update_downloads_the_package_and_hands_every_tab_to_the_new_version()
    {
        await using var s = Create();
        var (pinned, unpinned) = await OpenTabsAsync(s.H);
        await s.Updates.CheckNowAsync();

        Assert.True(await s.Updates.InstallAsync());

        var package = Assert.Single(s.Installer.Prepared);
        Assert.Equal(Package, File.ReadAllBytes(package));
        Assert.StartsWith(s.H.Services.Paths.UpdatesDirectory, package, StringComparison.Ordinal);
        var arguments = Assert.Single(s.Installer.Installed);
        Assert.Equal(LaunchArguments.RestoreOption, arguments[0]);
        var snapshot = RestartSnapshot.Load(s.H.Services.Paths.RestartFile, arguments[1], s.H.Time.GetUtcNow());
        Assert.NotNull(snapshot);
        Assert.Equal(new AppUpdateHandover("1.2.0", "1.3.0"), snapshot.Update);
        Assert.Equal(["pinned", "unpinned"], snapshot.Tabs.Select(t => t.UserName));
        Assert.Equal([pinned.Id], snapshot.RunningTabIds);
        Assert.Equal("half-typed", snapshot.Drafts[unpinned.Id].Text);

        Assert.Equal("Installing Claudette 1.3.0…", s.Host.ClosedWith);
        Assert.Empty(s.H.Shell.AllTabs);
        Assert.True(s.H.Services.SuspendSaving);
        Assert.True(s.Host.Exited);
    }

    [Fact]
    public async Task A_new_version_Claudette_starts_itself_has_to_say_it_is_up()
    {
        await using var s = Create();
        await OpenTabsAsync(s.H);
        await s.Updates.CheckNowAsync();
        s.Installer.Outcome = InstallOutcome.WaitForNewVersion;
        s.Installer.NewVersionStarts = RestartHandshake.SignalReady;

        Assert.True(await s.Updates.InstallAsync());

        Assert.True(s.Host.Exited);
    }

    [Fact]
    public async Task When_the_install_fails_the_tabs_come_back_and_the_package_can_be_installed_by_hand()
    {
        await using var s = Create();
        await OpenTabsAsync(s.H);
        await s.Updates.CheckNowAsync();
        s.Installer.InstallFailure = new AppInstallException("Windows doesn't trust the certificate the update is signed with.", canOpenPackage: true);

        Assert.False(await s.Updates.InstallAsync());

        Assert.False(s.Host.Exited);
        Assert.Equal(["pinned", "unpinned"], s.H.Shell.AllTabs.Select(t => t.DisplayName));
        Assert.Equal("half-typed", s.H.Shell.AllTabs.Last().ComposerText);
        Assert.False(s.H.Services.SuspendSaving);
        Assert.False(File.Exists(s.H.Services.Paths.RestartFile));
        Assert.Equal("Windows doesn't trust the certificate the update is signed with.", s.Badge.ErrorText);
        Assert.True(s.Badge.CanOpenPackage);
        Assert.Equal("Open the installer", s.Badge.OpenPackageLabel);
        Assert.Equal("Restart to update", s.Badge.RestartLabel);

        await s.Badge.OpenPackageCommand.ExecuteAsync(null);
        Assert.Equal([s.Updates.DownloadedPath!], s.H.Platform.OpenedFiles);
    }

    [Fact]
    public async Task A_package_that_fails_its_checks_leaves_the_tabs_alone()
    {
        await using var s = Create();
        await OpenTabsAsync(s.H);
        await s.Updates.CheckNowAsync();
        s.Installer.PrepareFailure = new AppInstallException("The download is Contoso.Other, not Claudette.");

        Assert.False(await s.Updates.InstallAsync());

        Assert.Null(s.Host.ClosedWith);
        Assert.Empty(s.Installer.Installed);
        Assert.Equal(2, s.H.Shell.AllTabs.Count());
        Assert.Equal("The download is Contoso.Other, not Claudette.", s.Badge.ErrorText);
        Assert.False(s.Badge.CanOpenPackage);
    }

    [Fact]
    public async Task Update_when_idle_waits_for_the_working_tab()
    {
        await using var s = Create();
        var (pinned, _) = await OpenTabsAsync(s.H);
        await s.Updates.CheckNowAsync();
        pinned.Status = TabStatus.Working;
        Assert.True(s.Badge.CanUpdateWhenIdle);
        Assert.NotNull(s.Badge.WorkingText);

        s.Badge.UpdateWhenIdleCommand.Execute(null);
        Assert.Empty(s.Installer.Installed);
        Assert.Equal("Updating when idle…", s.Badge.BadgeText);

        pinned.Status = TabStatus.Idle;

        await TabTestHarness.Eventually(() => s.Host.Exited, "the update");
        Assert.Single(s.Installer.Installed);
    }

    [Fact]
    public async Task A_copy_that_cannot_install_updates_links_to_the_release()
    {
        await using var s = Create(AppInstallKind.Other);

        await s.Updates.CheckNowAsync();

        Assert.True(s.Badge.HasBadge);
        Assert.False(s.Badge.CanRestartToUpdate);
        Assert.False(s.Badge.CanDownload);
        Assert.Equal("This copy of Claudette can't update itself. Download the new version from its release page.", s.Badge.ManualText);
        await s.Badge.OpenReleasePageCommand.ExecuteAsync(null);
        Assert.Equal(["https://github.com/reapazor/Claudette/releases/tag/v1.3.0"], s.H.Platform.OpenedUrls);
        Assert.DoesNotContain(s.GitHub.Requests, r => r.StartsWith("https://example.test/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_release_without_a_package_for_this_computer_is_announced_but_not_installed()
    {
        await using var s = Create(releases: [Release("1.3.0", arch: "arm64")]);

        await s.Updates.CheckNowAsync();

        Assert.True(s.Badge.HasBadge);
        Assert.False(s.Badge.CanRestartToUpdate);
        Assert.Equal("This release has no package for this computer yet. See its release page.", s.Badge.ManualText);
    }

    [Fact]
    public async Task A_package_without_a_checksum_is_announced_but_not_downloaded()
    {
        await using var s = Create(releases: [Release("1.3.0", digest: false)]);

        await s.Updates.CheckNowAsync();

        Assert.True(s.Badge.HasBadge);
        Assert.False(s.Badge.CanRestartToUpdate);
        Assert.False(s.Badge.CanDownload);
        Assert.Equal("GitHub published no checksum for this release's package, so Claudette can't check it. Download it from its release page.", s.Badge.ManualText);
        Assert.False(await s.Updates.InstallAsync());
        Assert.DoesNotContain(s.GitHub.Requests, r => r.StartsWith("https://example.test/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task After_an_update_the_new_version_says_where_it_came_from()
    {
        await using var s = Create(version: new AppVersion(1, 3, 0));

        s.Updates.OnRestoredAfterUpdate(new AppUpdateHandover("1.2.0", "1.3.0"));

        Assert.Equal("You have Claudette 1.3.0, updated from 1.2.0.", s.Badge.CurrentVersionText);
        Assert.False(s.Badge.HasBadge);
    }

    [Fact]
    public async Task An_update_that_did_not_install_says_so_when_Claudette_opens_again()
    {
        await using var s = Create();

        s.Updates.OnRestoredAfterUpdate(new AppUpdateHandover("1.2.0", "1.3.0"));

        Assert.True(s.Badge.HasBadge);
        Assert.Equal("Update didn't install", s.Badge.BadgeText);
        Assert.Equal("The update to Claudette 1.3.0 didn't finish, so this is still 1.2.0. Your tabs are back.", s.Badge.ErrorText);
    }

    [Fact]
    public async Task A_failed_check_is_shown_in_Settings_but_not_announced()
    {
        await using var s = Create();
        s.GitHub.Releases.Clear();
        s.GitHub.Releases.Add("{ oops");

        await s.Updates.CheckNowAsync();

        Assert.Equal("GitHub's answer couldn't be read.", s.Badge.CheckStatusText);
        Assert.False(s.Badge.HasBadge);
    }
}
