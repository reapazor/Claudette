using System.Buffers;
using System.Security.Cryptography;

namespace Claudette.Core.Updates;

/// <summary>Why a download failed, in words to show.</summary>
public sealed class UpdateDownloadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Downloads a release's package into <c>updates/&lt;version&gt;/</c> in the data folder (DESIGN.md §2, "Updating
/// Claudette"). The file is written under a temporary name and only renamed once its size and SHA-256 digest match what
/// GitHub reported, so a finished file is always a whole, verified one. A package already downloaded isn't fetched again.
/// </summary>
public sealed class UpdateDownloader(HttpClient http, string directory, string userAgent)
{
    private const int BufferSize = 81920;

    /// <summary>Downloads <paramref name="asset"/>, reporting progress from 0 to 1. Returns the file's path.</summary>
    public async Task<string> DownloadAsync(ReleaseAsset asset, AppVersion version, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var folder = Path.Combine(directory, version.ToString());
        // The name comes from GitHub: keep only its last part.
        var path = Path.Combine(folder, Path.GetFileName(asset.Name));
        if (File.Exists(path) && await MatchesAsync(path, asset, cancellationToken).ConfigureAwait(false))
        {
            progress?.Report(1);
            return path;
        }
        Directory.CreateDirectory(folder);
        var partial = path + ".part";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
            request.Headers.UserAgent.ParseAdd(userAgent);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateDownloadException($"The download failed: GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
            var total = asset.Size > 0 ? asset.Size : response.Content.Headers.ContentLength ?? 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    int read;
                    while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, read);
                        received += read;
                        if (total > 0)
                        {
                            progress?.Report(Math.Min(1, (double)received / total));
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            if (asset.Size > 0 && received != asset.Size)
            {
                throw new UpdateDownloadException($"The download was incomplete: {received:N0} of {asset.Size:N0} bytes.");
            }
            if (asset.Sha256 is { } expected && Convert.ToHexStringLower(hash.GetHashAndReset()) != expected)
            {
                throw new UpdateDownloadException("The download didn't match the checksum GitHub published for it, so it was deleted.");
            }
            File.Move(partial, path, overwrite: true);
            progress?.Report(1);
            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            throw new UpdateDownloadException($"The download failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateDownloadException("The download stopped responding.", ex);
        }
        finally
        {
            TryDelete(partial);
        }
    }

    /// <summary>Deletes the downloads <paramref name="keep"/> doesn't want, such as versions installed now or older.</summary>
    public void CleanUp(Func<AppVersion, bool> keep)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }
        foreach (var folder in Directory.EnumerateDirectories(directory))
        {
            if (AppVersion.TryParse(Path.GetFileName(folder)) is not { } version || !keep(version))
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use, perhaps by an installer: next time.
                }
            }
        }
    }

    private static async Task<bool> MatchesAsync(string path, ReleaseAsset asset, CancellationToken cancellationToken)
    {
        try
        {
            if (asset.Size > 0 && new FileInfo(path).Length != asset.Size)
            {
                return false;
            }
            if (asset.Sha256 is null)
            {
                return asset.Size > 0;
            }
            await using var stream = File.OpenRead(path);
            var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexStringLower(digest) == asset.Sha256;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
