using System.Collections.ObjectModel;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels.Settings;

/// <summary>One command in Settings → Keyboard (DESIGN.md §14).</summary>
public sealed class ShortcutRow(ShortcutCommand command, KeyboardSettings settings) : ShortcutEditor
{
    public ShortcutCommand Command { get; } = command;

    public string Label => Command.Label;

    public override KeyChord? Shortcut => KeyboardShortcuts.Resolve(settings, Command.Id);

    /// <summary>Changed from the default, so <b>Reset</b> applies.</summary>
    public bool IsCustomized => settings.Bindings.ContainsKey(Command.Id);

    protected override void OnRefresh() => OnPropertyChanged(nameof(IsCustomized));
}

/// <summary>
/// Settings → Keyboard (DESIGN.md §14, "Keyboard shortcuts"): every command's shortcut, each rebindable. Click one and
/// press the new keys; the window passes them to the <see cref="ShortcutRecorder"/>.
/// </summary>
public sealed partial class KeyboardPage : SettingsPage
{
    public KeyboardPage(SettingsContext context) : base(context, SettingsCategory.Keyboard)
    {
        LoadRows();
        context.Shortcuts.Saved += OnShortcutsSaved;
    }

    public override IEnumerable<SettingsSearchResult> SearchEntries =>
        [Entry("Send with", pageText: "Send with"), .. KeyboardShortcuts.All.Select(c => Entry($"{c.Label} shortcut", pageText: c.Label))];

    // ---- Send with (DESIGN.md §14, "Keyboard shortcuts") ----------------------------------------------------------

    /// <summary>Enter sends, Shift+Enter starts a new line.</summary>
    public bool SendWithEnter
    {
        get => Settings.Keyboard.SendKey == SendKey.Enter;
        set
        {
            if (value)
            {
                SetSendKey(SendKey.Enter);
            }
        }
    }

    /// <summary>Ctrl/Cmd+Enter sends, Enter starts a new line.</summary>
    public bool SendWithPrimaryEnter
    {
        get => Settings.Keyboard.SendKey == SendKey.PrimaryEnter;
        set
        {
            if (value)
            {
                SetSendKey(SendKey.PrimaryEnter);
            }
        }
    }

    public string PrimaryEnterLabel => $"{new KeyChord(ChordModifiers.Primary, "Enter").Display(Claudette.App.Services.Shortcuts.IsMac)}, with Enter for a new line";

    /// <summary>The keys that can't be rebound, listed under the shortcuts.</summary>
    public string FixedKeysText =>
        $"In the message box, {new KeyChord(ChordModifiers.Primary | ChordModifiers.Shift, "V").Display(Claudette.App.Services.Shortcuts.IsMac)} pastes text as it is, however long. "
        + "In the new tab picker, 1–9 pick a folder; in the quick suffixes menu, 1–9 pick a suffix.";

    private void SetSendKey(SendKey key)
    {
        if (Settings.Keyboard.SendKey == key)
        {
            return;
        }
        Settings.Keyboard.SendKey = key;
        OnPropertyChanged(nameof(SendWithEnter));
        OnPropertyChanged(nameof(SendWithPrimaryEnter));
        Save();
    }

    public ObservableCollection<ShortcutRow> ShortcutRows { get; } = [];

    private void LoadRows()
    {
        ShortcutRows.Clear();
        foreach (var command in KeyboardShortcuts.All)
        {
            ShortcutRows.Add(new ShortcutRow(command, Settings.Keyboard));
        }
    }

    [RelayCommand]
    private void StartRecording(ShortcutEditor? editor) => Context.Shortcuts.Start(editor);

    [RelayCommand]
    private void ResetShortcut(ShortcutRow? row)
    {
        if (row is not null && Settings.Keyboard.Bindings.Remove(row.Command.Id))
        {
            Context.Shortcuts.Save();
        }
    }

    /// <summary>Removes a shortcut: the command is then only in menus and buttons.</summary>
    [RelayCommand]
    private void ClearShortcut(ShortcutEditor? editor) => Context.Shortcuts.Clear(editor);

    private void OnShortcutsSaved()
    {
        foreach (var row in ShortcutRows)
        {
            row.Refresh();
        }
    }

    protected override void ResetSettings()
    {
        Context.Shortcuts.Cancel();
        Settings.Keyboard = new KeyboardSettings();
        LoadRows();
        Context.Shortcuts.Save();
    }
}
