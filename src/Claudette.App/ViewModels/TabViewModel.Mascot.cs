using Claudette.App.Mascot;
using Claudette.App.Services;
using Claudette.Core.Sessions;

namespace Claudette.App.ViewModels;

/// <summary>Claudette on the composer (DESIGN.md §5): what she reacts to while this tab is the selected one.</summary>
public sealed partial class TabViewModel
{
    /// <summary>Her, for the composer to show; one for every tab.</summary>
    public MascotService Mascot => _services.Mascot;

    /// <summary>What she does on this tab: types while Claude works, waves while a prompt waits, dozes at a usage limit.</summary>
    internal MascotMood MascotMood => HasLimitWait
        ? MascotMood.Resting
        : Status switch
        {
            TabStatus.Working => MascotMood.Working,
            TabStatus.NeedsInput => MascotMood.Waiting,
            _ => MascotMood.Idle,
        };

    /// <summary>Tells her what this tab is doing, while it's the tab she's on.</summary>
    private void TellMascot()
    {
        if (IsSelected)
        {
            _services.Mascot.Follow(MascotMood);
        }
    }

    partial void OnLimitWaitChanged(LimitWait? value) => TellMascot();
}
