using System.Text;

namespace Claudette.Core.Processes;

/// <summary>The outcome of a short command run to completion.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs short commands (such as <c>claude --version</c>) to completion and collects their output.</summary>
public static class ProcessRunner
{
    /// <param name="onLine">Called with each line of output as it arrives, from either stream, on a background thread.</param>
    public static async Task<ProcessResult> RunAsync(
        IProcessLauncher launcher,
        ProcessStartSpec spec,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default,
        Action<string>? onLine = null)
    {
        await using var process = launcher.Start(spec);
        process.CloseStandardInput();

        using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        var stdout = ReadAllAsync(process.StandardOutput, onLine, linked.Token);
        var stderr = ReadAllAsync(process.StandardError, onLine, linked.Token);
        try
        {
            var exitCode = await process.Exited.WaitAsync(linked.Token).ConfigureAwait(false);
            return new ProcessResult(exitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            process.Kill();
            throw new TimeoutException($"'{spec.FileName} {string.Join(' ', spec.Arguments)}' did not finish within {timeout}.");
        }
    }

    private static async Task<string> ReadAllAsync(System.Threading.Channels.ChannelReader<string> reader, Action<string>? onLine, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        await foreach (var line in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            builder.AppendLine(line);
            onLine?.Invoke(line);
        }
        return builder.ToString();
    }
}
