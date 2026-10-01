using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Updates;

namespace Claudette.App.Tests;

/// <summary>The version at the foot of the Settings sidebar, and Report an issue (DESIGN.md §14, "Version").</summary>
public class VersionFooterTests
{
    [Fact]
    public async Task An_installed_Claudette_shows_its_version()
    {
        await using var h = new TabTestHarness(appVersion: new AppVersion(0, 1, 0));
        h.Services.BuildCommit = "842169b";
        var settings = new SettingsViewModel(h.Services, null);

        Assert.Equal("Claudette 0.1.0", settings.VersionLabel);
        Assert.StartsWith("Claudette 0.1.0.", settings.VersionTip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_source_build_shows_the_commit_it_was_built_from()
    {
        await using var h = new TabTestHarness(appVersion: new AppVersion(0, 1, 0));
        h.Services.BuildCommit = "842169b";
        h.Services.BuildConfiguration = null;
        h.Services.IsSourceBuild = true;
        var settings = new SettingsViewModel(h.Services, null);

        Assert.Equal("Claudette 0.1.0 · 842169b", settings.VersionLabel);
        Assert.StartsWith("Claudette 0.1.0 (source build 842169b), built from this checkout.", settings.VersionTip, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Debug")]
    [InlineData("Release")]
    public async Task A_source_builds_tooltip_says_which_configuration_it_was_built_in(string configuration)
    {
        await using var h = new TabTestHarness(appVersion: new AppVersion(0, 1, 0));
        h.Services.BuildCommit = "842169b";
        h.Services.BuildConfiguration = configuration;
        h.Services.IsSourceBuild = true;
        var settings = new SettingsViewModel(h.Services, null);

        Assert.StartsWith($"Claudette 0.1.0 (source build 842169b), a {configuration} build from this checkout.", settings.VersionTip, StringComparison.Ordinal);
        Assert.Equal("Claudette 0.1.0 · 842169b", settings.VersionLabel);
    }

    [Fact]
    public async Task The_configuration_is_the_one_the_app_was_built_in_and_only_source_builds_show_it()
    {
        await using var h = new TabTestHarness(appVersion: new AppVersion(0, 1, 0));
#if DEBUG
        Assert.Equal("Debug", h.Services.BuildConfiguration);
#else
        Assert.Equal("Release", h.Services.BuildConfiguration);
#endif
        var settings = new SettingsViewModel(h.Services, null);

        Assert.DoesNotContain(h.Services.BuildConfiguration!, settings.VersionTip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clicking_the_version_copies_the_versions_and_says_so_for_a_moment()
    {
        await using var h = new TabTestHarness();
        var settings = new SettingsViewModel(h.Services, null);

        await settings.CopyVersionCommand.ExecuteAsync(null);

        var lines = h.Platform.Clipboard!.Split(Environment.NewLine);
        Assert.Equal("Claudette: 0.1.0", lines[0]);
        Assert.StartsWith("Claude Code: ", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("OS: ", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("Runtime: .NET", lines[3], StringComparison.Ordinal);
        Assert.Equal("Copied", settings.VersionLabel);

        h.Time.Advance(SettingsViewModel.CopiedFor);

        Assert.Equal("Claudette 0.1.0", settings.VersionLabel);
    }

    [Fact]
    public async Task Report_an_issue_opens_a_new_issue_with_the_versions_and_nothing_else()
    {
        await using var h = new TabTestHarness(appInstaller: new NoAppInstaller(AppInstallKind.Msix));
        var settings = new SettingsViewModel(h.Services, null);

        await settings.ReportIssueCommand.ExecuteAsync(null);

        var url = Assert.Single(h.Platform.OpenedUrls);
        const string prefix = "https://github.com/reapazor/Claudette/issues/new?body=";
        Assert.StartsWith(prefix, url, StringComparison.Ordinal);
        var body = Uri.UnescapeDataString(url[prefix.Length..]);
        Assert.Contains("\nClaudette: 0.1.0 (MSIX)\n", body, StringComparison.Ordinal);
        // Only versions: not where Claudette keeps its data or where anything is installed.
        Assert.DoesNotContain(h.Services.Paths.DataDirectory, body, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copied_diagnostics_name_the_build()
    {
        await using var h = new TabTestHarness();
        h.Services.BuildCommit = "842169b";
        h.Services.IsSourceBuild = true;
        var settings = new SettingsViewModel(h.Services, null);

        await settings.Advanced.CopyDiagnosticsCommand.ExecuteAsync(null);

        Assert.StartsWith("Claudette 0.1.0 (source build 842169b)", h.Platform.Clipboard, StringComparison.Ordinal);
    }
}
