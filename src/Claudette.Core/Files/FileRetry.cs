namespace Claudette.Core.Files;

/// <summary>
/// Tries a file operation again while another process has the file open: on Windows a rename over a file, or a read of
/// one, fails while an antivirus scanner, a backup or sync client, the search indexer or another Claudette has it open,
/// which usually lasts milliseconds and sometimes a second or two.
/// </summary>
/// <remarks>
/// This waits in real time, not on an injected <see cref="TimeProvider"/>: it's waiting out another process, which no
/// clock of Claudette's can hurry, and a fake clock would hang it instead. It only waits after a failure.
/// </remarks>
public static class FileRetry
{
    /// <summary>The waits between attempts: about two seconds in all.</summary>
    private static readonly int[] DelaysMs = [5, 10, 20, 40, 80, 160, 250, 400, 500, 600];

    /// <summary>Runs <paramref name="action"/>, trying again while the file is in use; the last failure is thrown.</summary>
    public static void Run(Action action) => Run<object?>(() =>
    {
        action();
        return null;
    });

    /// <inheritdoc cref="Run"/>
    public static T Run<T>(Func<T> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (IsInUse(ex) && attempt < DelaysMs.Length)
            {
#pragma warning disable RS0030 // Waits out another process (see the remarks).
                Thread.Sleep(DelaysMs[attempt]);
#pragma warning restore RS0030
            }
        }
    }

    /// <inheritdoc cref="Run"/>
    public static async Task RunAsync(Func<Task> action, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await action().ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsInUse(ex) && attempt < DelaysMs.Length)
            {
#pragma warning disable RS0030 // Waits out another process (see the remarks).
                await Task.Delay(DelaysMs[attempt], cancellationToken).ConfigureAwait(false);
#pragma warning restore RS0030
            }
        }
    }

    /// <summary>
    /// A failure that may pass: the file is in use or briefly locked. A missing file or folder isn't one, and neither is
    /// a full disk.
    /// </summary>
    public static bool IsInUse(Exception ex) =>
        ex is UnauthorizedAccessException
        || ex is IOException and not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException or EndOfStreamException)
            && !IsDiskFull(ex);

    private static bool IsDiskFull(Exception ex) => OperatingSystem.IsWindows()
        ? (ex.HResult & 0xFFFF) is 0x70 or 0x27 // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
        : ex.HResult == 28; // ENOSPC
}
