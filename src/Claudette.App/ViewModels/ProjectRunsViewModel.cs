using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.Core.Processes;
using Claudette.Core.ProjectTools;
using Claudette.Platform.Processes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>What the runs of a tab's project jobs need from its project tools, and through them from the tab.</summary>
internal interface IProjectRunsHost
{
    string Id { get; }

    /// <summary>The tab's name, for the "project action finished" notification.</summary>
    string DisplayName { get; }

    /// <summary>The Project page of the side panel shows, on the selected tab.</summary>
    bool IsProjectPageShowing { get; }

    void AddNote(string text, NoteKind kind = NoteKind.Info);

    /// <summary>Opens the side panel on the Project page.</summary>
    void OpenProjectPage();

    /// <summary>Makes the tab the selected one.</summary>
    void SelectTab();

    /// <summary>A job started or stopped running: the project's row names the running one.</summary>
    void RunningRunChanged();

    /// <summary>A job began: the project's row, menu and actions change.</summary>
    void JobBegan();

    /// <summary>
    /// A job ended, and its run is still listed: the project's row, menu and actions change, <paramref name="next"/>
    /// runs (Build and launch: the editor only starts after a build that worked), and the project is looked for again.
    /// </summary>
    void JobEnded(ProjectAction? next);
}

/// <summary>
/// The runs of a tab's project jobs (DESIGN.md §18, "Project tools"): each job this tab has run, with its own log, the
/// one running and the one the Project page shows, the running entry's ticking time, and the running job's process
/// tree, for the process monitor and Stop.
/// </summary>
public sealed partial class ProjectRunsViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly IProjectRunsHost _host;
    /// <summary>The running job's process tree, for the process monitor and Stop.</summary>
    private ProcessTree? _jobTree;
    /// <summary>A running entry's time ticks every second.</summary>
    private UiTicker RunTicker => field ??= new(_services.Time, _services.Dispatcher, TimeSpan.FromSeconds(1), () => RunningRun?.Tick());
    private ProjectRunViewModel? _notifiedRun;

    internal ProjectRunsViewModel(AppServices services, IProjectRunsHost host)
    {
        _services = services;
        _host = host;
    }

    /// <summary>
    /// Each job this tab has run, newest last (DESIGN.md §18): listed under the tab's row in the sidebar, each with its
    /// own log, until the user closes it. They aren't saved.
    /// </summary>
    public ObservableCollection<ProjectRunViewModel> Items { get; } = [];

    public bool HasRuns => Items.Count > 0;

    /// <summary>The run whose job is running. One job runs at a time per tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJobRunning))]
    public partial ProjectRunViewModel? RunningRun { get; private set; }

    partial void OnRunningRunChanged(ProjectRunViewModel? value)
    {
        RunTicker.Run(RunningRun is not null);
        _host.RunningRunChanged();
    }

    public bool IsJobRunning => RunningRun is not null;

    /// <summary>The run the Project page shows: the newest, or the one last clicked in the sidebar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRun))]
    public partial ProjectRunViewModel? SelectedRun { get; private set; }

    partial void OnSelectedRunChanged(ProjectRunViewModel? value) => UpdateShownRun();

    public bool HasSelectedRun => SelectedRun is not null;

    /// <summary>While a job runs, other jobs wait: one at a time per tab.</summary>
    internal ProjectAction ForJob(ProjectAction action) =>
        RunningRun is { } running && action.IsEnabled && action.Kind is ProjectActionKind.Run or ProjectActionKind.Destructive
            ? action with { DisabledReason = $"{running.Name} is still running. Stop it first, in the sidebar or on the Project page." }
            : action;

    /// <summary>A click on a run's entry in the sidebar: selects the tab and shows the run's log on the Project page.</summary>
    internal void OpenRun(ProjectRunViewModel run)
    {
        if (!Items.Contains(run))
        {
            return;
        }
        SelectedRun = run;
        _host.SelectTab();
        _host.OpenProjectPage();
    }

    /// <summary>A clicked "project action finished" notification: the Project page, on the run it was about.</summary>
    internal void OpenNotifiedRun()
    {
        if (_notifiedRun is { } run && Items.Contains(run))
        {
            SelectedRun = run;
        }
        _host.OpenProjectPage();
    }

    /// <summary>
    /// The × on a finished run's entry: the entry and its log go. A running one can't be closed, only stopped. When it
    /// was the one the Project page showed, the page shows the newest one left.
    /// </summary>
    internal void CloseRun(ProjectRunViewModel run)
    {
        if (run.IsRunning || !Items.Remove(run))
        {
            return;
        }
        if (ReferenceEquals(_notifiedRun, run))
        {
            // Its notification is about a log that's gone.
            _notifiedRun = null;
            _services.Notifications.ClearTab(_host.Id, NotificationKind.ProjectAction);
        }
        if (ReferenceEquals(SelectedRun, run))
        {
            SelectedRun = Items.LastOrDefault();
        }
        OnPropertyChanged(nameof(HasRuns));
    }

    /// <summary>Highlights the sidebar entry whose log is on the Project page, while the page shows on the selected tab.</summary>
    internal void UpdateShownRun()
    {
        var shown = _host.IsProjectPageShowing ? SelectedRun : null;
        foreach (var run in Items)
        {
            run.IsShowing = ReferenceEquals(run, shown);
        }
    }

    /// <summary>A new run, which the Project page shows.</summary>
    private ProjectRunViewModel AddRun(string name, ProjectAction action)
    {
        var run = new ProjectRunViewModel(this, name, action, _services.Time);
        Items.Add(run);
        OnPropertyChanged(nameof(HasRuns));
        SelectedRun = run;
        return run;
    }

    /// <summary>Starts a <see cref="ProjectActionKind.Run"/> action's process as a job, or makes a run that says why it couldn't.</summary>
    internal void StartJob(ProjectAction action, ProcessStartSpec spec)
    {
        ProjectJob job;
        try
        {
            if (action.ResultFile is { } result)
            {
                // A fresh result each time, so the summary never reads an old one.
                Directory.CreateDirectory(Path.GetDirectoryName(result)!);
                File.Delete(result);
            }
            job = _services.ProjectTools.StartJob(action.Label, spec);
        }
        catch (Exception ex) when (ProjectToolsViewModel.IsStartFailure(ex))
        {
            // A run all the same, so its entry and log say why.
            var failed = AddRun(action.Label, action);
            failed.Append([$"$ {action.CommandText}", $"Couldn't start it: {ex.Message}"]);
            failed.EndCouldntStart($"{action.Label} couldn't start: {ex.Message}");
            _host.AddNote($"Couldn't start {action.Label}: {ex.Message}", NoteKind.Error);
            return;
        }
        BeginJob(job, action, $"$ {action.CommandText}");
    }

    /// <summary>Runs <paramref name="job"/> as the tab's running job, with a new run whose log starts with <paramref name="firstLine"/>.</summary>
    internal void BeginJob(ProjectJob job, ProjectAction action, string firstLine)
    {
        var run = AddRun(job.Name, action);
        run.Job = job;
        run.Append([firstLine]);
        RunningRun = run;
        if (job.ProcessId is { } pid && _services.ProcessTrees is { } trees)
        {
            try
            {
                var tree = trees.Find(pid) ?? trees.Track(pid);
                Volatile.Write(ref _jobTree, tree);
                job.UseTreeKiller(tree.KillAll);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
            {
                // Without a tree, Stop still ends the process and its children.
            }
        }
        // Each run keeps its own lines, whichever run the Project page shows.
        job.Output += lines => run.Receive(lines, _services.Dispatcher);
        _ = WatchJobAsync(run, job);
        job.Begin();
        _host.JobBegan();
    }

    private async Task WatchJobAsync(ProjectRunViewModel run, ProjectJob job)
    {
        var result = await job.Completion.ConfigureAwait(false);
        _services.Dispatcher.Post(() => OnJobEnded(run, job, result));
    }

    private void OnJobEnded(ProjectRunViewModel run, ProjectJob job, ProjectJobResult result)
    {
        if (ReferenceEquals(RunningRun, run))
        {
            if (Interlocked.Exchange(ref _jobTree, null) is { } tree)
            {
                tree.Dispose();
            }
            RunningRun = null;
        }
        var action = run.Action;
        var summary = result.State == ProjectJobState.Succeeded || result.ExitCode is not null
            ? action?.Summarize?.Invoke(result.ExitCode ?? 0)
            : null;
        var exit = result.ExitCode is { } code ? $" (exit code {code.ToString(CultureInfo.InvariantCulture)})" : "";
        var status = result.State switch
        {
            ProjectJobState.Succeeded => $"{job.Name} succeeded.",
            ProjectJobState.Stopped => $"{job.Name} was stopped.",
            _ => $"{job.Name} failed{exit}.{(result.Message is { } why ? $" {why}" : "")}",
        } + (summary is null ? "" : $" {summary}");
        // The entry stays, whatever the result, until the user closes it.
        run.End(result.State, result.ExitCode, status);
        if (!Items.Contains(run))
        {
            // The tab closed and left it running.
            return;
        }
        if (result.State != ProjectJobState.Stopped
            && _services.Notifications.Notify(NotificationKind.ProjectAction, _host.DisplayName, status, _host.Id))
        {
            // Only when Claudette isn't in front (DESIGN.md §10); a click opens this run on the Project page.
            _notifiedRun = run;
        }
        _host.JobEnded(result.State == ProjectJobState.Succeeded ? action?.ThenOnSuccess : null);
    }

    /// <summary><b>Copy</b> on the Project page: the log it shows.</summary>
    [RelayCommand]
    private Task CopyOutputAsync() =>
        SelectedRun is { } run ? _services.Platform.SetClipboardTextAsync(string.Join(Environment.NewLine, run.Output)) : Task.CompletedTask;

    /// <summary>The job's processes, for the process monitor: the job's own process isn't the tab's <c>claude</c>.</summary>
    internal IReadOnlyList<ProcessSnapshot> JobProcesses(bool includeCommandLines)
    {
        if (Volatile.Read(ref _jobTree) is not { IsDisposed: false } tree)
        {
            return [];
        }
        try
        {
            return tree.Sample(includeCommandLines).Select(s => s.IsRoot ? s with { IsRoot = false } : s).ToArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception or ObjectDisposedException)
        {
            return [];
        }
    }

    /// <summary>Whether <paramref name="pid"/> belongs to the running job, so the process monitor's Stop goes to its tree.</summary>
    internal ProcessTree? JobTreeHolding(int pid) =>
        Volatile.Read(ref _jobTree) is { IsDisposed: false } tree && JobProcesses(false).Any(p => p.Pid == pid) ? tree : null;

    /// <summary>
    /// Closing the tab: a running job is stopped with the tab's other processes, unless they're kept, and the runs go
    /// with the tab.
    /// </summary>
    internal void CloseRuns(bool killProcesses)
    {
        if (killProcesses)
        {
            RunningRun?.Job?.Stop();
        }
        RunTicker.Stop();
        _notifiedRun = null;
        SelectedRun = null;
        Items.Clear();
        OnPropertyChanged(nameof(HasRuns));
    }
}
