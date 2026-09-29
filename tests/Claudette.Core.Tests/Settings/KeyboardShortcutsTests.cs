using Claudette.Core.Settings;

namespace Claudette.Core.Tests.Settings;

/// <summary>Rebindable keyboard shortcuts (DESIGN.md §14, "Keyboard").</summary>
public class KeyboardShortcutsTests
{
    [Theory]
    [InlineData("Primary+Shift+H", ChordModifiers.Primary | ChordModifiers.Shift, "H")]
    [InlineData("cmd+t", ChordModifiers.Primary, "t")]
    [InlineData("Ctrl+Tab", ChordModifiers.Ctrl, "Tab")]
    [InlineData("Escape", ChordModifiers.None, "Escape")]
    [InlineData("Alt + D1", ChordModifiers.Alt, "D1")]
    public void Reads_chords(string text, ChordModifiers modifiers, string key)
    {
        Assert.True(KeyChord.TryParse(text, out var chord));
        Assert.Equal(new KeyChord(modifiers, key), chord);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Hyper+T")]
    [InlineData("Ctrl+")]
    public void Rejects_bad_chords(string text) => Assert.False(KeyChord.TryParse(text, out _));

    [Fact]
    public void Writes_the_stored_form()
    {
        Assert.Equal("Primary+Alt+Shift+OemComma", new KeyChord(ChordModifiers.Shift | ChordModifiers.Primary | ChordModifiers.Alt, "OemComma").ToString());
    }

    [Theory]
    [InlineData("Primary+Shift+H", false, "Ctrl+Shift+H")]
    [InlineData("Primary+Shift+H", true, "⇧⌘H")]
    [InlineData("Primary+OemComma", false, "Ctrl+,")]
    [InlineData("Primary+Back", true, "⌘⌫")]
    [InlineData("Ctrl+Tab", true, "⌃⇥")]
    [InlineData("Primary+D1", false, "Ctrl+1…9")]
    [InlineData("Alt+D3", false, "Alt+3")]
    public void Reads_the_way_each_OS_writes_it(string chord, bool isMac, string expected)
    {
        Assert.True(KeyChord.TryParse(chord, out var parsed));
        Assert.Equal(expected, parsed.Display(isMac));
    }

    [Fact]
    public void Ctrl_and_Primary_are_one_key_except_on_macOS()
    {
        var primaryTab = new KeyChord(ChordModifiers.Primary, "Tab");
        var ctrlTab = new KeyChord(ChordModifiers.Ctrl, "Tab");

        Assert.True(primaryTab.SameKeysAs(ctrlTab, isMac: false));
        Assert.False(primaryTab.SameKeysAs(ctrlTab, isMac: true));
    }

    [Fact]
    public void Overrides_replace_or_remove_the_default()
    {
        var keyboard = new KeyboardSettings();
        Assert.Equal(new KeyChord(ChordModifiers.Primary, "T"), KeyboardShortcuts.Resolve(keyboard, KeyboardShortcuts.NewTab));

        keyboard.Bindings[KeyboardShortcuts.NewTab] = "Primary+Shift+N";
        Assert.Equal(new KeyChord(ChordModifiers.Primary | ChordModifiers.Shift, "N"), KeyboardShortcuts.Resolve(keyboard, KeyboardShortcuts.NewTab));

        keyboard.Bindings[KeyboardShortcuts.NewTab] = "";
        Assert.Null(KeyboardShortcuts.Resolve(keyboard, KeyboardShortcuts.NewTab));
    }

    [Fact]
    public void Finds_the_command_or_suffix_already_using_a_chord()
    {
        var settings = new AppSettings();
        settings.QuickSuffixes[0].Shortcut = "Primary+Alt+C";

        Assert.Equal("Close tab", KeyboardShortcuts.FindConflict(settings, new KeyChord(ChordModifiers.Primary, "W"), null, isMac: false));
        Assert.Null(KeyboardShortcuts.FindConflict(settings, new KeyChord(ChordModifiers.Primary, "W"), KeyboardShortcuts.CloseTab, isMac: false));
        Assert.Equal("the quick suffix \"Clarify first\"", KeyboardShortcuts.FindConflict(settings, new KeyChord(ChordModifiers.Primary | ChordModifiers.Alt, "C"), null, isMac: false));
        Assert.Null(KeyboardShortcuts.FindConflict(settings, new KeyChord(ChordModifiers.Primary | ChordModifiers.Alt, "X"), null, isMac: false));
    }

    [Fact]
    public void Go_to_tab_covers_every_digit()
    {
        var settings = new AppSettings();

        Assert.Equal("Go to tab 1–9", KeyboardShortcuts.FindConflict(settings, new KeyChord(ChordModifiers.Primary, "D4"), null, isMac: false));
        Assert.Null(KeyboardShortcuts.FindConflict(settings, new KeyChord(ChordModifiers.Alt, "D4"), null, isMac: false));
    }

    [Fact]
    public void Every_default_is_unique()
    {
        var settings = new AppSettings();
        foreach (var command in KeyboardShortcuts.All)
        {
            Assert.Null(KeyboardShortcuts.FindConflict(settings, command.Default, command.Id, isMac: false));
            Assert.Null(KeyboardShortcuts.FindConflict(settings, command.Default, command.Id, isMac: true));
        }
    }
}
