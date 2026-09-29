using System.Threading.Channels;
using Claudette.Core.Processes;

namespace Claudette.Core.Tests.Support;

/// <summary>Records what would have been started, and hands back processes the test controls.</summary>
internal sealed class FakeProcessLauncher : IProcessLauncher
{
    private readonly List<ProcessStartSpec> _started = [];
    private readonly List<FakeRunningProcess> _processes = [];

    /// <summary>When set, <see cref="Start"/> throws this instead of starting anything.</summary>
    public Exception? StartFailure { get; set; }

    /// <summary>
    /// When set, answers each started process: the result's output is written and the process exits with its code.
    /// Returning null leaves the process running for the test to drive.
    /// </summary>
    public Func<ProcessStartSpec, ProcessResult?>? Respond { get; set; }

    public IReadOnlyList<ProcessStartSpec> Started => _started;

    public IReadOnlyList<FakeRunningProcess> Processes => _processes;

    public IRunningProcess Start(ProcessStartSpec spec)
    {
        if (StartFailure is not null)
        {
            throw StartFailure;
        }
        _started.Add(spec);
        var process = new FakeRunningProcess(1000 + _processes.Count);
        _processes.Add(process);
        if (Respond?.Invoke(spec) is { } result)
        {
            process.WriteOutput(result.StandardOutput);
            process.WriteError(result.StandardError);
            process.Exit(result.ExitCode);
        }
        return process;
    }
}

internal sealed class FakeRunningProcess(int id) : IRunningProcess
{
    private readonly Channel<string> _stdout = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _stderr = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Id { get; } = id;

    public ChannelReader<string> StandardOutput => _stdout.Reader;

    public ChannelReader<string> StandardError => _stderr.Reader;

    public Task<int> Exited => _exited.Task;

    public bool StandardInputClosed { get; private set; }

    public bool Killed { get; private set; }

    /// <summary>Completes when the process is disposed.</summary>
    public Task Disposed => _disposed.Task;

    /// <summary>Lines written to standard input.</summary>
    public List<string> Input { get; } = [];

    public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        lock (Input)
        {
            Input.Add(line);
        }
        return ValueTask.CompletedTask;
    }

    public void CloseStandardInput() => StandardInputClosed = true;

    /// <summary>Writes each line of <paramref name="text"/> to standard output.</summary>
    public void WriteOutput(string text) => WriteLines(_stdout, text);

    public void WriteError(string text) => WriteLines(_stderr, text);

    private static void WriteLines(Channel<string> channel, string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.Length > 0)
            {
                channel.Writer.TryWrite(line.TrimEnd('\r'));
            }
        }
    }

    public void Kill()
    {
        Killed = true;
        Exit(-1);
    }

    public void Exit(int exitCode)
    {
        _stdout.Writer.TryComplete();
        _stderr.Writer.TryComplete();
        _exited.TrySetResult(exitCode);
    }

    public ValueTask DisposeAsync()
    {
        _disposed.TrySetResult();
        return ValueTask.CompletedTask;
    }
}
