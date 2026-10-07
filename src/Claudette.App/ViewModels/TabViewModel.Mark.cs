using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>A mark in the tab menu's <b>Mark</b> submenu (DESIGN.md §4, "Marks").</summary>
/// <param name="IsOn">The tab has this mark: the item is ticked, and picking it clears the mark.</param>
/// <param name="Toggle">The tab's <see cref="TabViewModel.ToggleMarkCommand"/>, which takes <paramref name="Mark"/>.</param>
public sealed record MarkMenuItem(TabMark Mark, bool IsOn, IRelayCommand<TabMark> Toggle)
{
    public string Name => TabMarkText.Name(Mark);
}

/// <summary>The tab's mark: an icon of the user's own, under its status icon (DESIGN.md §4, "Marks").</summary>
public sealed partial class TabViewModel
{
    /// <summary>How long after the mark changes a tab that syncs writes its record, so trying a few marks writes it once.</summary>
    internal static readonly TimeSpan MarkSyncDelay = TimeSpan.FromSeconds(2);

    /// <summary>Writes the library record once the mark settles, for a tab that syncs.</summary>
    private UiTimeout MarkSync => field ??= new(_services.Time, _services.Dispatcher);

    /// <summary>The tab's mark, or null for none. A mark a later Claudette added reads as none.</summary>
    public TabMark? Mark => TabMarks.Parse(State.Mark);

    public bool HasMark => Mark is not null;

    /// <summary>The tip on the row's mark: what the icon is, since what it means is the user's.</summary>
    public string? MarkTip => Mark is { } mark ? TabMarkText.Tip(mark) : null;

    /// <summary>The tab menu's <b>Mark</b> submenu: every mark, with the tab's own ticked.</summary>
    public IReadOnlyList<MarkMenuItem> MarkMenu => [.. TabMarks.All.Select(mark => new MarkMenuItem(mark, mark == Mark, ToggleMarkCommand))];

    /// <summary>A mark in the tab's menu: puts it on the tab, or takes it off when the tab has it already.</summary>
    [RelayCommand]
    private void ToggleMark(TabMark mark) => SetMark(Mark == mark ? null : mark);

    /// <summary><b>Clear mark</b> in the tab's menu.</summary>
    [RelayCommand(CanExecute = nameof(HasMark))]
    private void ClearMark() => SetMark(null);

    /// <summary>
    /// Marks the tab, or clears its mark with null. The mark is saved with the tab, kept for its session on this machine,
    /// and written to the library's record when the tab syncs, so the session has it wherever it's opened again.
    /// </summary>
    public void SetMark(TabMark? mark)
    {
        State.Mark = mark is { } chosen ? TabMarks.Key(chosen) : null;
        OnPropertyChanged(nameof(Mark));
        OnPropertyChanged(nameof(HasMark));
        OnPropertyChanged(nameof(MarkTip));
        OnPropertyChanged(nameof(MarkMenu));
        ClearMarkCommand.NotifyCanExecuteChanged();
        RememberMark(changed: true);
        _services.SaveState();
        QueueMarkSync();
    }

    /// <summary>
    /// Keeps the tab's mark for its session in this machine's state, for History to open it with: when the user changes
    /// it, a cleared one too, and when the session becomes the tab's own (its first turn, a copy's first, or after
    /// <c>/clear</c>) if that differs from what's kept. Not before: a copy has the original's id until its first turn,
    /// and its mark isn't the original's.
    /// </summary>
    private void RememberMark(bool changed = false)
    {
        if (State.SessionId is not { } sessionId || State.ForkOnNextStart || _forkAwaitingId)
        {
            return;
        }
        var marks = _services.State.SessionMarks;
        var kept = marks.GetValueOrDefault(sessionId);
        if (changed || (kept is null ? State.Mark is not null : kept.Mark != State.Mark))
        {
            marks[sessionId] = new SessionMark { Mark = State.Mark, At = _services.Time.GetUtcNow() };
        }
    }

    /// <summary>
    /// Brings the library's record up to date with the mark, for a tab that syncs (DESIGN.md §9, "Writing"). The
    /// transcript is only copied again if it changed. While a turn runs, the copy as it ends carries the mark instead.
    /// </summary>
    private void QueueMarkSync()
    {
        if (!State.SyncToLibrary || _closing.IsCancellationRequested)
        {
            return;
        }
        MarkSync.Restart(MarkSyncDelay, () =>
        {
            if (CanSyncNow && !_closing.IsCancellationRequested)
            {
                CopyToLibrary();
            }
        });
    }
}
