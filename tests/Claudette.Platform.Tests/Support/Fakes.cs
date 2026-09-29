using System.Threading.Channels;
using Claudette.Core.Processes;
using Claudette.Platform.Processes;

namespace Claudette.Platform.Tests.Support;

/// <summary>A process tree that returns whatever the test says, and counts what was asked of it.</summary>
internal sealed class FakeProcessTree(int rootPid = 100) : ProcessTree(rootPid)
{
    private int _samples;
    private int _concurrent;
    private int _maxConcurrent;

    public Func<bool, IReadOnlyList<ProcessSnapshot>> OnSample { get; set; } = _ => [];

    public int Samples => Volatile.Read(ref _samples);

    public int MaxConcurrentSamples => Volatile.Read(ref _maxConcurrent);

    public List<bool> CommandLineRequests { get; } = [];

    public int KillAllCalls { get; private set; }

    public override IReadOnlyList<ProcessSnapshot> Sample(bool includeCommandLines)
    {
        var now = Interlocked.Increment(ref _concurrent);
        InterlockedMax(ref _maxConcurrent, now);
        try
        {
            Interlocked.Increment(ref _samples);
            lock (CommandLineRequests)
            {
                CommandLineRequests.Add(includeCommandLines);
            }
            return OnSample(includeCommandLines);
        }
        finally
        {
            Interlocked.Decrement(ref _concurrent);
        }
    }

    public override IReadOnlyList<int> DescendantIds() => [];

    public override void KillAll() => KillAllCalls++;

    public override Task StopAsync(int pid, TimeSpan grace, CancellationToken cancellationToken = default) => Task.CompletedTask;

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}

internal sealed class FakeLauncher : IProcessLauncher
{
    private int _nextId = 4000;

    public List<FakeRunningProcess> Started { get; } = [];

    public List<ProcessStartSpec> Specs { get; } = [];

    public IRunningProcess Start(ProcessStartSpec spec)
    {
        var process = new FakeRunningProcess(Interlocked.Increment(ref _nextId));
        Started.Add(process);
        Specs.Add(spec);
        return process;
    }
}

internal sealed class FakeRunningProcess(int id) : IRunningProcess
{
    private readonly Channel<string> _stdout = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _stderr = Channel.CreateUnbounded<string>();

    public int Id { get; } = id;

    public bool Disposed { get; private set; }

    public int KillCalls { get; private set; }

    public ChannelReader<string> StandardOutput => _stdout.Reader;

    public ChannelReader<string> StandardError => _stderr.Reader;

    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<int> Exited => _exited.Task;

    public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public void CloseStandardInput()
    {
    }

    public void Kill() => KillCalls++;

    public void WriteOutput(string line) => _stdout.Writer.TryWrite(line);

    public void WriteError(string line) => _stderr.Writer.TryWrite(line);

    /// <summary>Closes both streams and exits with <paramref name="exitCode"/>.</summary>
    public void Exit(int exitCode)
    {
        _stdout.Writer.TryComplete();
        _stderr.Writer.TryComplete();
        _exited.TrySetResult(exitCode);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>A tracker that hands out <see cref="FakeProcessTree"/>s, or throws when told to.</summary>
internal sealed class FakeTracker : IProcessTreeTracker
{
    public Dictionary<int, FakeProcessTree> Trees { get; } = [];

    public Exception? Failure { get; set; }

    public ProcessTree Track(int rootPid)
    {
        if (Failure is not null)
        {
            throw Failure;
        }
        if (!Trees.TryGetValue(rootPid, out var tree))
        {
            Trees[rootPid] = tree = new FakeProcessTree(rootPid);
        }
        return tree;
    }

    public ProcessTree? Find(int rootPid) => Trees.GetValueOrDefault(rootPid);
}
