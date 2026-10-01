using Claudette.App.Services;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Claudette.App.ViewModels.Settings;

/// <summary>Something whose keyboard shortcut is being set in Settings: a command, or a quick suffix.</summary>
public abstract partial class ShortcutEditor : ObservableObject
{
    /// <summary>Waiting for the user to press the new shortcut.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShortcutText))]
    public partial bool IsRecording { get; set; }

    /// <summary>Why the last keys pressed can't be used.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    public abstract KeyChord? Shortcut { get; }

    public string ShortcutText => IsRecording ? "Press keys…" : Shortcut?.Display(Shortcuts.IsMac) ?? "None";

    public void Refresh()
    {
        OnPropertyChanged(nameof(ShortcutText));
        OnPropertyChanged(nameof(Shortcut));
        OnRefresh();
    }

    protected virtual void OnRefresh()
    {
    }
}

/// <summary>
/// The shortcut being set in Settings (DESIGN.md §14, "Keyboard shortcuts"): a command's, in Keyboard, or a quick
/// suffix's, in Quick suffixes. One at a time across both pages, and each checked against both for conflicts.
/// </summary>
public sealed class ShortcutRecorder(AppSettings settings, Action save)
{
    private ShortcutEditor? _recording;

    /// <summary>A shortcut was saved: both pages show their shortcuts as they are now.</summary>
    public event Action? Saved;

    /// <summary>A shortcut is being recorded: the window passes the next key press to <see cref="Record"/>.</summary>
    public bool IsRecording => _recording is not null;

    public void Start(ShortcutEditor? editor)
    {
        Cancel();
        if (editor is not null)
        {
            editor.Error = null;
            editor.IsRecording = true;
            _recording = editor;
        }
    }

    public void Cancel()
    {
        if (_recording is not null)
        {
            _recording.IsRecording = false;
            _recording = null;
        }
    }

    /// <summary>
    /// The keys pressed while recording. Refused, with the reason shown, when another command or suffix already uses
    /// them, or when they'd get in the way of typing.
    /// </summary>
    public void Record(KeyChord chord)
    {
        if (_recording is not { } editor)
        {
            return;
        }
        var goToTab = editor is ShortcutRow { Command.Id: KeyboardShortcuts.GoToTab };
        if (goToTab)
        {
            if (chord.Key.Length != 2 || chord.Key[0] != 'D' || chord.Key[1] is < '1' or > '9')
            {
                editor.Error = "Press a number key from 1 to 9, with the modifier keys you want.";
                return;
            }
            chord = chord with { Key = "D1" };
        }
        if (!AllowedWhileTyping(chord))
        {
            editor.Error = "Add Ctrl, Alt or Cmd, so typing still works.";
            return;
        }
        var id = editor switch
        {
            ShortcutRow row => row.Command.Id,
            QuickSuffixEditor suffix => suffix.Suffix.Id,
            _ => null,
        };
        if (KeyboardShortcuts.FindConflict(settings, chord, id, Shortcuts.IsMac) is { } conflict)
        {
            editor.Error = $"{chord.Display(Shortcuts.IsMac)} is already used by {conflict}.";
            return;
        }
        switch (editor)
        {
            case ShortcutRow row when chord == row.Command.Default:
                settings.Keyboard.Bindings.Remove(row.Command.Id);
                break;
            case ShortcutRow row:
                settings.Keyboard.Bindings[row.Command.Id] = chord.ToString();
                break;
            case QuickSuffixEditor suffix:
                suffix.Suffix.Shortcut = chord.ToString();
                break;
        }
        editor.Error = null;
        Cancel();
        Save();
    }

    /// <summary>Removes a shortcut: the command is then only in menus and buttons.</summary>
    public void Clear(ShortcutEditor? editor)
    {
        switch (editor)
        {
            case ShortcutRow row:
                settings.Keyboard.Bindings[row.Command.Id] = "";
                break;
            case QuickSuffixEditor suffix:
                suffix.Suffix.Shortcut = null;
                break;
            default:
                return;
        }
        Save();
    }

    /// <summary>Saves a changed shortcut, and has both pages show it.</summary>
    public void Save()
    {
        save();
        Saved?.Invoke();
    }

    /// <summary>Letters, digits and punctuation need Ctrl, Alt or Cmd; Escape, Tab, Enter and F-keys don't.</summary>
    private static bool AllowedWhileTyping(KeyChord chord) =>
        (chord.Modifiers & (ChordModifiers.Primary | ChordModifiers.Ctrl | ChordModifiers.Alt)) != 0
        || chord.Key is "Escape" or "Tab" or "Enter" or "Back" or "Delete" || (chord.Key.Length > 1 && chord.Key[0] == 'F' && char.IsDigit(chord.Key[1]));
}
