using Claudette.App.Mascot;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.Services;

/// <summary>
/// Claudette on the composer (DESIGN.md §5): one of her for every tab, on the selected tab's composer, reacting to that
/// tab. She's there while Settings → Appearance → <b>Show Claudette on the composer</b> is on, and stands still while
/// motion is reduced.
/// </summary>
public sealed partial class MascotService : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private MascotMood _mood;

    public MascotService(AppServices services)
    {
        _services = services;
        services.MotionChanged += OnMotionChanged;
        OnSettingsChanged();
    }

    /// <summary>Her, while the setting is on; null while it's off.</summary>
    [ObservableProperty]
    public partial MascotDirector? Director { get; private set; }

    /// <summary>The setting may have changed: she comes, climbing up from behind the box, or goes.</summary>
    public void OnSettingsChanged()
    {
        var on = _services.Settings.Appearance.ShowClaudette;
        if (on && Director is null)
        {
            var director = new MascotDirector(_services.Time, _services.Dispatcher, _services.Random);
            director.SetStill(_services.ReduceMotion);
            director.SetMood(_mood);
            Director = director;
        }
        else if (!on && Director is { } gone)
        {
            Director = null;
            gone.Dispose();
        }
    }

    /// <summary>What the selected tab is doing.</summary>
    public void Follow(MascotMood mood)
    {
        _mood = mood;
        Director?.SetMood(mood);
    }

    /// <summary>The selected tab's turn finished.</summary>
    public void TurnFinished() => Director?.TurnFinished();

    /// <summary>The user typed in the selected tab's composer.</summary>
    public void Nudge() => Director?.Nudge();

    /// <summary>Her menu's <b>Hide Claudette</b>: turns the setting off. Settings → Appearance brings her back.</summary>
    [RelayCommand]
    private void Hide()
    {
        _services.Settings.Appearance.ShowClaudette = false;
        _services.SaveSettings();
    }

    private void OnMotionChanged(object? sender, EventArgs e) => Director?.SetStill(_services.ReduceMotion);

    public void Dispose()
    {
        _services.MotionChanged -= OnMotionChanged;
        Director?.Dispose();
        Director = null;
    }
}
