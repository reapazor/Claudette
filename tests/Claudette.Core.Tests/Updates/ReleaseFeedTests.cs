using System.Net;
using System.Runtime.InteropServices;
using Claudette.Core.Tests.Support;
using Claudette.Core.Updates;

namespace Claudette.Core.Tests.Updates;

/// <summary>Reading Claudette's releases from GitHub and picking the one to offer (DESIGN.md §2, "Updating Claudette").</summary>
public class ReleaseFeedTests
{
    private const string Url = "https://api.github.com/repos/reapazor/Claudette/releases?per_page=20";

    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>As GitHub's REST API answers, trimmed, with a field Claudette doesn't know.</summary>
    public static readonly string Releases = $$"""
        [
          {
            "tag_name": "v1.4.0-beta.1", "name": "Claudette 1.4.0 beta 1", "draft": false, "prerelease": true,
            "html_url": "https://github.com/reapazor/Claudette/releases/tag/v1.4.0-beta.1", "published_at": "2026-09-28T10:00:00Z",
            "body": "Trying things.", "reactions": { "+1": 3 },
            "assets": [ { "name": "Claudette-1.4.0-beta.1-x64.msix", "browser_download_url": "https://example.test/beta.msix", "size": 10 } ]
          },
          {
            "tag_name": "v1.3.0", "name": "Claudette 1.3.0", "draft": false, "prerelease": false,
            "html_url": "https://github.com/reapazor/Claudette/releases/tag/v1.3.0", "published_at": "2026-09-20T09:30:00Z",
            "body": "## What's new\n- Updates itself",
            "assets": [
              { "name": "Claudette-1.3.0-x64.msix", "browser_download_url": "https://example.test/x64.msix", "size": 1234, "digest": "sha256:{{Digest}}" },
              { "name": "Claudette-1.3.0-arm64.msix", "browser_download_url": "https://example.test/arm64.msix", "size": 1200, "digest": null },
              { "name": "Claudette-1.3.0-arm64.dmg", "browser_download_url": "https://example.test/arm64.dmg", "size": 2000 },
              { "name": "Claudette-1.3.0-x64.dmg", "browser_download_url": "https://example.test/x64.dmg", "size": 2100 }
            ]
          },
          { "tag_name": "nightly", "name": "Not a version", "draft": false, "prerelease": false, "assets": [] },
          { "tag_name": "v9.0.0", "name": "A draft", "draft": true, "prerelease": false, "assets": [] },
          { "tag_name": "v1.2.0", "draft": false, "prerelease": false, "assets": [] }
        ]
        """;

    private static readonly AppVersion Current = new(1, 2, 0);

    [Fact]
    public void Releases_are_read_skipping_drafts_and_tags_that_are_not_versions()
    {
        var releases = ReleaseFeed.Parse(Releases);

        Assert.Equal(["1.4.0-beta.1", "1.3.0", "1.2.0"], releases.Select(r => r.Version.ToString()));
        var release = releases[1];
        Assert.Equal("Claudette 1.3.0", release.Name);
        Assert.Equal("## What's new\n- Updates itself", release.Notes);
        Assert.Equal("https://github.com/reapazor/Claudette/releases/tag/v1.3.0", release.PageUrl);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 9, 30, 0, TimeSpan.Zero), release.PublishedAt);
        Assert.False(release.IsPrerelease);
        Assert.True(releases[0].IsPrerelease);
        Assert.Equal(new ReleaseAsset("Claudette-1.3.0-x64.msix", "https://example.test/x64.msix", 1234, Digest), release.Assets[0]);
        Assert.Null(release.Assets[1].Sha256);
        // A release without a name is called by its tag.
        Assert.Equal("v1.2.0", releases[2].Name);
    }

    [Fact]
    public void Anything_but_a_list_reads_as_no_releases() => Assert.Empty(ReleaseFeed.Parse("""{ "message": "Moved" }"""));

    [Fact]
    public async Task The_releases_are_asked_for_as_GitHub_documents_with_a_user_agent()
    {
        var http = new FakeHttpHandler().OnJson(Url, Releases);
        var feed = new ReleaseFeed(new HttpClient(http), "Claudette/1.2.0");

        var releases = await feed.GetReleasesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, releases.Count);
        var request = Assert.Single(http.Requests);
        Assert.Equal("Claudette/1.2.0", request.Headers.UserAgent.ToString());
        Assert.Contains("application/vnd.github+json", request.Headers.Accept.ToString());
        Assert.Equal("2022-11-28", request.Headers.GetValues("X-GitHub-Api-Version").Single());
        Assert.Equal("https://github.com/reapazor/Claudette/releases", feed.ReleasesPage);
    }

    [Fact]
    public async Task GitHub_saying_no_is_explained()
    {
        var http = new FakeHttpHandler().On(Url, _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("x-ratelimit-remaining", "0");
            return response;
        });
        var limited = await Assert.ThrowsAsync<ReleaseFeedException>(() => new ReleaseFeed(new HttpClient(http), "Claudette/1.2.0").GetReleasesAsync(TestContext.Current.CancellationToken));
        Assert.Contains("limit", limited.Message);

        var missing = await Assert.ThrowsAsync<ReleaseFeedException>(() => new ReleaseFeed(new HttpClient(new FakeHttpHandler()), "Claudette/1.2.0").GetReleasesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("GitHub has no repository reapazor/Claudette.", missing.Message);

        var broken = new FakeHttpHandler().On(Url, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") });
        var unreadable = await Assert.ThrowsAsync<ReleaseFeedException>(() => new ReleaseFeed(new HttpClient(broken), "Claudette/1.2.0").GetReleasesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("GitHub's answer couldn't be read.", unreadable.Message);

        var offline = new FakeHttpHandler().On(Url, _ => throw new HttpRequestException("No such host is known."));
        var unreachable = await Assert.ThrowsAsync<ReleaseFeedException>(() => new ReleaseFeed(new HttpClient(offline), "Claudette/1.2.0").GetReleasesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Couldn't reach GitHub: No such host is known.", unreachable.Message);
    }

    [Fact]
    public void The_newest_full_release_above_this_version_is_offered_with_this_machines_package()
    {
        var releases = ReleaseFeed.Parse(Releases);

        var update = AvailableUpdate.Find(releases, Current, includePrereleases: false, AppInstallKind.Msix, Architecture.X64);

        Assert.Equal("1.3.0", update?.Version.ToString());
        Assert.Equal("Claudette-1.3.0-x64.msix", update?.Asset?.Name);
        Assert.Equal("Claudette-1.3.0-arm64.dmg", AvailableUpdate.PickAsset(releases[1], AppInstallKind.MacApp, Architecture.Arm64)?.Name);
        Assert.Equal("Claudette-1.3.0-arm64.msix", AvailableUpdate.PickAsset(releases[1], AppInstallKind.Msix, Architecture.Arm64)?.Name);
    }

    [Fact]
    public void Pre_releases_are_offered_only_when_asked_for()
    {
        var releases = ReleaseFeed.Parse(Releases);

        Assert.Equal("1.4.0-beta.1", AvailableUpdate.Find(releases, Current, includePrereleases: true, AppInstallKind.Msix, Architecture.X64)?.Version.ToString());
        Assert.Equal("1.3.0", AvailableUpdate.Find(releases, Current, includePrereleases: false, AppInstallKind.Msix, Architecture.X64)?.Version.ToString());
    }

    [Fact]
    public void Nothing_is_offered_when_this_version_is_the_newest() =>
        Assert.Null(AvailableUpdate.Find(ReleaseFeed.Parse(Releases), new AppVersion(1, 3, 0), includePrereleases: false, AppInstallKind.Msix, Architecture.X64));

    [Fact]
    public void A_release_is_still_offered_where_there_is_no_package_to_install_but_without_one()
    {
        var releases = ReleaseFeed.Parse(Releases);

        var linux = AvailableUpdate.Find(releases, Current, includePrereleases: false, AppInstallKind.Other, Architecture.X64);
        var beta = AvailableUpdate.Find(releases, Current, includePrereleases: true, AppInstallKind.MacApp, Architecture.Arm64);

        Assert.Equal("1.3.0", linux?.Version.ToString());
        Assert.Null(linux?.Asset);
        Assert.Equal("1.4.0-beta.1", beta?.Version.ToString());
        Assert.Null(beta?.Asset);
    }
}
