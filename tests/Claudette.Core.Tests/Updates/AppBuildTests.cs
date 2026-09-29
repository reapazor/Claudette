using Claudette.Core.Updates;

namespace Claudette.Core.Tests.Updates;

/// <summary>This copy of Claudette as the Settings sidebar and a bug report give it (DESIGN.md §14, "Version").</summary>
public class AppBuildTests
{
    private static readonly AppVersion Version = new(0, 1, 0);

    [Theory]
    [InlineData("0.1.0+842169b0c3d2e4f5a6b7c8d9e0f1a2b3c4d5e6f7", "842169b")]
    [InlineData("0.1.0-beta.1+842169B0C3D2", "842169b")]
    [InlineData("0.1.0+build.5.842169b0", "842169b")]
    [InlineData("0.1.0", null)]
    [InlineData("0.1.0+", null)]
    [InlineData("0.1.0+local", null)]
    [InlineData("0.1.0+8421", null)]
    [InlineData(null, null)]
    public void The_commit_comes_from_the_informational_version(string? informational, string? commit) =>
        Assert.Equal(commit, AppBuild.CommitOf(informational));

    [Fact]
    public void Only_a_source_build_shows_its_commit_in_the_label()
    {
        Assert.Equal("Claudette 0.1.0 · 842169b", new AppBuild(Version, AppInstallKind.SourceBuild, "842169b").Label);
        Assert.Equal("Claudette 0.1.0", new AppBuild(Version, AppInstallKind.SourceBuild).Label);
        Assert.Equal("Claudette 0.1.0", new AppBuild(Version, AppInstallKind.Msix, "842169b").Label);
        Assert.Equal("Claudette 0.2.0-beta.1", new AppBuild(new AppVersion(0, 2, 0, "beta.1"), AppInstallKind.MacApp, "842169b").Label);
    }

    [Theory]
    [InlineData(AppInstallKind.SourceBuild, "842169b", "0.1.0 (source build 842169b)")]
    [InlineData(AppInstallKind.SourceBuild, null, "0.1.0 (source build)")]
    [InlineData(AppInstallKind.Msix, "842169b", "0.1.0 (MSIX)")]
    [InlineData(AppInstallKind.MacApp, null, "0.1.0 (.dmg)")]
    [InlineData(AppInstallKind.Other, "842169b", "0.1.0")]
    public void The_description_says_how_it_was_installed(AppInstallKind kind, string? commit, string expected) =>
        Assert.Equal(expected, new AppBuild(Version, kind, commit).Description);

    [Fact]
    public void The_details_are_versions_one_per_line()
    {
        var details = new AppBuild(Version, AppInstallKind.Msix).Details("2.1.284", " Microsoft Windows 10.0.26100 ", "win-x64", ".NET 10.0.1");

        Assert.Equal(["Claudette: 0.1.0 (MSIX)", "Claude Code: 2.1.284", "OS: Microsoft Windows 10.0.26100 (win-x64)", "Runtime: .NET 10.0.1"], details);
        Assert.Equal("Claude Code: not found", new AppBuild(Version, AppInstallKind.Other).Details(null, "Linux", "linux-x64", ".NET 10.0.1")[1]);
    }

    [Fact]
    public void A_new_issue_has_the_outline_and_the_details_in_its_body()
    {
        var url = AppBuild.NewIssueUrl("reapazor/Claudette", ["Claudette: 0.1.0 (source build 842169b)", "Claude Code: 2.1.284"]);

        const string prefix = "https://github.com/reapazor/Claudette/issues/new?body=";
        Assert.StartsWith(prefix, url, StringComparison.Ordinal);
        var body = Uri.UnescapeDataString(url[prefix.Length..]);
        Assert.StartsWith("**What happened**\n", body, StringComparison.Ordinal);
        Assert.Contains("**Steps to reproduce**", body, StringComparison.Ordinal);
        Assert.EndsWith("---\nClaudette: 0.1.0 (source build 842169b)\nClaude Code: 2.1.284", body, StringComparison.Ordinal);
        // Escaped, so the body can't add query parameters of its own.
        Assert.DoesNotContain(' ', url);
        Assert.DoesNotContain('&', url);
        Assert.Equal(2, url.Split('?').Length);
    }
}
