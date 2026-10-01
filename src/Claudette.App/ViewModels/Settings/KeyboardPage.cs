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
        KeyboardShortcuts.All.Select(c => Entry($"{c.Label} shortcut", pageText: c.Label));

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
