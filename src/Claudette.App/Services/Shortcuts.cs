using Avalonia.Input;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Claudette.App.Services;

/// <summary>Matches key presses against the shortcuts in Settings → Keyboard (DESIGN.md §14).</summary>
public static class Shortcuts
{
    public static bool IsMac { get; } = OperatingSystem.IsMacOS();

    /// <summary>
    /// The chord for a key press, or null for a modifier key on its own. Avalonia reports Cmd as Meta on macOS, which is
    /// Primary there; elsewhere Ctrl is Primary.
    /// </summary>
    public static KeyChord? FromKeyPress(Key key, KeyModifiers modifiers)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.None or Key.System)
        {
            return null;
        }
        return new KeyChord(Modifiers(modifiers), KeyName(key));
    }

    /// <summary>Does this key press trigger the command's shortcut?</summary>
    public static bool Matches(KeyboardSettings settings, string commandId, Key key, KeyModifiers modifiers) =>
        Matches(KeyboardShortcuts.Resolve(settings, commandId), key, modifiers);

    public static bool Matches(KeyChord? bound, Key key, KeyModifiers modifiers)
    {
        if (bound is null || !Enum.TryParse<Key>(bound.Key, ignoreCase: true, out var boundKey)
            || KeyChord.Normalize(bound.Modifiers, IsMac) != KeyChord.Normalize(Modifiers(modifiers), IsMac))
        {
            return false;
        }
        return bound.IsDigitRange ? Digit(key) is not null : boundKey == key || NumberPadTwin(key) == boundKey;
    }

    /// <summary>The main keyboard's key for a number pad key: <c>NumPad0</c> is <c>D0</c>, <c>Add</c> is <c>OemPlus</c>.</summary>
    private static Key? NumberPadTwin(Key key) => key switch
    {
        >= Key.NumPad0 and <= Key.NumPad9 => Key.D0 + (key - Key.NumPad0),
        Key.Add => Key.OemPlus,
        Key.Subtract => Key.OemMinus,
        _ => null,
    };

    /// <summary>1–9 for the digit keys, on the main row or the number pad.</summary>
    public static int? Digit(Key key) => key switch
    {
        >= Key.D1 and <= Key.D9 => key - Key.D0,
        >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad0,
        _ => null,
    };

    private static ChordModifiers Modifiers(KeyModifiers modifiers)
    {
        var chord = ChordModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            chord |= ChordModifiers.Shift;
        }
        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            chord |= ChordModifiers.Alt;
        }
        if (IsMac)
        {
            chord |= (modifiers.HasFlag(KeyModifiers.Meta) ? ChordModifiers.Primary : 0) | (modifiers.HasFlag(KeyModifiers.Control) ? ChordModifiers.Ctrl : 0);
        }
        else if (modifiers.HasFlag(KeyModifiers.Control))
        {
            chord |= ChordModifiers.Primary;
        }
        return chord;
    }

    /// <summary>One name per key, although Avalonia gives some keys two (Enter and Return, Oem1 and OemSemicolon).</summary>
    private static string KeyName(Key key) => key switch
    {
        Key.Enter => "Enter",
        Key.Back => "Back",
        Key.OemSemicolon => "OemSemicolon",
        Key.OemQuestion => "OemQuestion",
        Key.OemTilde => "OemTilde",
        Key.OemOpenBrackets => "OemOpenBrackets",
        Key.OemPipe => "OemPipe",
        Key.OemCloseBrackets => "OemCloseBrackets",
        Key.OemQuotes => "OemQuotes",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        >= Key.NumPad0 and <= Key.NumPad9 => $"D{key - Key.NumPad0}",
        Key.Add => "OemPlus",
        Key.Subtract => "OemMinus",
        _ => key.ToString(),
    };
}

/// <summary>
/// Tooltips that name a shortcut, such as "New tab (Ctrl+T)", kept current when shortcuts are rebound. Shared by the
/// views through <see cref="AppServices.Tips"/>.
/// </summary>
public sealed class ShortcutTips(AppSettings settings) : ObservableObject
{
    public string NewTab => Tip("New tab", KeyboardShortcuts.NewTab);

    public string Settings => Tip("Settings", KeyboardShortcuts.Settings);

    public string History => Tip("Resume a past session", KeyboardShortcuts.History);

    public string Suffixes => Tip("Quick suffixes", KeyboardShortcuts.Suffixes);

    public string CollapseSidebar => Tip("Collapse the sidebar", KeyboardShortcuts.ToggleSidebar);

    public string ExpandSidebar => Tip("Expand the sidebar", KeyboardShortcuts.ToggleSidebar);

    public string Allow => Text(KeyboardShortcuts.AllowPrompt) ?? "";

    public string Deny => Text(KeyboardShortcuts.DenyPrompt) ?? "";

    public string ComposerPlaceholder => Text(KeyboardShortcuts.Stop) is { } stop
        ? $"Message Claude…  (Enter to send, Shift+Enter for a new line, {stop} to stop)"
        : "Message Claude…  (Enter to send, Shift+Enter for a new line)";

    /// <summary>The shortcut as it reads on this OS, or null when it has been removed.</summary>
    public string? Text(string commandId) => KeyboardShortcuts.Resolve(settings.Keyboard, commandId)?.Display(Shortcuts.IsMac);

    public void Refresh() => OnPropertyChanged(string.Empty);

    private string Tip(string label, string commandId) => Text(commandId) is { } shortcut ? $"{label} ({shortcut})" : label;
}
