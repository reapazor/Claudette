using System.Collections.ObjectModel;
using Claudette.App.Services;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// The main UI once Claude Code is ready: the tab strip grouped by folder, the selected tab, the new-tab picker and
/// dialogs (DESIGN.md §3, §4).
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly AppServices _services;
    private readonly Action _onAuthenticationRequired;

    public ShellViewModel(AppServices services, Action onAuthenticationRequired)
    {
        _services = services;
        _onAuthenticationRequired = onAuthenticationRequired;
        _services.SettingsChanged += (_, _) =>
        {
            foreach (var tab in AllTabs)
            {
                tab.OnSettingsChanged();
            }
        };
    }

    public ObservableCollection<TabGroupViewModel> Groups { get; } = [];

    public IEnumerable<TabViewModel> AllTabs => Groups.SelectMany(g => g.Tabs);

    /// <summary>
    /// Every open tab. Each gets its own view, kept alive while hidden, so switching tabs keeps scroll position and
    /// half-typed messages.
    /// </summary>
    public ObservableCollection<TabViewModel> OpenTabs { get; } = [];

    public bool HasTabs => Groups.Count > 0;

    [ObservableProperty]
    public partial TabViewModel? SelectedTab { get; set; }

    partial void OnSelectedTabChanged(TabViewModel? oldValue, TabViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }
        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }
        _services.State.SelectedTabId = newValue?.Id;
        _services.SaveState();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPickerOpen))]
    public partial NewTabPickerViewModel? Picker { get; set; }

    public bool IsPickerOpen => Picker is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConfirmation))]
    public partial ConfirmationViewModel? Confirmation { get; set; }

    public bool HasConfirmation => Confirmation is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTabSettings))]
    public partial TabSettingsViewModel? TabSettings { get; set; }

    public bool HasTabSettings => TabSettings is not null;

    /// <summary>Set by the view: opens the Settings window.</summary>
    public Func<Task>? ShowSettingsWindow { get; set; }

    /// <summary>
    /// Brings back saved tabs (DESIGN.md §9, "Restore on launch"): pinned tabs always, the others only when
    /// <see cref="SessionSettings.RestoreUnpinnedTabs"/> is on. Restored tabs start their process when selected.
    /// </summary>
    public void Restore(string? initialFolder)
    {
        var state = _services.State;
        var restoreUnpinned = _services.Settings.Sessions.RestoreUnpinnedTabs;
        state.Tabs = state.Tabs.Where(t => t.IsPinned || restoreUnpinned).ToList();
        foreach (var tabState in state.Tabs)
        {
            AddTab(new TabViewModel(_services, this, tabState, isRestored: true));
        }
        SelectedTab = AllTabs.FirstOrDefault(t => t.Id == state.SelectedTabId) ?? AllTabs.FirstOrDefault();
        SaveTabs();
        if (initialFolder is not null)
        {
            _ = OpenFolderAsync(initialFolder);
        }
        else if (!HasTabs)
        {
            NewTab();
        }
    }

    // ---- Opening tabs -----------------------------------------------------------------------------------

    [RelayCommand]
    private void NewTab() => Picker = new NewTabPickerViewModel(_services, this);

    public void ClosePicker() => Picker = null;

    /// <summary>Opens a new tab in <paramref name="folder"/> and selects it, which starts its session.</summary>
    public Task OpenFolderAsync(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return Task.CompletedTask;
        }
        var normalized = FolderHistory.Normalize(folder);
        FolderHistory.Touch(_services.State, normalized, _services.Time.GetUtcNow(), _services.Settings.NewTabs.RecentFolderLimit);
        var tab = new TabViewModel(_services, this, new TabState { Folder = normalized }, isRestored: false);
        AddTab(tab);
        SelectedTab = tab;
        SaveTabs();
        return tab.EnsureStartedAsync();
    }

    [RelayCommand]
    private Task NewTabInGroupAsync(TabGroupViewModel? group) =>
        group is null ? Task.CompletedTask : OpenFolderAsync(group.Folder);

    private void AddTab(TabViewModel tab)
    {
        var group = Groups.FirstOrDefault(g => FolderHistory.SamePath(g.Folder, tab.Folder));
        if (group is null)
        {
            var state = _services.State;
            if (!state.GroupColors.TryGetValue(tab.Folder, out var color))
            {
                var used = Groups.Select(g => g.ColorIndex).ToHashSet();
                color = Enumerable.Range(0, TabGroupViewModel.Palette.Count).FirstOrDefault(i => !used.Contains(i), Groups.Count % TabGroupViewModel.Palette.Count);
                state.GroupColors[tab.Folder] = color;
            }
            group = new TabGroupViewModel(tab.Folder, color, state.CollapsedGroups.Any(c => FolderHistory.SamePath(c, tab.Folder)));
            Groups.Add(group);
            UpdateGroupLabels();
            OnPropertyChanged(nameof(HasTabs));
        }
        // Pinned tabs sit at the start of their group.
        var index = tab.IsPinned ? group.Tabs.Count(t => t.IsPinned) : group.Tabs.Count;
        group.Tabs.Insert(index, tab);
        OpenTabs.Add(tab);
    }

    // ---- Closing tabs -----------------------------------------------------------------------------------

    [RelayCommand]
    private void CloseTab(TabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }
        var reasons = new List<string>();
        if (tab.IsPinned)
        {
            reasons.Add("This tab is pinned; closing it also unpins it.");
        }
        if (tab.IsWorking && _services.Settings.General.ConfirmCloseWorkingTab)
        {
            reasons.Add("Claude is still working in it and will be stopped.");
        }
        if (reasons.Count == 0)
        {
            _ = RemoveTabAsync(tab);
            return;
        }
        Confirmation = new ConfirmationViewModel($"Close \"{tab.DisplayName}\"?", string.Join(" ", reasons), "Close", () => RemoveTabAsync(tab), () => Confirmation = null);
    }

    [RelayCommand]
    private async Task CloseOtherTabsAsync(TabViewModel? keep)
    {
        foreach (var tab in AllTabs.Where(t => t != keep && !t.IsPinned).ToArray())
        {
            await RemoveTabAsync(tab);
        }
    }

    [RelayCommand]
    private async Task CloseGroupAsync(TabGroupViewModel? group)
    {
        foreach (var tab in group?.Tabs.Where(t => !t.IsPinned).ToArray() ?? [])
        {
            await RemoveTabAsync(tab);
        }
    }

    [RelayCommand]
    private void CloseSelectedTab() => CloseTab(SelectedTab);

    private async Task RemoveTabAsync(TabViewModel tab)
    {
        var ordered = AllTabs.ToList();
        var index = ordered.IndexOf(tab);
        var group = Groups.FirstOrDefault(g => g.Tabs.Contains(tab));
        group?.Tabs.Remove(tab);
        OpenTabs.Remove(tab);
        if (group is { Tabs.Count: 0 })
        {
            Groups.Remove(group);
            UpdateGroupLabels();
            OnPropertyChanged(nameof(HasTabs));
        }
        if (SelectedTab == tab)
        {
            var remaining = AllTabs.ToList();
            SelectedTab = remaining.Count == 0 ? null : remaining[Math.Clamp(index, 0, remaining.Count - 1)];
        }
        SaveTabs();
        await tab.DisposeAsync();
    }

    // ---- Selection and groups -------------------------------------------------------------------------------

    [RelayCommand]
    private void Select(TabViewModel? tab)
    {
        if (tab is not null)
        {
            SelectedTab = tab;
        }
    }

    [RelayCommand]
    private void SelectNext() => SelectOffset(1);

    [RelayCommand]
    private void SelectPrevious() => SelectOffset(-1);

    /// <summary>Ctrl/Cmd+1…9. Tabs in collapsed groups are still counted.</summary>
    public void SelectNumber(int number)
    {
        var tabs = AllTabs.ToList();
        if (number >= 1 && number <= tabs.Count)
        {
            SelectedTab = tabs[number - 1];
        }
    }

    private void SelectOffset(int offset)
    {
        var tabs = AllTabs.ToList();
        if (tabs.Count == 0)
        {
            return;
        }
        var index = SelectedTab is null ? 0 : tabs.IndexOf(SelectedTab);
        SelectedTab = tabs[(index + offset + tabs.Count) % tabs.Count];
    }

    [RelayCommand]
    private void ToggleGroupCollapsed(TabGroupViewModel? group)
    {
        if (group is null)
        {
            return;
        }
        group.IsCollapsed = !group.IsCollapsed;
        var collapsed = _services.State.CollapsedGroups;
        collapsed.RemoveAll(c => FolderHistory.SamePath(c, group.Folder));
        if (group.IsCollapsed)
        {
            collapsed.Add(group.Folder);
        }
        _services.SaveState();
    }

    [RelayCommand]
    private void CycleGroupColor(TabGroupViewModel? group)
    {
        if (group is null)
        {
            return;
        }
        group.ColorIndex = (group.ColorIndex + 1) % TabGroupViewModel.Palette.Count;
        _services.State.GroupColors[group.Folder] = group.ColorIndex;
        _services.SaveState();
    }

    [RelayCommand]
    private void MoveTabLeft(TabViewModel? tab) => MoveTab(tab, -1);

    [RelayCommand]
    private void MoveTabRight(TabViewModel? tab) => MoveTab(tab, 1);

    /// <summary>Moves a tab within its group, keeping pinned tabs first.</summary>
    private void MoveTab(TabViewModel? tab, int offset)
    {
        var group = Groups.FirstOrDefault(g => tab is not null && g.Tabs.Contains(tab));
        if (group is null || tab is null)
        {
            return;
        }
        var from = group.Tabs.IndexOf(tab);
        var to = from + offset;
        if (to < 0 || to >= group.Tabs.Count || group.Tabs[to].IsPinned != tab.IsPinned)
        {
            return;
        }
        group.Tabs.Move(from, to);
        SaveTabs();
    }

    /// <summary>Called when a tab is pinned or unpinned: pinned tabs move to the start of their group.</summary>
    internal void OnPinChanged(TabViewModel tab)
    {
        if (Groups.FirstOrDefault(g => g.Tabs.Contains(tab)) is { } group)
        {
            group.Tabs.Remove(tab);
            var index = tab.IsPinned ? group.Tabs.Count(t => t.IsPinned) : group.Tabs.Count(t => t.IsPinned);
            group.Tabs.Insert(index, tab);
            if (SelectedTab != tab)
            {
                SelectedTab = tab;
            }
        }
        SaveTabs();
    }

    // ---- Settings -------------------------------------------------------------------------------------------

    [RelayCommand]
    private Task OpenSettingsAsync() => ShowSettingsWindow?.Invoke() ?? Task.CompletedTask;

    [RelayCommand]
    private void OpenTabSettings(TabViewModel? tab)
    {
        if (tab is not null)
        {
            TabSettings = new TabSettingsViewModel(_services, tab, () => TabSettings = null);
        }
    }

    // ---- Sign-in ----------------------------------------------------------------------------------------------

    internal void OnAuthenticationRequired() => _onAuthenticationRequired();

    public async Task OnSignedInAgainAsync()
    {
        foreach (var tab in AllTabs.ToArray())
        {
            await tab.OnSignedInAgainAsync();
        }
    }

    // ---- Persistence ------------------------------------------------------------------------------------------

    private void SaveTabs()
    {
        _services.State.Tabs = AllTabs.Select(t => t.State).ToList();
        _services.SaveState();
    }

    /// <summary>Gives groups with the same folder name a distinguishing parent, such as <c>work/api</c>.</summary>
    private void UpdateGroupLabels()
    {
        foreach (var group in Groups)
        {
            var clash = Groups.Any(g => g != group && string.Equals(g.FolderName, group.FolderName, StringComparison.OrdinalIgnoreCase));
            var parent = Path.GetFileName(Path.GetDirectoryName(group.Folder) ?? "");
            group.Label = clash && parent.Length > 0 ? $"{parent}/{group.FolderName}" : group.FolderName;
        }
    }

    public async ValueTask DisposeAsync()
    {
        SaveTabs();
        await Task.WhenAll(AllTabs.ToArray().Select(t => t.DisposeAsync().AsTask()));
    }
}
