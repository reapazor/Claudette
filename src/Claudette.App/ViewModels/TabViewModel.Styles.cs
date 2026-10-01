using Claudette.App.Conversation;
using Claudette.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>An output style the tab can switch to, in the effort dropdown.</summary>
public sealed record OutputStyleChoice(string Name, bool IsCurrent)
{
    /// <summary>"Default" for Claude Code's own name, <c>default</c>.</summary>
    public string Label => string.Equals(Name, "default", StringComparison.OrdinalIgnoreCase) ? "Default" : Name;
}

public sealed partial class TabViewModel
{
    // ---- Ultracode and output style (DESIGN.md §5, "Model and effort") ---------------------------------------

    /// <summary>Ultracode is on: Claude may run workflows of subagents on its own for big tasks.</summary>
    public bool IsUltracode => State.Ultracode;

    /// <summary>The effort dropdown's switch: on or off from the next turn, kept with the tab.</summary>
    [RelayCommand]
    private async Task ToggleUltracodeAsync()
    {
        var on = !State.Ultracode;
        if (_session is { } session && !await ApplyUltracodeAsync(session, on))
        {
            return;
        }
        State.Ultracode = on;
        _services.SaveState();
        OnPropertyChanged(nameof(IsUltracode));
        OnPropertyChanged(nameof(EffortName));
        OnPropertyChanged(nameof(InfoRows));
    }

    private async Task<bool> ApplyUltracodeAsync(ClaudeSession session, bool on)
    {
        try
        {
            await session.SetUltracodeAsync(on);
            return true;
        }
        catch (Exception ex)
        {
            _conversation.AddNote($"Couldn't turn ultracode {(on ? "on" : "off")}: {ex.Message}", NoteKind.Error);
            return false;
        }
    }

    /// <summary>The output styles the session offers, with the current one marked; empty before it starts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutputStyles))]
    public partial IReadOnlyList<OutputStyleChoice> OutputStyleChoices { get; private set; } = [];

    /// <summary>A choice to make: more than one style, and a tab that reads the folder's settings (where the choice is kept).</summary>
    public bool HasOutputStyles => OutputStyleChoices.Count > 1;

    private void UpdateOutputStyles(InitializeResult? initialization, string? current = null)
    {
        current ??= initialization?.OutputStyle;
        OutputStyleChoices = State.WithoutProjectSettings || initialization is null
            ? []
            : [.. initialization.AvailableOutputStyles.Select(s => new OutputStyleChoice(s, string.Equals(s, current, StringComparison.OrdinalIgnoreCase)))];
    }

    /// <summary>
    /// Switches the output style for this folder: Claude Code keeps it in the project's local settings, as
    /// <c>/output-style</c> does, so terminal sessions there use it too. It applies from the next request.
    /// </summary>
    [RelayCommand]
    private async Task ChooseOutputStyleAsync(OutputStyleChoice? choice)
    {
        if (choice is null || choice.IsCurrent || _session is not { } session)
        {
            return;
        }
        try
        {
            await session.SetOutputStyleAsync(choice.Name);
            UpdateOutputStyles(session.Initialization, choice.Name);
            _conversation.AddNote($"Output style: {choice.Label}. Kept in this folder's .claude/settings.local.json, from the next reply.");
        }
        catch (Exception ex)
        {
            _conversation.AddNote($"Couldn't change the output style: {ex.Message}", NoteKind.Error);
        }
    }
}
