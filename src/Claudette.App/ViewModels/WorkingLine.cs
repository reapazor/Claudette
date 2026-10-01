using System.Globalization;
using Claudette.App.Services;
using Claudette.Core.Claude;
using Claudette.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Claudette.App.ViewModels;

/// <summary>
/// The line above the composer while a turn runs (DESIGN.md §5, "Working line"): a twinkling glyph, a fun verb that
/// changes every few seconds, how long the turn has run, its tokens so far, and how to stop it.
/// </summary>
/// <param name="verbs">The verbs to pick from: <see cref="SpinnerVerbs"/>, with the user's <c>spinnerVerbs</c>.</param>
/// <param name="isFun">Settings → Appearance → Show fun words while Claude works. Off shows a still "Working…".</param>
/// <param name="turnTokens">The turn's tokens so far.</param>
/// <param name="stopShortcut">The Stop shortcut as it reads now, or null when it has been removed.</param>
/// <param name="showActivity">Settings → Appearance → Show what Claude is doing: the running tool instead of the verb.</param>
/// <param name="isStill">Motion is reduced (DESIGN.md §3, "Accessibility"): the glyph stays still.</param>
public sealed partial class WorkingLine(
    TimeProvider timeProvider,
    IUiDispatcher dispatcher,
    Func<IReadOnlyList<string>> verbs,
    Func<bool> isFun,
    Func<long> turnTokens,
    Func<string?> stopShortcut,
    Random random,
    Func<bool>? showActivity = null,
    Func<bool>? isStill = null) : ObservableObject, IDisposable
{
    /// <summary>The frames of the glyph, as Claude Code's terminal spinner draws them, there and back.</summary>
    public static readonly IReadOnlyList<string> Frames = ["·", "✢", "✳", "✶", "✻", "✽", "✻", "✶", "✳", "✢"];

    public static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(120);

    /// <summary>How long a verb stays before the next one.</summary>
    public static readonly TimeSpan VerbInterval = TimeSpan.FromSeconds(8);

    /// <summary>The glyph when the fun is off, or motion is reduced: still, in the same place.</summary>
    public const string StillGlyph = "✻";

    private readonly Lock _lock = new();
    private ITimer? _timer;
    private bool _running;
    private bool _shown = true;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _verbSince;
    private int _frame;
    private string _funVerb = "Working…";
    private string? _activity;

    /// <summary>A turn is running: the line counts, whether or not it's shown right now.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; private set; }

    [ObservableProperty]
    public partial string Glyph { get; private set; } = StillGlyph;

    /// <summary>"Noodling…", "Running dotnet test…" while a tool runs, or "Working…" with the fun off.</summary>
    [ObservableProperty]
    public partial string Verb { get; private set; } = "Working…";

    /// <summary>The running tools in full (a whole command, a file's path) for the line's tooltip, or null.</summary>
    [ObservableProperty]
    public partial string? ActivityDetail { get; private set; }

    /// <summary>"42s · 3.1k tokens · Esc to stop".</summary>
    [ObservableProperty]
    public partial string Detail { get; private set; } = "";

    /// <summary>Starts counting a turn. A turn that's already counting carries on, for example after a prompt was answered.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_running)
            {
                return;
            }
            _running = true;
            _startedAt = _verbSince = timeProvider.GetUtcNow();
            _frame = 0;
            UpdateTimer();
        }
        IsActive = true;
        _funVerb = NextVerb(null);
        Update();
    }

    /// <summary>
    /// Whether the line is on screen: its tab is the selected one. A hidden line keeps counting the turn but doesn't
    /// tick, so tabs working in the background don't wake the UI eight times a second; it catches up when shown.
    /// </summary>
    public void SetShown(bool shown)
    {
        lock (_lock)
        {
            if (_shown == shown)
            {
                return;
            }
            _shown = shown;
            UpdateTimer();
        }
        if (shown && IsActive)
        {
            Update();
        }
    }

    /// <summary>The frame timer runs while a turn runs and the line is shown. Called under the lock.</summary>
    private void UpdateTimer()
    {
        if (_running && _shown)
        {
            _timer ??= timeProvider.CreateTimer(_ => dispatcher.Post(Tick), null, FrameInterval, FrameInterval);
        }
        else
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>What the running tools are doing (<see cref="Conversation.ToolActivity"/>), or null when none is running.</summary>
    public void SetActivity(string? activity, string? detail = null)
    {
        _activity = activity;
        ActivityDetail = activity is null ? null : detail;
        if (IsActive)
        {
            Update();
        }
    }

    /// <summary>The turn ended.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _running = false;
            UpdateTimer();
        }
        IsActive = false;
        _activity = null;
        ActivityDetail = null;
    }

    /// <summary>Shows the latest tokens straight away, rather than on the next tick.</summary>
    public void Refresh()
    {
        if (IsActive)
        {
            Update();
        }
    }

    private void Tick()
    {
        if (!IsActive)
        {
            return;
        }
        _frame++;
        var now = timeProvider.GetUtcNow();
        if (now - _verbSince >= VerbInterval)
        {
            _verbSince = now;
            _funVerb = NextVerb(_funVerb);
        }
        Update();
    }

    private void Update()
    {
        var fun = isFun();
        // With motion reduced (DESIGN.md §3, "Accessibility"), the verbs still change; only the glyph stops twinkling.
        Glyph = fun && !(isStill?.Invoke() ?? false) ? Frames[_frame % Frames.Count] : StillGlyph;
        if (!fun)
        {
            _funVerb = "Working…";
        }
        else if (_funVerb == "Working…")
        {
            // The fun was just turned back on.
            _funVerb = NextVerb(null);
        }
        Verb = _activity is { } activity && (showActivity?.Invoke() ?? false)
            ? activity.EndsWith('…') ? activity : activity + "…"
            : _funVerb;
        var parts = new List<string> { Elapsed(timeProvider.GetUtcNow() - _startedAt) };
        if (turnTokens() is > 0 and var tokens)
        {
            parts.Add(TokenTotals.Short(tokens).Replace(" tok", " tokens", StringComparison.Ordinal));
        }
        if (stopShortcut() is { } stop)
        {
            parts.Add($"{stop} to stop");
        }
        Detail = string.Join(" · ", parts);
    }

    /// <summary>A verb other than the one showing, with an ellipsis.</summary>
    private string NextVerb(string? current)
    {
        if (!isFun())
        {
            return "Working…";
        }
        var list = verbs();
        if (list.Count == 0)
        {
            return "Working…";
        }
        string next;
        do
        {
            next = list[random.Next(list.Count)] + "…";
        }
        while (next == current && list.Count > 1);
        return next;
    }

    /// <summary>"8s", "1m 05s", "1h 02m".</summary>
    public static string Elapsed(TimeSpan span) => span switch
    {
        { TotalHours: >= 1 } => string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}h {span.Minutes:00}m"),
        { TotalMinutes: >= 1 } => string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}m {span.Seconds:00}s"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, (int)span.TotalSeconds)}s"),
    };

    public void Dispose() => Stop();
}
