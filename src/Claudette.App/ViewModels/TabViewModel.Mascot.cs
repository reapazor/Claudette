using System.ComponentModel;
using Avalonia.Media;
using Claudette.App.Mascot;
using Claudette.App.Services;
using Claudette.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Claudette.App.ViewModels;

/// <summary>Claudette on the composer (DESIGN.md §5): what she reacts to while this tab is the selected one.</summary>
public sealed partial class TabViewModel
{
    /// <summary>Her, for the composer to show; one for every tab.</summary>
    public MascotService Mascot => _services.Mascot;

    /// <summary>The colour of the tab's group in the sidebar, which her hair tie takes; null outside a group.</summary>
    [ObservableProperty]
    public partial Color? GroupColor { get; internal set; }

    /// <summary>Claude Code is compacting the conversation (<c>system/status</c>): she sweeps up.</summary>
    private bool _compacting;

    /// <summary>The composer's text ended with her name last time it changed, so she's already waved back.</summary>
    private bool _greeted;

    /// <summary>Everything she reacts to on this tab, as it is now.</summary>
    internal MascotSituation MascotSituation => new()
    {
        Mood = HasLimitWait
            ? MascotMood.Resting
            : Status switch
            {
                TabStatus.Working => MascotMood.Working,
                TabStatus.NeedsInput => MascotMood.Waiting,
                _ => MascotMood.Idle,
            },
        Tool = Status == TabStatus.Working && _runningTools.Count > 0 ? MascotTools.For(_runningTools[^1].Name) : MascotTool.None,
        Agents = Agents.ActiveCount,
        Planning = PermissionMode == "plan",
        ContextFull = Context.IsHigh,
        Compacting = _compacting,
        OthersWaiting = _shell.AllTabs.Count(t => t != this && t.NeedsInput),
    };

    internal MascotMood MascotMood => MascotSituation.Mood;

    /// <summary>Tells her what this tab is doing, while it's the tab she's on.</summary>
    internal void TellMascot()
    {
        if (IsSelected)
        {
            _services.Mascot.Follow(MascotSituation);
        }
    }

    /// <summary>Watches what she reacts to that lives in the tab's parts: the context window.</summary>
    private void WatchForMascot() => Context.PropertyChanged += OnContextChangedForMascot;

    private void OnContextChangedForMascot(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ContextViewModel.IsHigh))
        {
            TellMascot();
        }
    }

    partial void OnLimitWaitChanged(LimitWait? value) => TellMascot();

    partial void OnPermissionModeChanged(string? value) => TellMascot();

    /// <summary>Claude Code started or finished compacting the conversation.</summary>
    private void SetCompacting(bool compacting)
    {
        if (_compacting != compacting)
        {
            _compacting = compacting;
            TellMascot();
        }
    }

    /// <summary>Her name typed at the end of the composer's text: she waves back, once.</summary>
    private void GreetMascot(string text)
    {
        var greeted = text.TrimEnd().EndsWith("claudette", StringComparison.OrdinalIgnoreCase);
        if (greeted && !_greeted && IsSelected)
        {
            _services.Mascot.Greeted();
        }
        _greeted = greeted;
    }

    /// <summary>Something happened in this tab that she reacts to, while it's the tab she's on.</summary>
    private void TellMascot(Action<MascotService> happened)
    {
        if (IsSelected)
        {
            happened(_services.Mascot);
        }
    }
}
