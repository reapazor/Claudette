using System.Collections.ObjectModel;
using System.Globalization;
using Claudette.App.Conversation;
using Claudette.Core.ProjectTools;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// One run of a project action as a job (DESIGN.md §18, "Project tools"): a build, project file generation, a custom
/// command, Clean's deletion, or one that couldn't start. It keeps its own log and stays in the sidebar under its tab,
/// whatever the result, until the user closes it or the tab.
/// </summary>
public sealed partial class ProjectRunViewModel : ObservableObject
{
    /// <summary>A run keeps at most this many lines of its output.</summary>
    public const int MaxOutputLines = 5000;

    private readonly ProjectToolsViewModel _tools;
    private readonly TimeProvider _time;
    private bool _couldntStart;

    internal ProjectRunViewModel(ProjectToolsViewModel tools, string name, ProjectAction? action, TimeProvider time)
    {
        _tools = tools;
        _time = time;
        Name = name;
        Action = action;
        Started = time.GetUtcNow();
        Status = $"{name} is running…";
    }

    /// <summary>"Build editor", "Clean intermediates".</summary>
    public string Name { get; }

    public DateTimeOffset Started { get; }

    public DateTimeOffset? Ended { get; private set; }

    /// <summary>The action it ran, for what follows it (Build and launch) and its summary (test counts).</summary>
    internal ProjectAction? Action { get; }

    /// <summary>The job, for Stop; null for a run that couldn't start.</summary>
    internal ProjectJob? Job { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(Succeeded), nameof(Failed), nameof(IsStopped), nameof(Glyph), nameof(Detail), nameof(Tip))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand), nameof(CloseCommand))]
    public partial ProjectJobState State { get; private set; } = ProjectJobState.Running;

    public bool IsRunning => State == ProjectJobState.Running;

    public bool Succeeded => State == ProjectJobState.Succeeded;

    public bool Failed => State == ProjectJobState.Failed;

    public bool IsStopped => State == ProjectJobState.Stopped;

    /// <summary>The process's exit code, once it has ended; null for work Claudette did itself.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>"Build editor is running…", "Build editor succeeded.", "Build editor failed (exit code 6)."</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tip))]
    public partial string Status { get; private set; }

    /// <summary>The sidebar entry's tip: the status line, and when it started and how long it took.</summary>
    public string Tip => State == ProjectJobState.Running ? $"{Status}\nStarted {MessageTimes.Short(Started, _time)}."
        : _couldntStart ? Status
        : $"{Status}\nStarted {MessageTimes.Short(Started, _time)} and took {WorkingLine.Elapsed((Ended ?? Started) - Started)}.";

    /// <summary>The sidebar entry's state: busy while running, then ✓, ✕ or ■.</summary>
    public string Glyph => State switch
    {
        ProjectJobState.Succeeded => "✓",
        ProjectJobState.Failed => "✕",
        ProjectJobState.Stopped => "■",
        _ => "●",
    };

    /// <summary>
    /// The sidebar entry's detail: how long it has been running, or how it ended and when: "Running · 1m 05s",
    /// "Failed · exit code 6 · 14:32".
    /// </summary>
    public string Detail => State == ProjectJobState.Running
        ? $"Running · {WorkingLine.Elapsed(_time.GetUtcNow() - Started)}"
        : $"{Result} · {MessageTimes.Short(Ended ?? Started, _time)}";

    private string Result => State switch
    {
        ProjectJobState.Succeeded => "Succeeded",
        ProjectJobState.Stopped => "Stopped",
        _ when _couldntStart => "Couldn't start",
        _ => ExitCode is { } code ? $"Failed · exit code {code.ToString(CultureInfo.InvariantCulture)}" : "Failed",
    };

    /// <summary>The run's output, at most <see cref="MaxOutputLines"/> lines.</summary>
    public ObservableCollection<string> Output { get; } = [];

    /// <summary>Lines dropped from the start of the output to keep it to the limit.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputNote))]
    public partial int OutputDropped { get; private set; }

    public string? OutputNote => OutputDropped > 0
        ? $"Showing the last {MaxOutputLines.ToString("N0", CultureInfo.CurrentCulture)} lines; {OutputDropped.ToString("N0", CultureInfo.CurrentCulture)} earlier ones were dropped."
        : null;

    /// <summary>Its log is on the Project page of its tab, which is the one showing: the sidebar entry is highlighted.</summary>
    [ObservableProperty]
    public partial bool IsShowing { get; internal set; }

    internal void Append(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            Output.Add(line);
        }
        var extra = Output.Count - MaxOutputLines;
        if (extra > 0)
        {
            for (var i = 0; i < extra; i++)
            {
                Output.RemoveAt(0);
            }
            OutputDropped += extra;
        }
    }

    /// <summary>The job ended: its state, exit code and status line, which also ends the log.</summary>
    internal void End(ProjectJobState state, int? exitCode, string status)
    {
        Ended = _time.GetUtcNow();
        ExitCode = exitCode;
        Status = status;
        State = state;
        Append([status]);
    }

    /// <summary>The job's process couldn't start: a failed run whose log says why.</summary>
    internal void EndCouldntStart(string status)
    {
        _couldntStart = true;
        Ended = _time.GetUtcNow();
        Status = status;
        State = ProjectJobState.Failed;
    }

    /// <summary>The running time moved on.</summary>
    internal void Tick() => OnPropertyChanged(nameof(Detail));

    /// <summary>A click on the sidebar entry: selects its tab and shows its log on the Project page.</summary>
    [RelayCommand]
    private void Open() => _tools.OpenRun(this);

    /// <summary>Ends the job's whole process tree, or its work, like the Project page's Stop.</summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => Job?.Stop();

    private bool CanClose => !IsRunning;

    /// <summary>Takes the entry and its log away. A running one has Stop instead, so a stray click never ends a build.</summary>
    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close() => _tools.CloseRun(this);

    public override string ToString() => $"{Name}: {Status}";
}
