using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Claudette.App.ViewModels;
using Claudette.Core.Settings;
using Claudette.Platform.Shell;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.Services;

/// <summary>
/// The parts of the OS around the window (DESIGN.md §2, §4, §14). On macOS: <b>Settings…</b> in the app menu, a File
/// menu with <b>Open Recent</b>, and recent folders in the Dock icon's menu. On Windows: recent folders in the taskbar
/// jump list. Kept current as folders are opened and shortcuts rebound.
/// </summary>
public sealed class PlatformChrome : IDisposable
{
    private const int MaxRecent = 10;

    private readonly AppServices _services;
    private readonly MainWindowViewModel _main;
    private readonly IJumpList? _jumpList;
    private readonly NativeMenu? _openRecent;
    private readonly NativeMenu? _dock;
    private readonly List<(NativeMenuItem Item, string CommandId)> _gestures = [];
    private string? _lastKey;

    /// <param name="app">The application, for the macOS app and Dock menus; null in tests.</param>
    /// <param name="window">The main window, for the macOS menu bar; null in tests.</param>
    public PlatformChrome(Application? app, Window? window, MainWindowViewModel main, AppServices services, IJumpList? jumpList)
    {
        _services = services;
        _main = main;
        _jumpList = jumpList;
        if (OperatingSystem.IsMacOS() && app is not null && window is not null)
        {
            _openRecent = new NativeMenu();
            _dock = new NativeMenu();
            NativeMenu.SetMenu(app, [Item("Settings…", KeyboardShortcuts.Settings, () => _main.Shell?.OpenSettingsCommand.Execute(null))]);
            NativeMenu.SetMenu(window,
            [
                new NativeMenuItem("File")
                {
                    Menu =
                    [
                        Item("New Tab", KeyboardShortcuts.NewTab, () => _main.Shell?.NewTabCommand.Execute(null)),
                        new NativeMenuItem("Open Recent") { Menu = _openRecent },
                        Item("History…", KeyboardShortcuts.History, () => _main.Shell?.OpenHistoryCommand.Execute(null)),
                        new NativeMenuItemSeparator(),
                        Item("Close Tab", KeyboardShortcuts.CloseTab, () => _main.Shell?.CloseSelectedTabCommand.Execute(null)),
                    ],
                },
            ]);
            NativeDock.SetMenu(app, _dock);
        }
        services.StateChanged += OnStateChanged;
        services.SettingsChanged += OnSettingsChanged;
        Refresh();
    }

    /// <summary>The folders shown now, for tests.</summary>
    public IReadOnlyList<RecentFolderEntry> Folders { get; private set; } = [];

    private NativeMenuItem Item(string header, string commandId, Action action)
    {
        var item = new NativeMenuItem(header) { Command = new RelayCommand(action), Gesture = Gesture(commandId) };
        _gestures.Add((item, commandId));
        return item;
    }

    private void OnStateChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>Rebound shortcuts show in the menus too.</summary>
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        foreach (var (item, commandId) in _gestures)
        {
            item.Gesture = Gesture(commandId);
        }
    }

    /// <summary>Rebuilds the lists when the recent or favorite folders changed. Checking which exist only happens then.</summary>
    public void Refresh()
    {
        var state = _services.State;
        var key = string.Join('\n', state.FavoriteFolders.Concat(state.RecentFolders.Select(r => r.Path)));
        if (key == _lastKey)
        {
            return;
        }
        _lastKey = key;
        Folders = FolderHistory.Shortlist(state, MaxRecent, Directory.Exists).Select(f => new RecentFolderEntry(f.Label, f.Path)).ToArray();
        foreach (var menu in new[] { _openRecent, _dock }.OfType<NativeMenu>())
        {
            menu.Items.Clear();
            foreach (var folder in Folders)
            {
                menu.Items.Add(new NativeMenuItem(folder.Label)
                {
                    ToolTip = folder.Path,
                    Command = new RelayCommand(() => _main.OnLaunchedAgain(["--folder", folder.Path])),
                });
            }
        }
        _jumpList?.Update(Folders);
    }

    /// <summary>The menu's form of a shortcut: Primary is Cmd on macOS.</summary>
    private KeyGesture? Gesture(string commandId)
    {
        if (KeyboardShortcuts.Resolve(_services.Settings.Keyboard, commandId) is not { IsDigitRange: false } chord
            || !Enum.TryParse<Key>(chord.Key, ignoreCase: true, out var key))
        {
            return null;
        }
        var modifiers = KeyModifiers.None;
        if (chord.Modifiers.HasFlag(ChordModifiers.Primary))
        {
            modifiers |= Shortcuts.IsMac ? KeyModifiers.Meta : KeyModifiers.Control;
        }
        if (chord.Modifiers.HasFlag(ChordModifiers.Ctrl))
        {
            modifiers |= KeyModifiers.Control;
        }
        if (chord.Modifiers.HasFlag(ChordModifiers.Alt))
        {
            modifiers |= KeyModifiers.Alt;
        }
        if (chord.Modifiers.HasFlag(ChordModifiers.Shift))
        {
            modifiers |= KeyModifiers.Shift;
        }
        return new KeyGesture(key, modifiers);
    }

    public void Dispose()
    {
        _services.StateChanged -= OnStateChanged;
        _services.SettingsChanged -= OnSettingsChanged;
    }
}
