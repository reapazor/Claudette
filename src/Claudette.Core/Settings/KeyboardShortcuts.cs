using System.Text;

namespace Claudette.Core.Settings;

/// <summary>Modifier keys of a <see cref="KeyChord"/>.</summary>
[Flags]
public enum ChordModifiers
{
    None = 0,
    /// <summary>Ctrl on Windows and Linux, Cmd on macOS: the platform's command key.</summary>
    Primary = 1,
    Shift = 2,
    Alt = 4,
    /// <summary>Ctrl on macOS, where <see cref="Primary"/> is Cmd. On Windows and Linux it's the same key as Primary.</summary>
    Ctrl = 8,
}

/// <summary>
/// A keyboard shortcut, independent of the OS: <c>Primary+Shift+H</c> is Ctrl+Shift+H on Windows and Cmd+Shift+H on
/// macOS, so a shortcut synced between machines means the same thing on both (DESIGN.md §14). Keys use Avalonia's key
/// names (<c>T</c>, <c>D1</c>, <c>OemComma</c>, <c>Enter</c>, <c>Back</c>, <c>Tab</c>, <c>Escape</c>, <c>F5</c>).
/// </summary>
public sealed record KeyChord(ChordModifiers Modifiers, string Key)
{
    /// <summary>Reads <c>Primary+Shift+H</c>. Modifier names are case-insensitive; <c>Cmd</c> reads as Primary.</summary>
    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = new KeyChord(ChordModifiers.None, "");
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = ChordModifiers.None;
        foreach (var part in parts[..^1])
        {
            modifiers |= part.ToUpperInvariant() switch
            {
                "PRIMARY" or "CMD" or "COMMAND" => ChordModifiers.Primary,
                "SHIFT" => ChordModifiers.Shift,
                "ALT" or "OPTION" => ChordModifiers.Alt,
                "CTRL" or "CONTROL" => ChordModifiers.Ctrl,
                _ => (ChordModifiers)(-1),
            };
            if ((int)modifiers < 0)
            {
                return false;
            }
        }
        var key = parts[^1];
        if (key.Length == 0)
        {
            return false;
        }
        chord = new KeyChord(modifiers, key);
        return true;
    }

    /// <summary>The stored form, such as <c>Primary+Shift+H</c>.</summary>
    public override string ToString()
    {
        var text = new StringBuilder();
        foreach (var (flag, name) in Names)
        {
            if (Modifiers.HasFlag(flag))
            {
                text.Append(name).Append('+');
            }
        }
        return text.Append(Key).ToString();
    }

    /// <summary>How the shortcut reads on this OS: <c>Ctrl+Shift+H</c>, or <c>⌘⇧H</c> on macOS.</summary>
    public string Display(bool isMac)
    {
        var key = KeyLabel(Key, isMac);
        if (isMac)
        {
            var mac = new StringBuilder();
            if (Modifiers.HasFlag(ChordModifiers.Ctrl))
            {
                mac.Append('⌃');
            }
            if (Modifiers.HasFlag(ChordModifiers.Alt))
            {
                mac.Append('⌥');
            }
            if (Modifiers.HasFlag(ChordModifiers.Shift))
            {
                mac.Append('⇧');
            }
            if (Modifiers.HasFlag(ChordModifiers.Primary))
            {
                mac.Append('⌘');
            }
            return mac.Append(key).ToString();
        }
        var parts = new List<string>();
        if (Modifiers.HasFlag(ChordModifiers.Primary) || Modifiers.HasFlag(ChordModifiers.Ctrl))
        {
            parts.Add("Ctrl");
        }
        if (Modifiers.HasFlag(ChordModifiers.Alt))
        {
            parts.Add("Alt");
        }
        if (Modifiers.HasFlag(ChordModifiers.Shift))
        {
            parts.Add("Shift");
        }
        parts.Add(key);
        return string.Join('+', parts);
    }

    /// <summary>
    /// The same keys on this OS? On Windows and Linux, Primary and Ctrl are one key, so <c>Primary+Tab</c> and
    /// <c>Ctrl+Tab</c> collide there but not on macOS.
    /// </summary>
    public bool SameKeysAs(KeyChord other, bool isMac) =>
        string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase) && Normalize(Modifiers, isMac) == Normalize(other.Modifiers, isMac);

    /// <summary>A chord for "a digit 1–9 with these modifiers", such as Go to tab N.</summary>
    public bool IsDigitRange => Key == "D1";

    /// <summary>The modifiers as this OS sees them: on Windows and Linux, Ctrl is Primary.</summary>
    public static ChordModifiers Normalize(ChordModifiers modifiers, bool isMac) =>
        !isMac && modifiers.HasFlag(ChordModifiers.Ctrl) ? (modifiers & ~ChordModifiers.Ctrl) | ChordModifiers.Primary : modifiers;

    private static readonly (ChordModifiers Flag, string Name)[] Names =
        [(ChordModifiers.Primary, "Primary"), (ChordModifiers.Ctrl, "Ctrl"), (ChordModifiers.Alt, "Alt"), (ChordModifiers.Shift, "Shift")];

    private static string KeyLabel(string key, bool isMac) => key switch
    {
        "OemComma" => ",",
        "OemPeriod" => ".",
        "OemPlus" => "+",
        "OemMinus" => "-",
        "OemQuestion" => "/",
        "OemSemicolon" => ";",
        "OemQuotes" => "'",
        "OemOpenBrackets" => "[",
        "OemCloseBrackets" => "]",
        "OemPipe" => "\\",
        "OemTilde" => "`",
        "Back" => isMac ? "⌫" : "Backspace",
        "Enter" or "Return" => isMac ? "↩" : "Enter",
        "Escape" => isMac ? "⎋" : "Esc",
        "Tab" => isMac ? "⇥" : "Tab",
        "Space" => "Space",
        "D1" => "1…9",
        _ when key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1]) => key[1..],
        _ => key,
    };
}

/// <summary>A command with a keyboard shortcut, listed in Settings → Keyboard (DESIGN.md §14).</summary>
/// <param name="Id">The stable id overrides are stored under, such as <c>tabs.new</c>.</param>
/// <param name="Default">The default chord, from the section that describes the command.</param>
public sealed record ShortcutCommand(string Id, string Label, KeyChord Default);

/// <summary>The rebindable shortcuts, and the user's overrides of them.</summary>
public static class KeyboardShortcuts
{
    public const string NewTab = "tabs.new";
    public const string CloseTab = "tabs.close";
    public const string NextTab = "tabs.next";
    public const string PreviousTab = "tabs.previous";
    public const string GoToTab = "tabs.goTo";
    public const string History = "history.open";
    public const string Settings = "settings.open";
    public const string Stop = "composer.stop";
    public const string Suffixes = "composer.suffixes";
    public const string AllowPrompt = "prompt.allow";
    public const string DenyPrompt = "prompt.deny";
    public const string ToggleSidebar = "sidebar.toggle";
    public const string RunProjectAction = "project.runMain";
    public const string NextTabNeedingInput = "tabs.nextNeedingInput";
    public const string CommandPalette = "palette.open";
    public const string Find = "conversation.find";

    /// <summary>Every rebindable command, in the order Settings lists them.</summary>
    public static IReadOnlyList<ShortcutCommand> All { get; } =
    [
        new(NewTab, "New tab", Chord("Primary+T")),
        new(CloseTab, "Close tab", Chord("Primary+W")),
        new(NextTab, "Next tab", Chord("Ctrl+Tab")),
        new(PreviousTab, "Previous tab", Chord("Ctrl+Shift+Tab")),
        new(GoToTab, "Go to tab 1–9", Chord("Primary+D1")),
        new(History, "History", Chord("Primary+Shift+H")),
        new(Settings, "Settings", Chord("Primary+OemComma")),
        new(ToggleSidebar, "Collapse or expand the sidebar", Chord("Primary+B")),
        new(Stop, "Stop Claude", Chord("Escape")),
        new(Suffixes, "Quick suffixes menu", Chord("Primary+Shift+S")),
        new(AllowPrompt, "Allow the waiting prompt", Chord("Primary+Enter")),
        new(DenyPrompt, "Deny the waiting prompt", Chord("Primary+Back")),
        new(RunProjectAction, "Run the project's main action", Chord("Primary+Shift+E")),
        new(NextTabNeedingInput, "Go to the next tab waiting for you", Chord("Primary+J")),
        new(CommandPalette, "Command palette", Chord("Primary+Shift+P")),
        new(Find, "Find in the conversation", Chord("Primary+F")),
    ];

    public static ShortcutCommand Command(string id) => All.First(c => c.Id == id);

    /// <summary>The chord in effect for <paramref name="id"/>, or null when the user removed it.</summary>
    public static KeyChord? Resolve(KeyboardSettings settings, string id)
    {
        if (settings.Bindings.TryGetValue(id, out var bound))
        {
            return KeyChord.TryParse(bound, out var chord) ? chord : null;
        }
        return Command(id).Default;
    }

    /// <summary>The command or quick suffix already using <paramref name="chord"/>, other than <paramref name="exceptId"/>.</summary>
    public static string? FindConflict(AppSettings settings, KeyChord chord, string? exceptId, bool isMac)
    {
        foreach (var command in All)
        {
            if (command.Id != exceptId && Resolve(settings.Keyboard, command.Id) is { } used && Collides(used, chord, isMac))
            {
                return command.Label;
            }
        }
        foreach (var suffix in settings.QuickSuffixes)
        {
            if (suffix.Id != exceptId && KeyChord.TryParse(suffix.Shortcut, out var used) && Collides(used, chord, isMac))
            {
                return $"the quick suffix \"{suffix.Label}\"";
            }
        }
        return null;
    }

    /// <summary>Two chords collide if they're the same keys, or one is a digit range covering the other's digit.</summary>
    private static bool Collides(KeyChord a, KeyChord b, bool isMac) =>
        a.SameKeysAs(b, isMac) || DigitCollides(a, b, isMac) || DigitCollides(b, a, isMac);

    private static bool DigitCollides(KeyChord range, KeyChord other, bool isMac) =>
        range.IsDigitRange && other.Key.Length == 2 && other.Key[0] == 'D' && other.Key[1] is >= '1' and <= '9'
        && range.SameKeysAs(other with { Key = "D1" }, isMac);

    private static KeyChord Chord(string text) => KeyChord.TryParse(text, out var chord) ? chord : throw new ArgumentException(text);
}

/// <summary>Settings → Keyboard (DESIGN.md §14): shortcuts the user changed, by command id. An empty value removes one.</summary>
public sealed class KeyboardSettings
{
    public Dictionary<string, string> Bindings { get; set; } = new(StringComparer.Ordinal);
}
