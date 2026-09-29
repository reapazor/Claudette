using System.Net;
using System.Security.Cryptography;
using Claudette.Core.Tests.Support;
using Claudette.Core.Updates;

namespace Claudette.Core.Tests.Updates;

/// <summary>Downloading a release's package, and only keeping it once it checks out (DESIGN.md §2, "Updating Claudette").</summary>
public sealed class UpdateDownloaderTests : IDisposable
{
    private const string Url = "https://example.test/Claudette-1.3.0-x64.msix";

    private static readonly AppVersion Version = new(1, 3, 0);

    private readonly TempFolder _temp = new();

    private static readonly byte[] Package = [.. Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251))];

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static ReleaseAsset Asset(long? size = null, string? sha256 = "") =>
        new("Claudette-1.3.0-x64.msix", Url, size ?? Package.Length, sha256 == "" ? Sha256(Package) : sha256);

    private UpdateDownloader Downloader(FakeHttpHandler http) => new(new HttpClient(http), _temp.Path, "Claudette/1.2.0");

    [Fact]
    public async Task A_package_that_matches_its_checksum_is_kept_and_progress_reaches_the_end()
    {
        var http = new FakeHttpHandler().OnBytes(Url, Package);
        var reported = new List<double>();

        var path = await Downloader(http).DownloadAsync(Asset(), Version, new SynchronousProgress(reported.Add), TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_temp.Path, "1.3.0", "Claudette-1.3.0-x64.msix"), path);
        Assert.Equal(Package, File.ReadAllBytes(path));
        Assert.Equal(1, reported[^1]);
        Assert.True(reported.Count > 2, "progress is reported as it downloads");
        Assert.Equal("Claudette/1.2.0", Assert.Single(http.Requests).Headers.UserAgent.ToString());
        Assert.False(File.Exists(path + ".part"));
    }

    [Fact]
    public async Task A_package_that_does_not_match_is_deleted()
    {
        var tampered = Package.ToArray();
        tampered[100] ^= 0xFF;
        var http = new FakeHttpHandler().OnBytes(Url, tampered);

        var error = await Assert.ThrowsAsync<UpdateDownloadException>(() => Downloader(http).DownloadAsync(Asset(), Version, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("checksum", error.Message);
        Assert.Empty(Directory.EnumerateFiles(_temp.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_short_download_is_not_kept()
    {
        var http = new FakeHttpHandler().OnBytes(Url, Package[..1000]);

        var error = await Assert.ThrowsAsync<UpdateDownloadException>(() => Downloader(http).DownloadAsync(Asset(sha256: null), Version, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal($"The download was incomplete: 1,000 of {Package.Length:N0} bytes.", error.Message);
        Assert.Empty(Directory.EnumerateFiles(_temp.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_failed_request_says_what_GitHub_answered()
    {
        var http = new FakeHttpHandler().On(Url, _ => new HttpResponseMessage(HttpStatusCode.BadGateway) { ReasonPhrase = "Bad Gateway" });

        var error = await Assert.ThrowsAsync<UpdateDownloadException>(() => Downloader(http).DownloadAsync(Asset(), Version, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("The download failed: GitHub answered 502 Bad Gateway.", error.Message);
    }

    [Fact]
    public async Task A_package_already_downloaded_is_not_fetched_again()
    {
        var http = new FakeHttpHandler().OnBytes(Url, Package);
        var first = await Downloader(http).DownloadAsync(Asset(), Version, cancellationToken: TestContext.Current.CancellationToken);

        var second = await Downloader(http).DownloadAsync(Asset(), Version, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
        Assert.Single(http.Requests);
    }

    [Fact]
    public void Clean_up_keeps_only_the_versions_asked_for()
    {
        _temp.Write("1.2.0/Claudette-1.2.0-x64.msix", "old");
        _temp.Write("1.3.0/Claudette-1.3.0-x64.msix", "new");
        _temp.Write("stray/file", "?");

        new UpdateDownloader(new HttpClient(new FakeHttpHandler()), _temp.Path, "Claudette/1.2.0").CleanUp(v => v > new AppVersion(1, 2, 0));

        Assert.Equal(["1.3.0"], Directory.EnumerateDirectories(_temp.Path).Select(Path.GetFileName));
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>Reports on the calling thread, unlike <see cref="Progress{T}"/>.</summary>
    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
