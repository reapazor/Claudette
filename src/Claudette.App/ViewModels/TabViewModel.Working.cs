using Claudette.Core.Claude;

namespace Claudette.App.ViewModels;

/// <summary>The working line above the composer while a turn runs (DESIGN.md §5, "Working line").</summary>
public sealed partial class TabViewModel
{
    private IReadOnlyList<string> _spinnerVerbs = SpinnerVerbs.BuiltIn;

    /// <summary>The glyph, verb, time and tokens of the turn in progress.</summary>
    public WorkingLine Working => field ??= new WorkingLine(
        _services.Time,
        _services.Dispatcher,
        () => _spinnerVerbs,
        () => _services.Settings.Appearance.FunWorkingWords,
        () => _callUsage.TurnTokens,
        () => _services.Tips.Text(Core.Settings.KeyboardShortcuts.Stop),
        _services.Random);

    /// <summary>Shown while Claude works; hidden while a prompt waits on the user, though the turn's time runs on.</summary>
    public bool IsWorkingLineShown => Status == TabStatus.Working;

    partial void OnStatusChanged(TabStatus oldValue, TabStatus newValue)
    {
        switch (newValue)
        {
            case TabStatus.Working:
                Working.Start();
                break;
            case TabStatus.NeedsInput:
                break;
            default:
                Working.Stop();
                break;
        }
        OnPropertyChanged(nameof(IsWorkingLineShown));
    }

    /// <summary>
    /// Reads the <c>spinnerVerbs</c> of Claude Code's settings for this folder, in the background, as the session
    /// starts: a verb added for the terminal shows here from the next turn.
    /// </summary>
    internal Task LoadSpinnerVerbsAsync()
    {
        var files = SpinnerVerbs.SettingsFiles(_services.ClaudeConfigDirectory, Folder);
        return Task.Run(() => _spinnerVerbs = SpinnerVerbs.Resolve(files));
    }
}
