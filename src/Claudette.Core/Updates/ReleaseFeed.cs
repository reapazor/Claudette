using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Claudette.Core.Updates;

/// <summary>A published release of Claudette on GitHub.</summary>
/// <param name="Notes">The release notes, as GitHub Markdown.</param>
/// <param name="PageUrl">The release's page on GitHub.</param>
public sealed record AppRelease(
    AppVersion Version,
    string Tag,
    string Name,
    string Notes,
    string PageUrl,
    DateTimeOffset? PublishedAt,
    bool IsPrerelease,
    IReadOnlyList<ReleaseAsset> Assets);

/// <summary>A file attached to a release: a package for one platform and architecture.</summary>
/// <param name="Sha256">The SHA-256 digest GitHub reports for the file, as lowercase hex; null when it gives none.</param>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size, string? Sha256);

/// <summary>Why the releases couldn't be read, in words to show.</summary>
public sealed class ReleaseFeedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Claudette's releases, from the GitHub REST API (DESIGN.md §2, "Updating Claudette"). Unauthenticated: GitHub allows
/// 60 requests an hour from one address, and Claudette checks every few hours. Drafts aren't visible without a token,
/// so a release is offered once it's published.
/// </summary>
public sealed class ReleaseFeed(HttpClient http, string userAgent, string repository = ReleaseFeed.DefaultRepository, string apiBase = "https://api.github.com")
{
    public const string DefaultRepository = "reapazor/Claudette";

    /// <summary>The releases page, for builds that can't install an update themselves.</summary>
    public string ReleasesPage => $"https://github.com/{repository}/releases";

    /// <summary>The most recent releases, newest first, skipping drafts and tags that aren't versions.</summary>
    public async Task<IReadOnlyList<AppRelease>> GetReleasesAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase.TrimEnd('/')}/repos/{repository}/releases?per_page=20");
        request.Headers.UserAgent.ParseAdd(userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ReleaseFeedException($"Couldn't reach GitHub: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ReleaseFeedException("GitHub didn't answer in time.", ex);
        }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                && response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0")
            {
                throw new ReleaseFeedException("GitHub's limit on checks from this network was reached. Claudette tries again later.");
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new ReleaseFeedException($"GitHub has no repository {repository}.");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new ReleaseFeedException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return Parse(json);
            }
            catch (JsonException ex)
            {
                throw new ReleaseFeedException("GitHub's answer couldn't be read.", ex);
            }
        }
    }

    /// <summary>Reads the <c>GET /repos/{owner}/{repo}/releases</c> answer. Unknown fields are ignored.</summary>
    public static IReadOnlyList<AppRelease> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var releases = new List<AppRelease>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || Bool(item, "draft") || Text(item, "tag_name") is not { } tag || AppVersion.TryParse(tag) is not { } version)
            {
                continue;
            }
            var assets = new List<ReleaseAsset>();
            if (item.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in list.EnumerateArray())
                {
                    if (Text(asset, "name") is { } name && Text(asset, "browser_download_url") is { } url)
                    {
                        var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
                        assets.Add(new ReleaseAsset(name, url, size, Sha256(Text(asset, "digest"))));
                    }
                }
            }
            var published = Text(item, "published_at") is { } at && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
                ? when
                : (DateTimeOffset?)null;
            releases.Add(new AppRelease(version, tag, Text(item, "name") is { Length: > 0 } title ? title : tag, Text(item, "body") ?? "",
                Text(item, "html_url") ?? "", published, Bool(item, "prerelease"), assets));
        }
        return releases;
    }

    /// <summary>GitHub gives the digest as <c>sha256:&lt;hex&gt;</c>.</summary>
    private static string? Sha256(string? digest) =>
        digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && digest[7..] is { Length: 64 } hex && hex.All(char.IsAsciiHexDigit)
            ? hex.ToLowerInvariant()
            : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
