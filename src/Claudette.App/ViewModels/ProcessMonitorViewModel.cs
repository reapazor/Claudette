using System.Collections.ObjectModel;
using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;
using Claudette.Platform.Processes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>One process in the tab's Processes panel (DESIGN.md §4, "Process monitor").</summary>
public sealed partial class ProcessRow : ObservableObject
{
    public ProcessRow(ProcessSnapshot snapshot) => _snapshot = snapshot;

    private ProcessSnapshot _snapshot;

    /// <summary>The latest sample of the process. A row is kept from sample to sample and updated in place.</summary>
    public ProcessSnapshot Snapshot
    {
        get => _snapshot;
        set
        {
            if (Equals(value, _snapshot))
            {
                return;
            }
            _snapshot = value;
            // Everything shown comes from it.
            OnPropertyChanged(string.Empty);
        }
    }

    public int Pid => Snapshot.Pid;

    public string Name => Snapshot.Name;

    /// <summary>Indentation for the tree, from the process's depth under <c>claude</c>.</summary>
    [ObservableProperty]
    public partial double Indent { get; set; }

    public bool IsRoot => Snapshot.IsRoot;

    public bool IsDetached => Snapshot.IsDetached;

    public string CpuText => Snapshot.CpuPercent is { } cpu ? $"{cpu:0.#}%" : "…";

    public string MemoryText => ProcessSummary.FormatMemory(Snapshot.MemoryBytes);

    [ObservableProperty]
    public partial string RunningText { get; set; } = "";

    public string? CommandLine => Snapshot.CommandLine;

    public bool HasCommandLine => !string.IsNullOrEmpty(CommandLine);

    /// <summary>The Bash tool call or background task that started it, when Claudette could tell.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTool), nameof(ToolText))]
    public partial ToolUseItem? Tool { get; set; }

    public bool HasTool => Tool is not null;

    public string? ToolText => Tool is null ? null : $"{Tool.Name}: {Tool.Summary}";

    public string Tooltip => $"{Name} (PID {Pid}){(IsDetached ? ", detached" : "")}\n{CommandLine ?? Snapshot.ExecutablePath ?? ""}";
}

/// <summary>What the process monitor needs from its tab.</summary>
internal interface IProcessMonitorHost : ITabAreaHost
{
    /// <summary>The side panel is open, so the composer bar leaves the summary to it.</summary>
    bool IsSidePanelOpen { get; }

    /// <summary>The conversation's tool calls, for the call that started a process.</summary>
    IEnumerable<ToolUseItem> ToolItems { get; }

    /// <summary>The background task Claude Code started for a tool call, if any.</summary>
    string? TaskIdFor(string toolUseId);

    /// <summary>The running project job's processes, which show under the tab's (DESIGN.md §18, "Project tools").</summary>
    IReadOnlyList<ProcessSnapshot> ProjectJobProcesses(bool includeCommandLines);

    /// <summary>The project job's tree, when <paramref name="pid"/> belongs to it, so Stop goes to that tree.</summary>
    ProcessTree? ProjectJobTreeHolding(int pid);

    /// <summary>Asks the view to scroll to a conversation item.</summary>
    void ScrollTo(ConversationItem item);

    /// <summary>A new sample: the header's total across the tabs changes.</summary>
    void ProcessesSampled();
}

/// <summary>The tab's processes (DESIGN.md §4, "Process monitor").</summary>
public sealed partial class ProcessMonitorViewModel : ViewModelBase
{
    /// <summary>A child process using more than this much CPU gives the tab an activity icon.</summary>
    private const double ActiveCpuPercent = 5;

    private readonly AppServices _services;
    private readonly IProcessMonitorHost _host;
    private ProcessTree? _tree;
    private ProcessSampler? _sampler;
    private readonly Dictionary<int, string> _processTools = [];

    internal ProcessMonitorViewModel(AppServices services, IProcessMonitorHost host)
    {
        _services = services;
        _host = host;
    }

    /// <summary>The monitor is on for this tab: its own setting in Tab settings…, or Settings → Processes.</summary>
    public bool IsOn => (_host.State.Overrides.ShowProcessMonitor ?? _services.Settings.Processes.ShowMonitor) && _tree is not null;

    /// <summary>For example <c>3 procs · 42% CPU · 1.1 GB</c>, in the composer bar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSummary))]
    public partial string? SummaryText { get; set; }

    /// <summary>In the composer bar, unless the side panel is open and already shows it.</summary>
    public bool ShowSummary => SummaryText is not null && !_host.IsSidePanelOpen;

    /// <summary>The side panel opened or closed, which shows the summary instead of the composer bar.</summary>
    internal void OnSidePanelOpenChanged() => OnPropertyChanged(nameof(ShowSummary));

    /// <summary>
    /// The latest sample's totals, <c>claude</c> included, for the header's total across the tabs; null while the monitor
    /// is off or before the first sample.
    /// </summary>
    public ProcessSummary? LatestSummary { get; private set; }

    private void SetLatestSummary(ProcessSummary? summary)
    {
        LatestSummary = summary;
        _host.ProcessesSampled();
    }

    /// <summary>A child process is using noticeable CPU: the tab shows an activity icon.</summary>
    [ObservableProperty]
    public partial bool HasBusyProcesses { get; set; }

    public ObservableCollection<ProcessRow> Processes { get; } = [];

    /// <summary>The Processes page is showing, so sample faster and include command lines.</summary>
    [ObservableProperty]
    public partial bool IsPanelVisible { get; set; }

    partial void OnIsPanelVisibleChanged(bool value) => UpdateSampler();

    /// <summary>The session started: follow its <c>claude</c> and what it starts.</summary>
    internal void AttachTree(ClaudeSession session)
    {
        if (_services.ProcessTrees is not { } trees || session.ProcessId is not { } pid)
        {
            return;
        }
        try
        {
            _tree = trees.Find(pid) ?? trees.Track(pid);
        }
        catch (Exception)
        {
            _tree = null;
        }
        UpdateSampler();
    }

    /// <summary>Settings or visibility changed: every 2 s while the panel shows, every 10 s for the summary, off when the monitor is off.</summary>
    internal void UpdateSampler()
    {
        OnPropertyChanged(nameof(IsOn));
        if (!IsOn)
        {
            _sampler?.Dispose();
            _sampler = null;
            SummaryText = null;
            HasBusyProcesses = false;
            Processes.Clear();
            if (LatestSummary is not null)
            {
                SetLatestSummary(null);
            }
            return;
        }
        if (_sampler is null)
        {
            _sampler = new ProcessSampler(_tree!, _services.Time);
            // A project action's job shows too (DESIGN.md §18, "Project tools"), under the tab's claude.
            _sampler.Sampled += snapshots =>
            {
                var job = _host.ProjectJobProcesses(_services.Settings.Processes.ShowCommandLines);
                IReadOnlyList<ProcessSnapshot> all = job.Count == 0 ? snapshots : [.. snapshots, .. job];
                _services.Dispatcher.Post(() => OnProcessesSampled(all));
            };
        }
        var settings = _services.Settings.Processes;
        _sampler.Interval = IsPanelVisible ? TimeSpan.FromSeconds(Math.Max(1, settings.RefreshSeconds)) : ProcessSampler.SummaryInterval;
        _sampler.IncludeCommandLines = settings.ShowCommandLines;
        if (!_sampler.IsRunning)
        {
            _sampler.Start();
        }
    }

    private void OnProcessesSampled(IReadOnlyList<ProcessSnapshot> snapshots)
    {
        if (_sampler is null)
        {
            return;
        }
        var summary = ProcessSummary.From(snapshots);
        SummaryText = summary.Count > 0 ? summary.ToString() : null;
        HasBusyProcesses = snapshots.Any(s => !s.IsRoot && s.CpuPercent >= ActiveCpuPercent);
        SetLatestSummary(summary);

        // Link a new process to the Bash call that was running when it appeared. A process that's gone is forgotten,
        // so its PID, used again by another program, isn't linked to the old call.
        var pids = snapshots.Select(s => s.Pid).ToHashSet();
        foreach (var gone in _processTools.Keys.Where(pid => !pids.Contains(pid)).ToArray())
        {
            _processTools.Remove(gone);
        }
        var running = _host.ToolItems.LastOrDefault(t => t is { Name: "Bash", IsComplete: false });
        foreach (var snapshot in snapshots.Where(s => !s.IsRoot && !_processTools.ContainsKey(s.Pid)))
        {
            if (running is not null)
            {
                _processTools[snapshot.Pid] = running.ToolUseId;
            }
        }

        var byPid = new Dictionary<int, ProcessSnapshot>();
        foreach (var snapshot in snapshots)
        {
            byPid.TryAdd(snapshot.Pid, snapshot);
        }
        var depth = new Dictionary<int, int>();
        int Depth(ProcessSnapshot s)
        {
            if (s.IsRoot || depth.Count > 500)
            {
                return 0;
            }
            if (depth.TryGetValue(s.Pid, out var known))
            {
                return known;
            }
            depth[s.Pid] = 1;
            return depth[s.Pid] = byPid.TryGetValue(s.ParentPid, out var parent) && parent.Pid != s.Pid ? Depth(parent) + 1 : 1;
        }
        var now = _services.Time.GetUtcNow();
        var toolsById = _processTools.Count == 0 ? [] : _host.ToolItems.ToDictionary(t => t.ToolUseId);
        UpdateRows(Ordered(snapshots).Select(snapshot => (
            snapshot,
            Indent: Depth(snapshot) * 14.0,
            Running: snapshot.StartTime is { } started ? Elapsed(now - started) : "",
            Tool: _processTools.TryGetValue(snapshot.Pid, out var toolId) && toolsById.TryGetValue(toolId, out var tool) ? tool : null)).ToList());
    }

    /// <summary>
    /// Brings the rows to <paramref name="wanted"/>, keeping each process's row from sample to sample: only what changed
    /// is updated, moved, added or removed, so the list doesn't flash, and a selected row stays selected.
    /// </summary>
    private void UpdateRows(List<(ProcessSnapshot Snapshot, double Indent, string Running, ToolUseItem? Tool)> wanted)
    {
        for (var i = 0; i < wanted.Count; i++)
        {
            var (snapshot, indent, running, tool) = wanted[i];
            var at = -1;
            for (var j = i; j < Processes.Count; j++)
            {
                if (Processes[j].Pid == snapshot.Pid)
                {
                    at = j;
                    break;
                }
            }
            ProcessRow row;
            if (at < 0)
            {
                row = new ProcessRow(snapshot);
                Processes.Insert(i, row);
            }
            else
            {
                if (at != i)
                {
                    Processes.Move(at, i);
                }
                row = Processes[i];
                row.Snapshot = snapshot;
            }
            row.Indent = indent;
            row.RunningText = running;
            row.Tool = tool;
        }
        while (Processes.Count > wanted.Count)
        {
            Processes.RemoveAt(Processes.Count - 1);
        }
    }

    /// <summary>Depth-first from the root, so children sit under their parent.</summary>
    private static IEnumerable<ProcessSnapshot> Ordered(IReadOnlyList<ProcessSnapshot> snapshots)
    {
        var byParent = snapshots.ToLookup(s => s.ParentPid);
        var pids = snapshots.Select(s => s.Pid).ToHashSet();
        var visited = new HashSet<int>();
        IEnumerable<ProcessSnapshot> Walk(ProcessSnapshot s)
        {
            if (!visited.Add(s.Pid))
            {
                yield break;
            }
            yield return s;
            foreach (var child in byParent[s.Pid].Where(c => c.Pid != s.Pid).OrderBy(c => c.Pid).SelectMany(Walk))
            {
                yield return child;
            }
        }
        // Roots: the claude process, then anything whose parent isn't in the list (detached processes).
        foreach (var top in snapshots.Where(s => s.IsRoot).Concat(snapshots.Where(s => !s.IsRoot && !pids.Contains(s.ParentPid))))
        {
            foreach (var s in Walk(top))
            {
                yield return s;
            }
        }
    }

    private static string Elapsed(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => $"{(int)span.TotalSeconds}s",
        { TotalHours: < 1 } => $"{(int)span.TotalMinutes}m",
        { TotalDays: < 1 } => $"{(int)span.TotalHours}h {span.Minutes:00}m",
        _ => $"{(int)span.TotalDays}d {span.Hours}h",
    };

    [RelayCommand]
    private void ShowProcessTool(ProcessRow? row)
    {
        if (row?.Tool is { } tool)
        {
            _host.ScrollTo(tool);
        }
    }

    /// <summary>Stop, after confirming: through Claude Code for a background task, else gracefully and then forcefully.</summary>
    [RelayCommand]
    private void StopProcess(ProcessRow? row)
    {
        if (row is null || row.IsRoot)
        {
            return;
        }
        // A task Claude Code started for the call, so Stop can go through it (DESIGN.md §4, "Actions").
        var taskId = row.Tool is { } tool ? _host.TaskIdFor(tool.ToolUseId) : null;
        _host.Confirm(
            $"Stop {row.Name} (PID {row.Pid})?",
            taskId is not null
                ? "It's a background task Claude started. Claude Code stops it and tells Claude it ended."
                : "Claudette asks it to close, then ends it if it doesn't. Claude isn't told directly.",
            "Stop",
            async () =>
            {
                try
                {
                    if (taskId is not null && _host.Session is { } session)
                    {
                        await session.StopTaskAsync(taskId);
                    }
                    else if ((_host.ProjectJobTreeHolding(row.Pid) ?? _tree) is { } tree)
                    {
                        await tree.StopAsync(row.Pid, TimeSpan.FromSeconds(3));
                    }
                }
                catch (Exception ex)
                {
                    _host.AddNote($"Couldn't stop {row.Name}: {ex.Message}", NoteKind.Error);
                }
            });
    }

    [RelayCommand]
    private Task CopyCommandLineAsync(ProcessRow? row) =>
        row?.CommandLine is { } commandLine ? _services.Platform.SetClipboardTextAsync(commandLine) : Task.CompletedTask;

    [RelayCommand]
    private Task RevealExecutableAsync(ProcessRow? row) =>
        row?.Snapshot.ExecutablePath is { } path && Path.GetDirectoryName(path) is { } folder
            ? _services.Platform.RevealFolderAsync(folder)
            : Task.CompletedTask;

    /// <summary>Processes the tab started that are still running, for the close confirmation.</summary>
    public IReadOnlyList<ProcessSnapshot> RunningChildProcesses()
    {
        try
        {
            return [.. _tree?.Sample(includeCommandLines: false).Where(s => !s.IsRoot) ?? [], .. _host.ProjectJobProcesses(includeCommandLines: false)];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Stops the sampler, and optionally every process the tab started (DESIGN.md §4, "Cleanup").</summary>
    internal async Task EndTreeAsync(bool killProcesses)
    {
        _sampler?.Dispose();
        _sampler = null;
        // Its processes leave the header's total; on the UI thread, like the samples, since closing can finish elsewhere.
        _services.Dispatcher.Post(() => SetLatestSummary(null));
        if (_tree is { } tree)
        {
            if (killProcesses)
            {
                await Task.Run(tree.KillAll);
            }
            tree.Dispose();
            _tree = null;
        }
    }
}
