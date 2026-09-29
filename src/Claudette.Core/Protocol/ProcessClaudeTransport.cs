using System.Threading.Channels;
using Claudette.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Claudette.Core.Protocol;

/// <summary>An <see cref="IClaudeTransport"/> over a real <c>claude</c> process's standard streams.</summary>
public sealed class ProcessClaudeTransport : IClaudeTransport
{
    private const int StandardErrorTailLines = 50;

    private readonly IRunningProcess _process;
    private readonly ILogger _logger;
    private readonly Queue<string> _stderrTail = new();
    private readonly Task<TransportExit> _completion;

    public ProcessClaudeTransport(IRunningProcess process, ILogger logger)
    {
        _process = process;
        _logger = logger;
        var stderrPump = PumpStandardErrorAsync();
        _completion = WaitForExitAsync(stderrPump);
    }

    public ChannelReader<string> Output => _process.StandardOutput;

    public Task<TransportExit> Completion => _completion;

    public int? ProcessId => _process.Id;

    public ValueTask SendAsync(string line, CancellationToken cancellationToken = default) =>
        _process.WriteLineAsync(line, cancellationToken);

    public void CloseInput() => _process.CloseStandardInput();

    public void Terminate() => _process.Kill();

    public ValueTask DisposeAsync() => _process.DisposeAsync();

    private async Task PumpStandardErrorAsync()
    {
        await foreach (var line in _process.StandardError.ReadAllAsync().ConfigureAwait(false))
        {
            _logger.LogDebug("claude stderr [{Pid}]: {Line}", _process.Id, line);
            lock (_stderrTail)
            {
                _stderrTail.Enqueue(line);
                if (_stderrTail.Count > StandardErrorTailLines)
                {
                    _stderrTail.Dequeue();
                }
            }
        }
    }

    private async Task<TransportExit> WaitForExitAsync(Task stderrPump)
    {
        int? exitCode = null;
        try
        {
            exitCode = await _process.Exited.ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Could not read the claude process exit code.");
        }
        await stderrPump.ConfigureAwait(false);
        lock (_stderrTail)
        {
            return new TransportExit(exitCode, string.Join('\n', _stderrTail));
        }
    }
}
