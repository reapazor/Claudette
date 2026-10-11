using Claudette.App.Mascot;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.Services;

/// <summary>
/// Claudette on the composer (DESIGN.md §5): one of her for every tab, on the selected tab's composer, reacting to that
/// tab. She's there while Settings → Appearance → <b>Show Claudette on the composer</b> is on, as big and as lively as
/// it says, and stands still while motion is reduced. What she says comes from here: tips, and that another tab needs
/// the user, with the shortcuts as they're bound now.
/// </summary>
public sealed partial class MascotService : ObservableObject, IMascotLines, IDisposable
{
    private readonly AppServices _services;
    private MascotSituation _situation = MascotSituation.Quiet;

    public MascotService(AppServices services)
    {
        _services = services;
        services.MotionChanged += OnMotionChanged;
        OnSettingsChanged();
    }

    /// <summary>Her, while the setting is on; null while it's off.</summary>
    [ObservableProperty]
    public partial MascotDirector? Director { get; private set; }

    /// <summary>Screen pixels in a cell of her sprite at 100%: Settings → Appearance → her size.</summary>
    [ObservableProperty]
    public partial double PixelsPerCell { get; private set; } = 3;

    private AppearanceSettings Settings => _services.Settings.Appearance;

    /// <summary>The settings may have changed: she comes, climbing up from behind the box, or goes, or changes.</summary>
    public void OnSettingsChanged()
    {
        PixelsPerCell = Settings.ClaudetteSize switch
        {
            ClaudetteSize.Small => 2,
            ClaudetteSize.Large => 4,
            _ => 3,
        };
        if (Settings.ShowClaudette && Director is null)
        {
            var director = new MascotDirector(_services.Time, _services.Dispatcher, _services.Random, this);
            director.SetStill(_services.ReduceMotion);
            director.SetSituation(_situation);
            Director = director;
        }
        else if (!Settings.ShowClaudette && Director is { } gone)
        {
            Director = null;
            gone.Dispose();
        }
        Director?.SetSpell(Settings.ClaudetteLiveliness == ClaudetteLiveliness.Calm ? MascotSpell.Calm : MascotSpell.Lively);
        Director?.SetTips(Settings.ClaudetteTips);
    }

    /// <summary>What the selected tab is doing.</summary>
    public void Follow(MascotSituation situation)
    {
        _situation = situation;
        Director?.SetSituation(situation);
    }

    /// <summary>The selected tab's turn finished.</summary>
    public void TurnFinished() => Director?.TurnFinished();

    /// <summary>The selected tab's turn failed.</summary>
    public void TurnFailed() => Director?.TurnFailed();

    /// <summary>Every one of Claude's tasks in the selected tab is done.</summary>
    public void AllTasksDone() => Director?.AllTasksDone();

    /// <summary>Every file the selected tab changed is reviewed.</summary>
    public void AllReviewed() => Director?.AllReviewed();

    /// <summary>The user sent a message from the selected tab.</summary>
    public void MessageSent() => Director?.MessageSent();

    /// <summary>A file or image was attached in the selected tab.</summary>
    public void Caught() => Director?.Caught();

    /// <summary>A huge paste was kept as an attachment in the selected tab.</summary>
    public void HeavyPaste() => Director?.HeavyPaste();

    /// <summary>The user typed her name in the selected tab's composer.</summary>
    public void Greeted() => Director?.Greeted();

    /// <summary>A file is being dragged over the selected tab's composer, or no longer is.</summary>
    public void DragHover(bool hovering) => Director?.SetDragHover(hovering);

    /// <summary>The user typed in the selected tab's composer.</summary>
    public void Nudge() => Director?.Nudge();

    /// <summary>Her menu's <b>Hide Claudette</b>: turns the setting off. Settings → Appearance brings her back.</summary>
    [RelayCommand]
    private void Hide()
    {
        Settings.ShowClaudette = false;
        _services.SaveSettings();
    }

    private void OnMotionChanged(object? sender, EventArgs e) => Director?.SetStill(_services.ReduceMotion);

    // ---- What she says ------------------------------------------------------------------------------------------------

    /// <summary>Her tips: the shortcuts as bound now (none for a shortcut that's been removed), and a few that need none.</summary>
    public string? Tip(Random random)
    {
        var tips = new (string? Shortcut, string Text)[]
        {
            (Key(KeyboardShortcuts.NextTabNeedingInput), "{0} goes to the next tab waiting for you"),
            (Key(KeyboardShortcuts.CommandPalette), "{0} opens the command palette"),
            (Key(KeyboardShortcuts.Find), "{0} finds things in the conversation"),
            (Key(KeyboardShortcuts.Stash), "{0} puts what you're typing aside for later"),
            (Key(KeyboardShortcuts.Suffixes), "{0} adds a quick suffix to your message"),
            (Key(KeyboardShortcuts.History), "{0} finds a past session"),
            (Key(KeyboardShortcuts.ZoomIn), "{0} makes everything bigger"),
            ("", "Up brings back your earlier prompts"),
            ("", "Drop a file on the message box to attach it"),
            ("", "Right-click me if you'd like me to go"),
        }.Where(t => t.Shortcut is not null).ToArray();
        if (tips.Length == 0)
        {
            return null;
        }
        var (shortcut, text) = tips[random.Next(tips.Length)];
        return string.Format(System.Globalization.CultureInfo.CurrentCulture, text, shortcut);
    }

    /// <summary>"Another tab needs you · Ctrl+J", or "2 tabs need you · Ctrl+J".</summary>
    public string OthersWaiting(int count)
    {
        var who = count == 1 ? "Another tab needs you" : $"{count} tabs need you";
        return Key(KeyboardShortcuts.NextTabNeedingInput) is { } key ? $"{who} · {key}" : who;
    }

    private string? Key(string command) => _services.Tips.Text(command);

    public void Dispose()
    {
        _services.MotionChanged -= OnMotionChanged;
        Director?.Dispose();
        Director = null;
    }
}
