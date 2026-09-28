using System.Text;

namespace Claudette.Core.Processes;

/// <summary>The outcome of a short command run to completion.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs short commands (such as <c>claude --version</c>) to completion and collects their output.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        IProcessLauncher launcher,
        ProcessStartSpec spec,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        await using var process = launcher.Start(spec);
        process.CloseStandardInput();

        using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        var stdout = ReadAllAsync(process.StandardOutput, linked.Token);
        var stderr = ReadAllAsync(process.StandardError, linked.Token);
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

    private static async Task<string> ReadAllAsync(System.Threading.Channels.ChannelReader<string> reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        await foreach (var line in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            builder.AppendLine(line);
        }
        return builder.ToString();
    }
}
