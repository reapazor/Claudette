using System.Threading.Channels;
using Claudette.Core.Processes;

namespace Claudette.App.Tests.Support;

/// <summary>Records what would be started, and hands back processes the test controls.</summary>
internal sealed class FakeLauncher : IProcessLauncher
{
    public List<ProcessStartSpec> Started { get; } = [];

    public List<FakeProcess> Processes { get; } = [];

    /// <summary>Called as each process starts, for example to play a new build saying it's up.</summary>
    public Action<ProcessStartSpec, FakeProcess>? OnStart { get; set; }

    public IRunningProcess Start(ProcessStartSpec spec)
    {
        var process = new FakeProcess();
        Started.Add(spec);
        Processes.Add(process);
        OnStart?.Invoke(spec, process);
        return process;
    }
}

internal sealed class FakeProcess : IRunningProcess
{
    private readonly Channel<string> _stdout = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _stderr = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Id => 4321;

    public bool Killed { get; private set; }

    public bool Disposed { get; private set; }

    public ChannelReader<string> StandardOutput => _stdout.Reader;

    public ChannelReader<string> StandardError => _stderr.Reader;

    public Task<int> Exited => _exited.Task;

    public void WriteError(string line) => _stderr.Writer.TryWrite(line);

    public void Exit(int code)
    {
        _stdout.Writer.TryComplete();
        _stderr.Writer.TryComplete();
        _exited.TrySetResult(code);
    }

    public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public void CloseStandardInput()
    {
    }

    public void Kill()
    {
        Killed = true;
        Exit(-1);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
