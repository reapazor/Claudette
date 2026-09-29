using System.Threading.Channels;
using Claudette.Core.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudette.Platform.Processes;

/// <summary>
/// Starts processes through another launcher and tracks the tree of those that ask for it with
/// <see cref="ProcessStartSpec.TrackProcessTree"/> (DESIGN.md §4, Process monitor).
/// <list type="bullet">
/// <item>Tracking starts right after the process does. <c>claude</c> starts MCP servers during startup, so the
/// window in which a child could escape is kept as small as possible.</item>
/// <item>A tracking failure is logged and never breaks the launch.</item>
/// <item>Disposing the returned process also releases the tree's OS handles. It doesn't end anything beyond what the
/// inner process's own dispose ends; call <see cref="ProcessTree.KillAll"/> first for that.</item>
/// </list>
/// </summary>
public sealed class TrackingProcessLauncher(IProcessLauncher inner, IProcessTreeTracker tracker, ILogger<TrackingProcessLauncher>? logger = null)
    : IProcessLauncher
{
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    public IRunningProcess Start(ProcessStartSpec spec)
    {
        var process = inner.Start(spec);
        if (!spec.TrackProcessTree)
        {
            return process;
        }
        try
        {
            return new TrackedProcess(process, tracker.Track(process.Id));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't track the processes started by {FileName} (PID {Pid}).", spec.FileName, process.Id);
            return process;
        }
    }

    private sealed class TrackedProcess(IRunningProcess inner, ProcessTree tree) : IRunningProcess
    {
        public int Id => inner.Id;

        public ChannelReader<string> StandardOutput => inner.StandardOutput;

        public ChannelReader<string> StandardError => inner.StandardError;

        public Task<int> Exited => inner.Exited;

        public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken = default) =>
            inner.WriteLineAsync(line, cancellationToken);

        public void CloseStandardInput() => inner.CloseStandardInput();

        public void Kill() => inner.Kill();

        public async ValueTask DisposeAsync()
        {
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                tree.Dispose();
            }
        }
    }
}
