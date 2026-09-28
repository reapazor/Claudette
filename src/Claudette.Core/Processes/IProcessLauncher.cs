using System.Threading.Channels;

namespace Claudette.Core.Processes;

/// <summary>Starts child processes. Everything that launches a process goes through this, so tests can replace it.</summary>
public interface IProcessLauncher
{
    IRunningProcess Start(ProcessStartSpec spec);
}

/// <summary>A started child process with line-based standard streams.</summary>
public interface IRunningProcess : IAsyncDisposable
{
    int Id { get; }

    /// <summary>Lines from standard output, in order. Completes when the stream closes.</summary>
    ChannelReader<string> StandardOutput { get; }

    /// <summary>Lines from standard error, in order. Completes when the stream closes.</summary>
    ChannelReader<string> StandardError { get; }

    /// <summary>Completes with the exit code when the process exits.</summary>
    Task<int> Exited { get; }

    ValueTask WriteLineAsync(string line, CancellationToken cancellationToken = default);

    void CloseStandardInput();

    /// <summary>Ends the process and its children immediately.</summary>
    void Kill();
}
