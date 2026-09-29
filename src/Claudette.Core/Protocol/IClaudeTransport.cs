using System.Threading.Channels;

namespace Claudette.Core.Protocol;

/// <summary>How a <c>claude</c> process ended.</summary>
public sealed record TransportExit(int? ExitCode, string StandardErrorTail);

/// <summary>
/// The line-based connection to one Claude Code process. All protocol traffic goes through this interface, so tests
/// can replace the process with a fake (DESIGN.md §15).
/// </summary>
public interface IClaudeTransport : IAsyncDisposable
{
    /// <summary>Lines Claude Code wrote to standard output. Completes when the process closes it.</summary>
    ChannelReader<string> Output { get; }

    /// <summary>Completes when the process has exited.</summary>
    Task<TransportExit> Completion { get; }

    ValueTask SendAsync(string line, CancellationToken cancellationToken = default);

    /// <summary>Closes standard input, which tells Claude Code to finish and exit.</summary>
    void CloseInput();

    /// <summary>Ends the process immediately.</summary>
    void Terminate();

    /// <summary>The operating system process id, for the process monitor (DESIGN.md §4). Null for fakes.</summary>
    int? ProcessId => null;
}
