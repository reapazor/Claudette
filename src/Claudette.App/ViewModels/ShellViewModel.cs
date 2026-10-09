using System.Collections.ObjectModel;
using Avalonia.Media;
using Claudette.App.Services;
using Claudette.Core.Accessibility;
using Claudette.Core.Development;
using Claudette.Core.Git;
using Claudette.Core.Protocol;
using Claudette.Core.Settings;
using Claudette.Platform.Processes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// The main UI once Claude Code is ready: the sidebar of tabs grouped by folder, the selected tab, the new-tab picker
/// and dialogs (DESIGN.md §3, §4). Opening a session from History is <see cref="SessionOpener"/>'s, the sidebar's and
/// side panel's widths are <see cref="Layout"/>'s, and a group's color is chosen by <see cref="GroupColors"/>.
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly AppServices _services;
    private readonly Action _onAuthenticationRequired;
    private readonly SessionOpener _opener;

    public ShellViewModel(AppServices services, Action onAuthenticationRequired)
    {
        _services = services;
        _onAuthenticationRequired = onAuthenticationRequired;
        _opener = new SessionOpener(services, this);
        Layout = new ShellLayout(services.State, services.SaveState, () =>
        {
            foreach (var tab in AllTabs)
            {
                tab.OnSidePanelWidthChanged();
            }
        });
        _services.Notifications.SelectedTabId = () => SelectedTab?.Id;
        // The worktrees open tabs work in are never cleaned up (DESIGN.md §4, "Cleaning up worktrees").
        _services.Worktrees.OpenTabs = () => [.. AllTabs.Select(t => t.State)];
        IsCompact = services.Settings.Appearance.Density == Density.Compact;
        IsClaudeStyle = services.Settings.Appearance.Style == AppStyle.Claude;
        IsFullWidth = services.Settings.Appearance.FullWidthConversation;
        _services.SettingsChanged += (_, _) =>
        {
            IsCompact = _services.Settings.Appearance.Density == Density.Compact;
            IsClaudeStyle = _services.Settings.Appearance.Style == AppStyle.Claude;
            IsFullWidth = _services.Settings.Appearance.FullWidthConversation;
            foreach (var tab in AllTabs)
            {
                tab.OnSettingsChanged();
            }
        };
        _services.Library.LeaseLost += (sessionId, machine) =>
        {
            foreach (var tab in AllTabs.Where(t => t.State.SessionId == sessionId))
            {
                _ = tab.OnTakenOverAsync(machine);
            }
        };
        if (_services.ClaudeUpdates is { } updates)
        {
            updates.Changed += () =>
            {
                foreach (var tab in AllTabs)
                {
                    tab.OnClaudeVersionsChanged();
                }
            };
        }
        // Signing in with another account can make Remote Control available, or not (DESIGN.md §18).
        _services.RemoteControl.AvailabilityChanged += () =>
        {
            foreach (var tab in AllTabs)
            {
                tab.RemoteControl.OnAvailabilityChanged();
            }
        };
        _services.UsageHistoryCleared += (_, resetTabTotals) =>
        {
            if (resetTabTotals)
            {
                foreach (var tab in AllTabs)
                {
                    tab.Context.ResetTokenTotals();
                }
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

    /// <summary>Settings → Keyboard, for the window's shortcuts.</summary>
    public KeyboardSettings Keyboard => _services.Settings.Keyboard;

    /// <summary>Quick suffixes, some of which have their own shortcuts (DESIGN.md §5).</summary>
    public IReadOnlyList<QuickSuffix> QuickSuffixes => _services.Settings.QuickSuffixes;

    public ShortcutTips Tips => _services.Tips;

    /// <summary>The Claude Code update badge at the foot of the sidebar (DESIGN.md §12); null until update checks start.</summary>
    [ObservableProperty]
    public partial ClaudeUpdateViewModel? Updates { get; set; }

    /// <summary>The Claude Code version each running tab uses (DESIGN.md §12).</summary>
    public IReadOnlyCollection<Version> RunningVersions => AllTabs.Select(t => t.RunningVersion).OfType<Version>().ToArray();

    /// <summary>Raised when a tab starts or stops its process, or learns its Claude Code version.</summary>
    public event Action? RunningVersionsChanged;

    internal void OnRunningVersionsChanged() => RunningVersionsChanged?.Invoke();

    /// <summary>
    /// A tab's status changed, or it started or stopped waiting for a usage limit to reset: the Dock/taskbar badge
    /// counts tabs needing input, and the icon animates while tabs wait or work (DESIGN.md §10).
    /// </summary>
    internal void OnTabStatusChanged()
    {
        _services.Notifications.SetTabActivity(AllTabs.Count(t => t.NeedsInput), AllTabs.Count(t => t.IsWaitingForLimitReset),
            AllTabs.Count(t => t.Status == TabStatus.Working));
        // A closed tab's processes leave the header's total.
        OnTabProcessesSampled();
        TabStatusChanged?.Invoke();
    }

    // ---- Every tab's processes, in the header (DESIGN.md §4, "Process monitor") -------------------------------------

    /// <summary>
    /// The CPU and memory of every tab's processes, <c>claude</c> included, such as <c>42% CPU · 1.1 GB</c>. Null while
    /// no tab has the process monitor on and a sample.
    /// </summary>
    [ObservableProperty]
    public partial string? ProcessTotalsText { get; private set; }

    /// <summary>Each tab's part of the total.</summary>
    [ObservableProperty]
    public partial string? ProcessTotalsTip { get; private set; }

    /// <summary>A tab sampled its processes, or stopped sampling: adds up the latest samples again.</summary>
    internal void OnTabProcessesSampled()
    {
        var all = AllTabs.ToArray();
        var sampled = all.Where(t => t.ProcessMonitor.LatestSummary is not null).ToArray();
        if (sampled.Length == 0)
        {
            ProcessTotalsText = null;
            ProcessTotalsTip = null;
            return;
        }
        ProcessTotalsText = ProcessSummary.Sum(sampled.Select(t => t.ProcessMonitor.LatestSummary!)).UsageText;
        var others = all.Length - sampled.Length;
        ProcessTotalsTip = string.Join("\n",
        [
            "Processes of every tab, Claude Code included",
            .. sampled.Select(t => $"{t.DisplayName}: {t.ProcessMonitor.LatestSummary}"),
            .. others == 0 ? Array.Empty<string>()
                : [$"Not counted: {(others == 1 ? "1 tab" : $"{others} tabs")} not started, or with the process monitor off"],
        ]);
    }

    /// <summary>Raised when a tab's status changes, for restarting into a new build once no tab is working.</summary>
    public event Action? TabStatusChanged;

    /// <summary>Selects a tab by id, for a clicked notification. False when it has closed since.</summary>
    public bool SelectTab(string tabId)
    {
        if (AllTabs.FirstOrDefault(t => t.Id == tabId) is not { } tab)
        {
            return false;
        }
        if (Groups.FirstOrDefault(g => g.Tabs.Contains(tab)) is { IsCollapsed: true } group)
        {
            ToggleGroupCollapsed(group);
        }
        SelectedTab = tab;
        return true;
    }

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
            if (_services.Notifications.IsAppActive)
            {
                // Looked at now: its notifications are no longer news (DESIGN.md §10).
                _services.Notifications.ClearTab(newValue.Id);
            }
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

    /// <summary>Set by the view: opens the Settings window where <see cref="SettingsOpening"/> says, and returns when it closes.</summary>
    public Func<SettingsOpening, Task>? ShowSettingsWindow { get; set; }

    /// <summary>
    /// Brings back saved tabs (DESIGN.md §9, "Restore on launch"): pinned tabs always, the others only when
    /// <see cref="SessionSettings.RestoreUnpinnedTabs"/> is on. Restored tabs start their process when selected.
    /// </summary>
    /// <param name="snapshot">
    /// After a restart into a new build (DESIGN.md §9, "Working on Claudette"): every tab the build had open, with what
    /// was typed in each, and the tabs that were running start again straight away.
    /// </param>
    public void Restore(string? initialFolder, RestartSnapshot? snapshot = null)
    {
        var state = _services.State;
        var restoreUnpinned = _services.Settings.Sessions.RestoreUnpinnedTabs;
        state.Tabs = snapshot?.Tabs ?? state.Tabs.Where(t => t.IsPinned || restoreUnpinned).ToList();
        foreach (var tabState in state.Tabs)
        {
            AddTab(new TabViewModel(_services, this, tabState, isRestored: true));
        }
        // Each sub-thread joins its thread again (DESIGN.md §18, "Threads").
        LinkThreads();
        foreach (var tab in AllTabs)
        {
            if (snapshot?.Drafts.GetValueOrDefault(tab.Id) is { } draft)
            {
                tab.RestoreDraft(draft);
            }
            else
            {
                // What was typed when Claudette last quit (DESIGN.md §5, "Drafts and the stash").
                tab.RestoreSavedDraft();
            }
            // A deleted clone, say: shown straight away, not only once the tab is selected (DESIGN.md §9, "Missing folder").
            if (!Directory.Exists(tab.Folder))
            {
                tab.MarkFolderMissing();
            }
        }
        SelectedTab = AllTabs.FirstOrDefault(t => t.Id == (snapshot?.SelectedTabId ?? state.SelectedTabId)) ?? AllTabs.FirstOrDefault();
        SaveTabs();
        foreach (var tab in AllTabs.Where(t => snapshot?.RunningTabIds.Contains(t.Id) == true))
        {
            if (snapshot!.InterruptedTabIds.Contains(tab.Id))
            {
                tab.CarryOnInterruptedTurn();
            }
            _ = tab.EnsureStartedAsync();
        }
        // Library retention and settings sync, at launch (DESIGN.md §9, §14). Retention keeps the sessions of open tabs
        // that sync; a tab that doesn't sync has no part in the library.
        var keep = state.Tabs.Where(t => t.SyncToLibrary).Select(t => t.SessionId).OfType<string>().ToHashSet();
        _ = Task.Run(() => _services.Library.Prune(keep));
        // The drafts of tabs that weren't restored won't be needed.
        _ = _services.Drafts.KeepOnlyAsync(state.Tabs.Select(t => t.Id).ToHashSet(StringComparer.Ordinal));
        // Worktrees no tab needs any more go, as Settings → General says (DESIGN.md §4, "Cleaning up worktrees").
        _services.Worktrees.Start();
        _ = _services.Library.SyncSettingsAsync();
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

    /// <summary>
    /// Opens a new tab in <paramref name="folder"/> and selects it, which starts its session. Every way to a new tab
    /// comes here (the picker, a group's <c>+</c>, <c>--folder</c>, Open Recent, the jump list, a dropped folder), so
    /// each starts syncing or not as Settings → Sessions says (DESIGN.md §9, "Session library"), and connected to the
    /// Claude app or not as Settings → Claude Code says (DESIGN.md §18, "Remote Control").
    /// </summary>
    /// <param name="opened">Runs on the new tab before it's selected and started, such as making it a sub-thread.</param>
    public Task OpenFolderAsync(string folder, Action<TabViewModel>? opened = null)
    {
        if (!Directory.Exists(folder))
        {
            return Task.CompletedTask;
        }
        var normalized = FolderHistory.Normalize(folder);
        FolderHistory.Touch(_services.State, normalized, _services.Time.GetUtcNow(), _services.Settings.NewTabs.RecentFolderLimit);
        var state = new TabState
        {
            Folder = normalized,
            SyncToLibrary = _services.Settings.Sessions.SyncNewTabs,
            RemoteControl = _services.Settings.ClaudeCode.ConnectNewTabsToClaudeApp,
        };
        var tab = new TabViewModel(_services, this, state, isRestored: false);
        AddTab(tab);
        opened?.Invoke(tab);
        SelectedTab = tab;
        SaveTabs();
        return tab.EnsureStartedAsync();
    }

    [RelayCommand]
    private Task NewTabInGroupAsync(TabGroupViewModel? group) =>
        group is null ? Task.CompletedTask : OpenFolderAsync(group.Folder);

    /// <summary>
    /// A tab's folder moved (<b>Choose folder…</b> for a missing folder, DESIGN.md §9): the tab joins that folder's
    /// group, and the folder becomes a recent one.
    /// </summary>
    internal void MoveTabToFolder(TabViewModel tab, string folder)
    {
        var normalized = FolderHistory.Normalize(folder);
        var selected = SelectedTab;
        if (Groups.FirstOrDefault(g => g.Tabs.Contains(tab)) is { } old)
        {
            old.Tabs.Remove(tab);
            if (old.Tabs.Count == 0)
            {
                Groups.Remove(old);
            }
        }
        OpenTabs.Remove(tab);
        tab.State.Folder = normalized;
        // Still a worktree only if the new folder is one Claude Code made (DESIGN.md §4, "Worktree tabs").
        tab.State.WorktreeOf = GitWorktrees.MainCheckoutOf(normalized);
        tab.State.NewWorktree = null;
        AddTab(tab);
        UpdateGroupLabels();
        OnPropertyChanged(nameof(HasTabs));
        SelectedTab = selected;
        FolderHistory.Touch(_services.State, normalized, _services.Time.GetUtcNow(), _services.Settings.NewTabs.RecentFolderLimit);
        SaveTabs();
    }

    private void AddTab(TabViewModel tab)
    {
        // A tab in a worktree is in its main checkout's group (DESIGN.md §4, "Worktree tabs").
        var folder = tab.GroupFolder;
        var group = Groups.FirstOrDefault(g => FolderHistory.SamePath(g.Folder, folder));
        if (group is null)
        {
            var state = _services.State;
            var color = GroupColors.ForNewGroup(state, folder, Groups.Select(g => g.Color).ToArray());
            group = new TabGroupViewModel(folder, color, state.CollapsedGroups.Any(c => FolderHistory.SamePath(c, folder)));
            Groups.Add(group);
            UpdateGroupLabels();
            OnPropertyChanged(nameof(HasTabs));
        }
        // Pinned tabs sit at the start of their group.
        var index = tab.IsPinned ? group.Tabs.Count(t => t.IsPinned) : group.Tabs.Count;
        group.Tabs.Insert(index, tab);
        OpenTabs.Add(tab);
    }

    // ---- History (DESIGN.md §9) -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHistoryOpen))]
    public partial HistoryViewModel? History { get; set; }

    public bool IsHistoryOpen => History is not null;

    /// <summary>Ctrl/Cmd+Shift+H, or "Open from History…" in the new-tab picker.</summary>
    [RelayCommand]
    private void OpenHistory()
    {
        Picker = null;
        History = new HistoryViewModel(_services, this);
    }

    public void CloseHistory() => History = null;

    /// <summary>Resumes a past session in a new tab, with its earlier conversation loaded (<see cref="SessionOpener"/>).</summary>
    public Task OpenFromHistoryAsync(HistoryEntry entry) => _opener.OpenAsync(entry);

    /// <summary>
    /// <b>Duplicate tab</b> (DESIGN.md §5, "Rewind and branch"): a new tab in its group carrying its session on as a copy,
    /// or a new session in the same folder when it hasn't one yet.
    /// </summary>
    [RelayCommand]
    private Task DuplicateTabAsync(TabViewModel? tab) =>
        tab is null ? Task.CompletedTask : OpenCopyAsync(tab.CopyState(CopyPoint.Whole), text: null, images: []);

    /// <summary>Opens a tab copying another (<see cref="TabViewModel.CopyState"/>), with a message in its composer.</summary>
    internal Task OpenCopyAsync(TabState state, string? text, IReadOnlyList<MessageImage> images)
    {
        var tab = new TabViewModel(_services, this, state, isRestored: state.SessionId is not null);
        AddTab(tab);
        SelectedTab = tab;
        SaveTabs();
        if (text is not null)
        {
            tab.PutInComposer(text, images);
        }
        return tab.EnsureStartedAsync();
    }

    // ---- Closing tabs -----------------------------------------------------------------------------------

    /// <summary>Several tabs can be closing at once: each waits up to a few seconds for its claude to finish.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task CloseTabAsync(TabViewModel? tab)
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
        // Processes the tab started, such as dev servers, are stopped with it unless the user keeps them (DESIGN.md §4).
        // Listing them scans every process (ps on macOS): not on the UI thread.
        var running = await Task.Run(tab.ProcessMonitor.RunningChildProcesses);
        if (running.Count > 0)
        {
            var names = string.Join(", ", running.Take(5).Select(p => $"{p.Name} ({p.Pid})")) + (running.Count > 5 ? $" and {running.Count - 5} more" : "");
            reasons.Add($"It started {(running.Count == 1 ? "a process that is" : $"{running.Count} processes that are")} still running: {names}.");
            Confirmation = new ConfirmationViewModel($"Close \"{tab.DisplayName}\"?", string.Join(" ", reasons),
                running.Count == 1 ? "Close and stop it" : "Close and stop them", () => CloseAndTidyAsync(tab), () => Confirmation = null,
                "Close, leave running", () => CloseAndTidyAsync(tab, killProcesses: false));
            return;
        }
        if (reasons.Count == 0)
        {
            await CloseAndTidyAsync(tab);
            return;
        }
        Confirmation = new ConfirmationViewModel($"Close \"{tab.DisplayName}\"?", string.Join(" ", reasons), "Close", () => CloseAndTidyAsync(tab), () => Confirmation = null);
    }

    /// <summary>Closes the tab, then offers to remove the worktree it worked in, if no other tab does (DESIGN.md §4).</summary>
    private async Task CloseAndTidyAsync(TabViewModel tab, bool killProcesses = true)
    {
        await RemoveTabAsync(tab, killProcesses);
        // What the user chose to leave running may still be working in the tab's worktree.
        if (killProcesses)
        {
            await OfferToRemoveWorktreesAsync([tab.State]);
        }
    }

    /// <summary>Closes several tabs side by side, then offers to remove the worktrees they leave unused.</summary>
    private async Task CloseAllAndTidyAsync(IReadOnlyList<TabViewModel> tabs)
    {
        await Task.WhenAll(tabs.Select(tab => RemoveTabAsync(tab)));
        await OfferToRemoveWorktreesAsync([.. tabs.Select(t => t.State)]);
    }

    /// <summary>A tab's turn reached the usage history: that tab's "this session window" tokens follow.</summary>
    internal void OnTurnRecorded(string tabId) => AllTabs.FirstOrDefault(t => t.Id == tabId)?.Context.RefreshTokenWindow();

    /// <summary>A plain confirmation over the whole window.</summary>
    internal void Confirm(string title, string message, string confirmText, Func<Task> onConfirm) =>
        Confirmation = new ConfirmationViewModel(title, message, confirmText, onConfirm, () => Confirmation = null);

    /// <summary>A confirmation with a second choice besides the main one.</summary>
    internal void Confirm(string title, string message, string confirmText, Func<Task> onConfirm, string secondaryText, Func<Task> onSecondary) =>
        Confirmation = new ConfirmationViewModel(title, message, confirmText, onConfirm, () => Confirmation = null, secondaryText, onSecondary);

    /// <summary>
    /// Closes them together: each tab leaves the sidebar at once, then they wait for their claude processes side by
    /// side rather than one after another (a few seconds each for a working tab).
    /// </summary>
    [RelayCommand]
    private Task CloseOtherTabsAsync(TabViewModel? keep) =>
        CloseAllAndTidyAsync([.. AllTabs.Where(t => t != keep && !t.IsPinned)]);

    [RelayCommand]
    private Task CloseGroupAsync(TabGroupViewModel? group) =>
        CloseAllAndTidyAsync(group?.Tabs.Where(t => !t.IsPinned).ToArray() ?? []);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task CloseSelectedTabAsync() => CloseTabAsync(SelectedTab);

    private async Task RemoveTabAsync(TabViewModel tab, bool killProcesses = true)
    {
        LeaveThreadsOnClose(tab);
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
        OnTabStatusChanged();
        // Its worktree's inactivity counts from now (DESIGN.md §4, "Cleaning up worktrees").
        _services.Worktrees.MarkUsed(tab.State);
        await tab.CloseAsync(killProcesses);
        // Closed, not quit: its draft goes with it (DESIGN.md §5, "Drafts and the stash").
        await _services.Drafts.SaveAsync(tab.Id, null);
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

    /// <summary>
    /// <b>Change color…</b> in a group's menu opens this picker (DESIGN.md §4, "Grouped by folder"). What it picks applies
    /// at once and is remembered for the folder.
    /// </summary>
    public GroupColorPickerViewModel PickGroupColor(TabGroupViewModel group) => new(group, color => SetGroupColor(group, color));

    public void SetGroupColor(TabGroupViewModel group, Color color)
    {
        group.Color = color;
        GroupColors.Remember(_services.State, group.Folder, color);
        _services.SaveState();
    }

    [RelayCommand]
    private void MoveTabUp(TabViewModel? tab) => MoveTab(tab, -1);

    [RelayCommand]
    private void MoveTabDown(TabViewModel? tab) => MoveTab(tab, 1);

    /// <summary>
    /// Moves a tab within its group, keeping pinned tabs first. A thread moves with its sub-threads, past the next tab
    /// or thread, and a sub-thread moves among its thread's (DESIGN.md §18, "Threads").
    /// </summary>
    private void MoveTab(TabViewModel? tab, int offset)
    {
        var group = Groups.FirstOrDefault(g => tab is not null && g.Tabs.Contains(tab));
        if (group is null || tab is null)
        {
            return;
        }
        if (tab.ThreadHead is { } head)
        {
            var siblings = group.Tabs.Where(t => t.ThreadHead == head).ToList();
            var next = siblings.IndexOf(tab) + offset;
            if (next < 0 || next >= siblings.Count)
            {
                return;
            }
            group.Tabs.Move(group.Tabs.IndexOf(tab), group.Tabs.IndexOf(siblings[next]));
            SaveTabs();
            return;
        }
        var blocks = Blocks(group);
        var from = blocks.FindIndex(b => b[0] == tab);
        var to = from + offset;
        if (from < 0 || to < 0 || to >= blocks.Count || blocks[to][0].IsPinned != tab.IsPinned)
        {
            return;
        }
        (blocks[from], blocks[to]) = (blocks[to], blocks[from]);
        Reorder(group, [.. blocks.SelectMany(b => b)]);
        SaveTabs();
    }

    /// <summary>
    /// Dragging a tab (DESIGN.md §4): moves it to <paramref name="index"/> within its own group. Pinned tabs stay ahead
    /// of the others, so a tab only moves among tabs with the same pinned state. Returns whether it moved.
    /// </summary>
    public bool MoveTabTo(TabViewModel tab, int index)
    {
        if (Groups.FirstOrDefault(g => g.Tabs.Contains(tab)) is not { } group)
        {
            return false;
        }
        var pinned = group.Tabs.Count(t => t.IsPinned);
        var (first, last) = tab.IsPinned ? (0, pinned - 1) : (pinned, group.Tabs.Count - 1);
        var from = group.Tabs.IndexOf(tab);
        var to = Math.Clamp(index, first, last);
        if (to == from)
        {
            return false;
        }
        group.Tabs.Move(from, to);
        KeepSubThreadsUnderThreads(group);
        SaveTabs();
        return true;
    }

    /// <summary>Dragging a group label (DESIGN.md §4): moves the whole group. Returns whether it moved.</summary>
    public bool MoveGroupTo(TabGroupViewModel group, int index)
    {
        var from = Groups.IndexOf(group);
        var to = Math.Clamp(index, 0, Groups.Count - 1);
        if (from < 0 || to == from)
        {
            return false;
        }
        Groups.Move(from, to);
        SaveTabs();
        return true;
    }

    /// <summary>Called when a tab is pinned or unpinned: pinned tabs move to the start of their group.</summary>
    internal void OnPinChanged(TabViewModel tab)
    {
        if (Groups.FirstOrDefault(g => g.Tabs.Contains(tab)) is { } group)
        {
            group.Tabs.Remove(tab);
            var index = tab.IsPinned ? group.Tabs.Count(t => t.IsPinned) : group.Tabs.Count(t => t.IsPinned);
            group.Tabs.Insert(index, tab);
            KeepSubThreadsUnderThreads(group);
            if (SelectedTab != tab)
            {
                SelectedTab = tab;
            }
        }
        SaveTabs();
    }

    // ---- Restarting into a new build (DESIGN.md §9, "Working on Claudette") -------------------------------------

    /// <summary>The sidebar's entry for a new build of Claudette, when running from a source build.</summary>
    [ObservableProperty]
    public partial NewBuildViewModel? NewBuild { get; set; }

    /// <summary>The sidebar's entry for a new release of Claudette (DESIGN.md §2, "Updating Claudette").</summary>
    [ObservableProperty]
    public partial AppUpdateViewModel? AppUpdate { get; set; }

    /// <summary>
    /// The latest announcement, in a live region screen readers read out (DESIGN.md §3, "Accessibility"): a prompt that
    /// waits, a turn that finished, an error.
    /// </summary>
    [ObservableProperty]
    public partial string Announcement { get; private set; } = "";

    /// <summary>Has screen readers say <paramref name="text"/>, even when it's the same as the last time.</summary>
    public void Announce(string text) =>
        // A live region is read when it changes: the same words again need a change no one hears.
        Announcement = Announcement == text ? text + "\u200B" : text;

    /// <summary>Ctrl/Cmd + (DESIGN.md §3, "Accessibility"): the main window's content one step bigger.</summary>
    [RelayCommand]
    private void ZoomIn() => SetZoom(Zoom.In(_services.Settings.Appearance.Zoom));

    [RelayCommand]
    private void ZoomOut() => SetZoom(Zoom.Out(_services.Settings.Appearance.Zoom));

    [RelayCommand]
    private void ResetZoom() => SetZoom(Zoom.Default);

    private void SetZoom(int percent)
    {
        if (percent == _services.Settings.Appearance.Zoom)
        {
            return;
        }
        _services.Settings.Appearance.Zoom = percent;
        _services.SaveSettings();
        Announce($"Zoom {percent}%");
    }

    /// <summary>Whether a tab is starting, in a turn, or waiting on the user.</summary>
    public bool AnyTabWorking => AllTabs.Any(t => t.IsWorking || t.Status == TabStatus.Starting);

    /// <summary>Every open tab, what's typed in each, and which are running, for the build this one restarts into.</summary>
    public RestartSnapshot CaptureForRestart() => new()
    {
        Tabs = AllTabs.Select(t => t.State).ToList(),
        SelectedTabId = SelectedTab?.Id,
        Drafts = AllTabs.Where(t => t.Draft is not null).ToDictionary(t => t.Id, t => t.Draft!),
        RunningTabIds = AllTabs.Where(t => t.IsProcessRunning).Select(t => t.Id).ToList(),
        InterruptedTabIds = AllTabs.Where(t => t.IsInTurn).Select(t => t.Id).ToList(),
    };

    /// <summary>
    /// Stops every tab for a restart, as closing Claudette does, but keeps them in the saved state: the new build
    /// brings them back, or this one does if the new build doesn't start.
    /// </summary>
    public async Task CloseTabsForRestartAsync()
    {
        var tabs = AllTabs.ToArray();
        var selected = _services.State.SelectedTabId;
        SelectedTab = null;
        _services.State.SelectedTabId = selected;
        Groups.Clear();
        OpenTabs.Clear();
        OnPropertyChanged(nameof(HasTabs));
        // A turn is left as it is, not interrupted, so Claude Code can carry it on after the restart.
        await Task.WhenAll(tabs.Select(t => t.CloseAsync(killProcesses: true, interruptTurn: false).AsTask()));
        OnTabStatusChanged();
    }

    /// <summary>
    /// Settings → Appearance → Density is Compact (DESIGN.md §14): the view takes the <c>compact</c> class, whose styles
    /// tighten the conversation, the sidebar's rows and the composer.
    /// </summary>
    [ObservableProperty]
    public partial bool IsCompact { get; private set; }

    /// <summary>
    /// Settings → Appearance → Style is Claude (DESIGN.md §3, "Visual style"): the view takes the <c>claude</c> class,
    /// whose styles give the conversation the Claude apps' shapes: your messages in bubbles, a rounded composer with a
    /// round send button, rounder code blocks and cards. The colors and the replies' serif are app-wide resources.
    /// </summary>
    [ObservableProperty]
    public partial bool IsClaudeStyle { get; private set; }

    /// <summary>
    /// Settings → Appearance → Full-width conversation (DESIGN.md §14): the view takes the <c>fullwidth</c> class, whose
    /// styles let the conversation, the composer and the strips over them fill the tab rather than stop at 900 pixels.
    /// </summary>
    [ObservableProperty]
    public partial bool IsFullWidth { get; private set; }

    // ---- Sidebar and side panel (DESIGN.md §3, §4, "Sidebar") -------------------------------------------------

    /// <summary>The sidebar's width, and whether it shows only its rail, and the width every tab's side panel has.</summary>
    public ShellLayout Layout { get; }

    // ---- Settings -------------------------------------------------------------------------------------------

    [RelayCommand]
    private Task OpenSettingsAsync() => ShowSettingsAsync(null, SelectedTab);

    /// <summary>Opens Settings at a category, for example Quick suffixes from the suffix menu's <b>Edit suffixes…</b>.</summary>
    internal Task OpenSettingsAtAsync(string category) => ShowSettingsAsync(category, SelectedTab);

    /// <summary>
    /// Opens Settings at one of <paramref name="tab"/>'s project pages (DESIGN.md §14, "The project's pages"): Actions for
    /// <b>Add an action…</b> and Links for <b>Add a link…</b>, with a new one started. The Settings window follows the
    /// selected tab, so the tab is selected first when it isn't.
    /// </summary>
    internal Task OpenProjectSettingsAsync(TabViewModel tab, string page, bool startNew = false)
    {
        if (!ReferenceEquals(SelectedTab, tab) && AllTabs.Contains(tab))
        {
            SelectedTab = tab;
        }
        return ShowSettingsAsync(page, tab, startNew);
    }

    /// <summary>The window gets the project pages of the tab selected as it opens; with no tab, there are none.</summary>
    private Task ShowSettingsAsync(string? category, TabViewModel? tab, bool startNew = false) =>
        ShowSettingsWindow is { } show
            ? show(new SettingsOpening(category, tab is null ? null : new ProjectSettingsViewModel(_services, tab, this), startNew))
            : Task.CompletedTask;

    [RelayCommand]
    private void OpenTabSettings(TabViewModel? tab)
    {
        if (tab is not null)
        {
            TabSettings = new TabSettingsViewModel(_services, tab, () => TabSettings = null, this);
        }
    }

    // ---- Project tools (DESIGN.md §18) ------------------------------------------------------------------------

    /// <summary>A folder's project files changed: every tab in that folder reads them again.</summary>
    internal void OnProjectActionsChanged(string folder)
    {
        foreach (var tab in AllTabs.Where(t => FolderHistory.SamePath(t.Folder, folder)))
        {
            tab.ProjectTools.ReloadCustomActions();
        }
    }

    // ---- Sign-in ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Claude Code needs a sign-in (DESIGN.md §11): tabs hold the messages sent meanwhile, and deliver them once
    /// signed in again.
    /// </summary>
    public bool NeedsSignIn { get; private set; }

    /// <summary>A tab found Claude Code signed out.</summary>
    internal void OnAuthenticationRequired()
    {
        NeedsSignIn = true;
        _onAuthenticationRequired();
    }

    /// <summary>
    /// Signed out from the account menu or Settings. The next sign-in may be a different account, so every tab that's
    /// running restarts on its session then.
    /// </summary>
    public void OnSignedOut()
    {
        NeedsSignIn = true;
        foreach (var tab in AllTabs)
        {
            tab.OnSignedOut();
        }
    }

    /// <summary>Signed in again: tabs that failed restart with <c>--resume</c>, and held messages go out.</summary>
    public async Task OnSignedInAgainAsync()
    {
        NeedsSignIn = false;
        await Task.WhenAll(AllTabs.ToArray().Select(t => t.OnSignedInAgainAsync()));
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
