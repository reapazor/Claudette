using System.Threading.Channels;
using Claudette.Core.Processes;

namespace Claudette.Core.ProjectTools;

public enum ProjectJobState
{
    Running,
    Succeeded,
    Failed,
    Stopped,
}

/// <summary>How a job ended.</summary>
/// <param name="ExitCode">The process's exit code; null for work Claudette did itself, or a process that never started.</param>
/// <param name="Message">Why it failed, when there's more to say than the exit code.</param>
public sealed record ProjectJobResult(ProjectJobState State, int? ExitCode, string? Message = null);

/// <summary>
/// A long project action (DESIGN.md §18, "Project tools"): a build, project file generation, a custom command, or
/// deleting folders. Its output arrives in batches of lines, and <see cref="Stop"/> ends it, process tree and all.
/// </summary>
public sealed class ProjectJob
{
    private readonly TaskCompletionSource<ProjectJobResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private IRunningProcess? _process;
    private Action? _killTree;
    private int _stopped;

    private ProjectJob(string name)
    {
        Name = name;
    }

    /// <summary>"Build editor", for the chip and the Project page.</summary>
    public string Name { get; }

    /// <summary>The job's process, while it has one.</summary>
    public int? ProcessId => _process?.Id;

    /// <summary>Lines of output, a batch at a time, on a background thread. Standard error comes in with standard output.</summary>
    public event Action<IReadOnlyList<string>>? Output;

    public Task<ProjectJobResult> Completion => _completion.Task;

    public bool IsStopRequested => Volatile.Read(ref _stopped) != 0;

    /// <summary>
    /// Starts <paramref name="spec"/>. Subscribe to <see cref="Output"/> before calling <see cref="Begin"/>, which reads
    /// the output; lines wait until then.
    /// </summary>
    /// <exception cref="Exception">The process couldn't start, as <see cref="IProcessLauncher.Start"/> throws.</exception>
    public static ProjectJob Start(string name, IProcessLauncher launcher, ProcessStartSpec spec)
    {
        var job = new ProjectJob(name);
        job._process = launcher.Start(spec);
        job._process.CloseStandardInput();
        return job;
    }

    /// <summary>Work Claudette does itself, such as deleting folders. It reports lines and says whether it succeeded.</summary>
    public static ProjectJob Run(string name, Func<Action<string>, CancellationToken, Task<bool>> work)
    {
        var job = new ProjectJob(name);
        job._work = work;
        return job;
    }

    private Func<Action<string>, CancellationToken, Task<bool>>? _work;

    /// <summary>What <see cref="Stop"/> uses to end the whole process tree, rather than only the process.</summary>
    public void UseTreeKiller(Action killTree) => _killTree = killTree;

    /// <summary>Starts reading the output (or doing the work) until the job ends.</summary>
    public void Begin() => _ = RunAsync();

    /// <summary>Ends the job: its whole process tree, or the work at its next step. Idempotent.</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }
        _stop.Cancel();
        try
        {
            _killTree?.Invoke();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // It may have exited in the meantime; killing the process below covers the rest.
        }
        _process?.Kill();
    }

    private async Task RunAsync()
    {
        try
        {
            _completion.TrySetResult(_process is { } process ? await WaitForProcessAsync(process).ConfigureAwait(false) : await DoWorkAsync().ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _completion.TrySetResult(new ProjectJobResult(IsStopRequested ? ProjectJobState.Stopped : ProjectJobState.Failed, null, ex.Message));
        }
    }

    private async Task<ProjectJobResult> WaitForProcessAsync(IRunningProcess process)
    {
        var stdout = PumpAsync(process.StandardOutput);
        var stderr = PumpAsync(process.StandardError);
        int exitCode;
        try
        {
            exitCode = await process.Exited.ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        finally
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }
        var state = IsStopRequested ? ProjectJobState.Stopped : exitCode == 0 ? ProjectJobState.Succeeded : ProjectJobState.Failed;
        return new ProjectJobResult(state, exitCode);
    }

    private async Task<ProjectJobResult> DoWorkAsync()
    {
        var ok = await Task.Run(() => _work!(line => Output?.Invoke([line]), _stop.Token)).ConfigureAwait(false);
        return new ProjectJobResult(IsStopRequested ? ProjectJobState.Stopped : ok ? ProjectJobState.Succeeded : ProjectJobState.Failed, null);
    }

    private async Task PumpAsync(ChannelReader<string> reader)
    {
        var batch = new List<string>();
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var line))
            {
                batch.Add(line);
            }
            Output?.Invoke(batch.ToArray());
            batch.Clear();
        }
    }
}
