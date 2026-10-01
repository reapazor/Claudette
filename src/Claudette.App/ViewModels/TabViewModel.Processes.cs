using System.Collections.ObjectModel;
using Claudette.App.Conversation;
using Claudette.Core.Sessions;
using Claudette.Platform.Processes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>One process in the tab's Processes panel (DESIGN.md §4, "Process monitor").</summary>
public sealed class ProcessRow
{
    public required ProcessSnapshot Snapshot { get; init; }

    public int Pid => Snapshot.Pid;

    public string Name => Snapshot.Name;

    /// <summary>Indentation for the tree, from the process's depth under <c>claude</c>.</summary>
    public double Indent { get; init; }

    public bool IsRoot => Snapshot.IsRoot;

    public bool IsDetached => Snapshot.IsDetached;

    public string CpuText => Snapshot.CpuPercent is { } cpu ? $"{cpu:0.#}%" : "…";

    public string MemoryText => ProcessSummary.FormatMemory(Snapshot.MemoryBytes);

    public required string RunningText { get; init; }

    public string? CommandLine => Snapshot.CommandLine;

    public bool HasCommandLine => !string.IsNullOrEmpty(CommandLine);

    /// <summary>The Bash tool call or background task that started it, when Claudette could tell.</summary>
    public ToolUseItem? Tool { get; init; }

    public bool HasTool => Tool is not null;

    public string? ToolText => Tool is null ? null : $"{Tool.Name}: {Tool.Summary}";

    public string Tooltip => $"{Name} (PID {Pid}){(IsDetached ? ", detached" : "")}\n{CommandLine ?? Snapshot.ExecutablePath ?? ""}";
}

/// <summary>The tab's processes (DESIGN.md §4, "Process monitor").</summary>
public sealed partial class TabViewModel
{
    /// <summary>A child process using more than this much CPU gives the tab an activity icon.</summary>
    private const double ActiveCpuPercent = 5;

    private ProcessTree? _tree;
    private ProcessSampler? _sampler;
    private readonly Dictionary<int, string> _processTools = [];

    /// <summary>The monitor is on for this tab: its own setting in Tab settings…, or Settings → Processes.</summary>
    public bool IsProcessMonitorOn => (State.Overrides.ShowProcessMonitor ?? _services.Settings.Processes.ShowMonitor) && _tree is not null;

    /// <summary>For example <c>3 procs · 42% CPU · 1.1 GB</c>, in the composer bar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProcessSummary))]
    public partial string? ProcessSummaryText { get; set; }

    /// <summary>In the composer bar, unless the side panel is open and already shows it.</summary>
    public bool ShowProcessSummary => ProcessSummaryText is not null && !IsSidePanelOpen;

    /// <summary>
    /// The latest sample's totals, <c>claude</c> included, for the header's total across the tabs; null while the monitor
    /// is off or before the first sample.
    /// </summary>
    public ProcessSummary? LatestProcessSummary { get; private set; }

    private void SetLatestProcessSummary(ProcessSummary? summary)
    {
        LatestProcessSummary = summary;
        _shell.OnTabProcessesSampled();
    }

    /// <summary>A child process is using noticeable CPU: the tab shows an activity icon.</summary>
    [ObservableProperty]
    public partial bool HasBusyProcesses { get; set; }

    public ObservableCollection<ProcessRow> Processes { get; } = [];

    /// <summary>The Processes page is showing, so sample faster and include command lines.</summary>
    [ObservableProperty]
    public partial bool IsProcessPanelVisible { get; set; }

    partial void OnIsProcessPanelVisibleChanged(bool value) => UpdateSampler();

    /// <summary>Asks the view to scroll to a conversation item, for example the card that started a process.</summary>
    public event Action<ConversationItem>? ScrollToRequested;

    /// <summary>Asks the view to scroll to <paramref name="item"/>, wherever it is in the conversation.</summary>
    public void ScrollTo(ConversationItem item) => ScrollToRequested?.Invoke(item);

    /// <summary>
    /// The conversation's top-level item that holds <paramref name="item"/>: itself, or the subagent group it's inside,
    /// however deep. Null when it isn't in the conversation. The view brings that into view first, since the
    /// conversation is virtualized and only items in view have controls.
    /// </summary>
    public ConversationItem? TopLevelItemOf(ConversationItem item)
    {
        foreach (var top in Items)
        {
            if (ReferenceEquals(top, item) || top is SubagentItem group && Contains(group, item))
            {
                return top;
            }
        }
        return null;

        static bool Contains(SubagentItem group, ConversationItem item) =>
            group.Items.Any(child => ReferenceEquals(child, item) || child is SubagentItem inner && Contains(inner, item));
    }

    private void AttachProcessTree(ClaudeSession session)
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
    private void UpdateSampler()
    {
        OnPropertyChanged(nameof(IsProcessMonitorOn));
        if (!IsProcessMonitorOn)
        {
            _sampler?.Dispose();
            _sampler = null;
            ProcessSummaryText = null;
            HasBusyProcesses = false;
            Processes.Clear();
            if (LatestProcessSummary is not null)
            {
                SetLatestProcessSummary(null);
            }
            return;
        }
        if (_sampler is null)
        {
            _sampler = new ProcessSampler(_tree!, _services.Time);
            // A project action's job shows too (DESIGN.md §18, "Project tools"), under the tab's claude.
            _sampler.Sampled += snapshots =>
            {
                var job = ProjectJobProcesses(_services.Settings.Processes.ShowCommandLines);
                IReadOnlyList<ProcessSnapshot> all = job.Count == 0 ? snapshots : [.. snapshots, .. job];
                _services.Dispatcher.Post(() => OnProcessesSampled(all));
            };
        }
        var settings = _services.Settings.Processes;
        _sampler.Interval = IsProcessPanelVisible ? TimeSpan.FromSeconds(Math.Max(1, settings.RefreshSeconds)) : ProcessSampler.SummaryInterval;
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
        ProcessSummaryText = summary.Count > 0 ? summary.ToString() : null;
        HasBusyProcesses = snapshots.Any(s => !s.IsRoot && s.CpuPercent >= ActiveCpuPercent);
        SetLatestProcessSummary(summary);

        // Link a new process to the Bash call that was running when it appeared.
        var running = Items.OfType<ToolUseItem>().LastOrDefault(t => t is { Name: "Bash", IsComplete: false });
        foreach (var snapshot in snapshots.Where(s => !s.IsRoot && !_processTools.ContainsKey(s.Pid)))
        {
            if (running is not null)
            {
                _processTools[snapshot.Pid] = running.ToolUseId;
            }
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
            var parent = snapshots.FirstOrDefault(p => p.Pid == s.ParentPid && p.Pid != s.Pid);
            return depth[s.Pid] = parent is null ? 1 : Depth(parent) + 1;
        }
        var now = _services.Time.GetUtcNow();
        var toolsById = Items.OfType<ToolUseItem>().ToDictionary(t => t.ToolUseId);
        Processes.Clear();
        foreach (var snapshot in Ordered(snapshots))
        {
            Processes.Add(new ProcessRow
            {
                Snapshot = snapshot,
                Indent = Depth(snapshot) * 14,
                RunningText = snapshot.StartTime is { } started ? Elapsed(now - started) : "",
                Tool = _processTools.TryGetValue(snapshot.Pid, out var toolId) && toolsById.TryGetValue(toolId, out var tool) ? tool : null,
            });
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
            ScrollToRequested?.Invoke(tool);
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
        var taskId = row.Tool is { } tool ? Tasks.TaskIdFor(tool.ToolUseId) : null;
        _shell.Confirm(
            $"Stop {row.Name} (PID {row.Pid})?",
            taskId is not null
                ? "It's a background task Claude started. Claude Code stops it and tells Claude it ended."
                : "Claudette asks it to close, then ends it if it doesn't. Claude isn't told directly.",
            "Stop",
            async () =>
            {
                try
                {
                    if (taskId is not null && _session is not null)
                    {
                        await _session.StopTaskAsync(taskId);
                    }
                    else if ((ProjectJobTreeHolding(row.Pid) ?? _tree) is { } tree)
                    {
                        await tree.StopAsync(row.Pid, TimeSpan.FromSeconds(3));
                    }
                }
                catch (Exception ex)
                {
                    _conversation.AddNote($"Couldn't stop {row.Name}: {ex.Message}", NoteKind.Error);
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
            return [.. _tree?.Sample(includeCommandLines: false).Where(s => !s.IsRoot) ?? [], .. ProjectJobProcesses(includeCommandLines: false)];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Stops the sampler, and optionally every process the tab started (DESIGN.md §4, "Cleanup").</summary>
    private async Task EndProcessTreeAsync(bool killProcesses)
    {
        _sampler?.Dispose();
        _sampler = null;
        // Its processes leave the header's total; on the UI thread, like the samples, since closing can finish elsewhere.
        _services.Dispatcher.Post(() => SetLatestProcessSummary(null));
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
