using System.Collections.ObjectModel;
using System.Text.Json;
using Claudette.App.Services;
using Claudette.Core.Development;
using Claudette.Core.Git;
using Claudette.Core.Library;
using Claudette.Core.Settings;
using Claudette.Platform.Processes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// The main UI once Claude Code is ready: the sidebar of tabs grouped by folder, the selected tab, the new-tab picker
/// and dialogs (DESIGN.md §3, §4).
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly AppServices _services;
    private readonly Action _onAuthenticationRequired;

    public ShellViewModel(AppServices services, Action onAuthenticationRequired)
    {
        _services = services;
        _onAuthenticationRequired = onAuthenticationRequired;
        IsSidebarCollapsed = services.State.SidebarCollapsed;
        SidebarWidth = Math.Clamp(services.State.SidebarWidth ?? DefaultSidebarWidth, MinSidebarWidth, MaxSidebarWidth);
        _services.Notifications.SelectedTabId = () => SelectedTab?.Id;
        IsCompact = services.Settings.Appearance.Density == Density.Compact;
        IsClaudeStyle = services.Settings.Appearance.Style == AppStyle.Claude;
        _services.SettingsChanged += (_, _) =>
        {
            IsCompact = _services.Settings.Appearance.Density == Density.Compact;
            IsClaudeStyle = _services.Settings.Appearance.Style == AppStyle.Claude;
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
                tab.OnRemoteControlAvailabilityChanged();
            }
        };
        _services.UsageHistoryCleared += (_, resetTabTotals) =>
        {
            if (resetTabTotals)
            {
                foreach (var tab in AllTabs)
                {
                    tab.ResetTokenTotals();
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
    /// A tab's status changed: the Dock/taskbar badge counts tabs needing input, and the icon animates while tabs work
    /// (DESIGN.md §10).
    /// </summary>
    internal void OnTabStatusChanged()
    {
        _services.Notifications.SetTabActivity(AllTabs.Count(t => t.NeedsInput), AllTabs.Count(t => t.Status == TabStatus.Working));
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
        var sampled = all.Where(t => t.LatestProcessSummary is not null).ToArray();
        if (sampled.Length == 0)
        {
            ProcessTotalsText = null;
            ProcessTotalsTip = null;
            return;
        }
        ProcessTotalsText = ProcessSummary.Sum(sampled.Select(t => t.LatestProcessSummary!)).UsageText;
        var others = all.Length - sampled.Length;
        ProcessTotalsTip = string.Join("\n",
        [
            "Processes of every tab, Claude Code included",
            .. sampled.Select(t => $"{t.DisplayName}: {t.LatestProcessSummary}"),
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
            oldValue.PropertyChanged -= OnSelectedTabPropertyChanged;
        }
        OnPropertyChanged(nameof(Links));
        OnPropertyChanged(nameof(HasLinks));
        if (newValue is not null)
        {
            newValue.PropertyChanged += OnSelectedTabPropertyChanged;
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
        foreach (var tab in AllTabs)
        {
            if (snapshot?.Drafts.GetValueOrDefault(tab.Id) is { } draft)
            {
                tab.RestoreDraft(draft);
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
            _ = tab.EnsureStartedAsync();
        }
        // Library retention and settings sync, at launch (DESIGN.md §9, §14). Retention keeps the sessions of open tabs
        // that sync; a tab that doesn't sync has no part in the library.
        var keep = state.Tabs.Where(t => t.SyncToLibrary).Select(t => t.SessionId).OfType<string>().ToHashSet();
        _ = Task.Run(() => _services.Library.Prune(keep));
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
    public Task OpenFolderAsync(string folder)
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
        AddTab(tab);
        UpdateGroupLabels();
        OnPropertyChanged(nameof(HasTabs));
        SelectedTab = selected;
        FolderHistory.Touch(_services.State, normalized, _services.Time.GetUtcNow(), _services.Settings.NewTabs.RecentFolderLimit);
        SaveTabs();
    }

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

    /// <summary>Resumes a past session in a new tab, with its earlier conversation loaded.</summary>
    public async Task OpenFromHistoryAsync(HistoryEntry entry)
    {
        if (!entry.IsConflictCopy && AllTabs.FirstOrDefault(t => t.State.SessionId == entry.SessionId) is { } open)
        {
            SelectedTab = open;
            return;
        }
        if (entry.IsConflictCopy)
        {
            if (entry.Record is not null)
            {
                await OpenFromLibraryAsync(entry, fork: true, takeOver: false);
            }
            return;
        }
        // This machine's copy, unless another machine carried the session on since: then the library's is newer.
        var fromLibrary = !entry.IsLocal || entry.ContinuedElsewhere;
        if (fromLibrary && entry.Record is null)
        {
            return;
        }
        Func<bool, bool, Task> resume = fromLibrary
            ? (fork, takeOver) => OpenFromLibraryAsync(entry, fork, takeOver)
            : (fork, takeOver) => OpenLocalAsync(entry, fork, takeOver);
        // One machine at a time (DESIGN.md §9), whichever copy it opens from.
        if (entry.Record is not null && _services.Library.CheckLease(entry.SessionId) is LeaseStatus.HeldByOther other)
        {
            Confirmation = new ConfirmationViewModel(
                $"\"{entry.Title}\" is open on {other.Machine}",
                $"It was last active there at {other.UpdatedAt.ToLocalTime():t}. Open a copy to continue separately, or take it over; the tab on {other.Machine} then becomes read-only.",
                "Take over",
                () => resume(false, true),
                () => Confirmation = null,
                "Open a copy",
                () => resume(true, false));
            return;
        }
        await resume(false, false);
    }

    /// <summary>A session whose transcript is on this machine: resumes it where Claude Code keeps it.</summary>
    private async Task OpenLocalAsync(HistoryEntry entry, bool fork, bool takeOver)
    {
        var folder = entry.Folder is { } known && Directory.Exists(known)
            ? known
            : await _services.Platform.PickFolderAsync($"Choose the folder for \"{entry.Title}\"");
        if (folder is null)
        {
            return;
        }
        if (takeOver)
        {
            _services.Library.Leases.Acquire(entry.SessionId, _services.Library.Library.GetSessionFolder(entry.SessionId));
        }
        OpenSession(NewState(entry, folder, transcriptPath: null, fork, _services.Settings.ClaudeCode.ConnectNewTabsToClaudeApp));
    }

    /// <summary>
    /// A session from the library, possibly recorded on another machine (DESIGN.md §9, "Restoring on another machine"):
    /// find the project here, warn if the code differs, then copy the transcript to a local working copy and resume it.
    /// </summary>
    private async Task OpenFromLibraryAsync(HistoryEntry entry, bool fork, bool takeOver)
    {
        var record = entry.Record!;
        var folder = await FindProjectFolderAsync(record, entry.Title);
        if (folder is null)
        {
            return;
        }
        if (record.Project is { } recorded)
        {
            var local = ProjectIdentity.Read(folder);
            var difference = ProjectIdentity.Compare(recorded, record.HadUncommittedChanges, local);
            if (difference.Any)
            {
                Confirmation = new ConfirmationViewModel(
                    "The code here may be different",
                    difference.Describe(record.Machine, recorded, local) + " Sync the code first (push and pull), or continue anyway.",
                    "Continue anyway",
                    () => ResumeFromLibraryAsync(entry, folder, fork, takeOver),
                    () => Confirmation = null);
                return;
            }
        }
        await ResumeFromLibraryAsync(entry, folder, fork, takeOver);
    }

    private async Task ResumeFromLibraryAsync(HistoryEntry entry, string folder, bool fork, bool takeOver)
    {
        var library = _services.Library;
        string transcript;
        try
        {
            if (entry.IsConflictCopy && entry.LibraryTranscript is { } copy)
            {
                // A conflict copy gets its own folder, so it can't overwrite the working copy of the original.
                var separate = Path.Combine(_services.Paths.LocalSessionsDirectory, $"copy-{Guid.NewGuid():N}");
                Directory.CreateDirectory(separate);
                transcript = Path.Combine(separate, $"{entry.SessionId}.jsonl");
                await Task.Run(() => File.Copy(copy, transcript, overwrite: true));
            }
            else
            {
                transcript = await library.CopyToLocalAsync(entry.SessionId);
            }
        }
        catch (Exception ex)
        {
            Confirmation = new ConfirmationViewModel("Couldn't open the session", ex.Message, "OK", () => Task.CompletedTask, () => Confirmation = null);
            return;
        }
        if (takeOver)
        {
            library.Leases.Acquire(entry.SessionId, library.Library.GetSessionFolder(entry.SessionId));
        }
        OpenSession(NewState(entry, folder, transcript, fork, _services.Settings.ClaudeCode.ConnectNewTabsToClaudeApp));
    }

    /// <summary>
    /// Where a project lives on this machine: a folder remembered for it, a recent or favorite folder in the same
    /// repository, the recorded path if it exists here, or else the user picks one, which is remembered.
    /// </summary>
    private async Task<string?> FindProjectFolderAsync(SessionRecord record, string title)
    {
        var state = _services.State;
        var key = record.Project is { RemoteUrl: { } remote } project ? $"{ProjectIdentity.NormalizeRemote(remote)}|{project.PathInRepo}" : null;
        if (key is not null && state.FolderMappings.TryGetValue(key, out var mapped) && Directory.Exists(mapped))
        {
            return mapped;
        }
        if (record.Project is { RemoteUrl: not null } identity)
        {
            var candidates = state.FavoriteFolders.Concat(state.RecentFolders.Select(r => r.Path)).Concat(AllTabs.Select(t => t.Folder)).Distinct();
            if (ProjectIdentity.FindMatchingFolder(identity, candidates) is { } match)
            {
                return match;
            }
        }
        if (record.Folder is { } recordedFolder && Directory.Exists(recordedFolder))
        {
            return recordedFolder;
        }
        var picked = await _services.Platform.PickFolderAsync($"Where is the folder for \"{title}\" on this machine?");
        if (picked is not null && key is not null)
        {
            state.FolderMappings[key] = picked;
            _services.SaveState();
        }
        return picked;
    }

    /// <param name="remoteControl">Settings → Claude Code → <b>Connect new tabs to the Claude app</b> (DESIGN.md §18).</param>
    private static TabState NewState(HistoryEntry entry, string folder, string? transcriptPath, bool fork, bool remoteControl)
    {
        var record = entry.Record;
        var state = new TabState
        {
            Folder = FolderHistory.Normalize(folder),
            SessionId = entry.SessionId,
            AutoName = record?.AutoName ?? entry.Title,
            UserName = fork ? null : record?.UserName,
            TranscriptPath = transcriptPath,
            ForkOnNextStart = fork,
            // A session someone synced keeps syncing wherever it's opened, a copy of one too; one that only ever lived
            // on this machine stays here (DESIGN.md §9, "Session library").
            SyncToLibrary = record is not null,
            // A tab opened from History is a new tab here; the phone connection belongs to this machine, not the record.
            RemoteControl = remoteControl,
        };
        if (record is not null)
        {
            // The tab's own choices come back with it (DESIGN.md §9); older records only had its model and effort.
            state.Overrides = record.Overrides is { } overrides
                ? JsonSerializer.Deserialize<TabOverrides>(JsonSerializer.Serialize(overrides, JsonFileStore<TabOverrides>.Options), JsonFileStore<TabOverrides>.Options) ?? new TabOverrides()
                : new TabOverrides { Model = record.Model, Effort = record.Effort };
            if (!fork)
            {
                state.Tokens = record.Tokens;
            }
        }
        return state;
    }

    private void OpenSession(TabState state)
    {
        FolderHistory.Touch(_services.State, state.Folder, _services.Time.GetUtcNow(), _services.Settings.NewTabs.RecentFolderLimit);
        var tab = new TabViewModel(_services, this, state, isRestored: true);
        AddTab(tab);
        SelectedTab = tab;
        SaveTabs();
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
        // Processes the tab started, such as dev servers, are stopped with it unless the user keeps them (DESIGN.md §4).
        var running = tab.RunningChildProcesses();
        if (running.Count > 0)
        {
            var names = string.Join(", ", running.Take(5).Select(p => $"{p.Name} ({p.Pid})")) + (running.Count > 5 ? $" and {running.Count - 5} more" : "");
            reasons.Add($"It started {(running.Count == 1 ? "a process that is" : $"{running.Count} processes that are")} still running: {names}.");
            Confirmation = new ConfirmationViewModel($"Close \"{tab.DisplayName}\"?", string.Join(" ", reasons),
                running.Count == 1 ? "Close and stop it" : "Close and stop them", () => RemoveTabAsync(tab), () => Confirmation = null,
                "Close, leave running", () => RemoveTabAsync(tab, killProcesses: false));
            return;
        }
        if (reasons.Count == 0)
        {
            _ = RemoveTabAsync(tab);
            return;
        }
        Confirmation = new ConfirmationViewModel($"Close \"{tab.DisplayName}\"?", string.Join(" ", reasons), "Close", () => RemoveTabAsync(tab), () => Confirmation = null);
    }

    /// <summary>A turn reached the usage history: refresh each tab's "this session window" tokens.</summary>
    internal void OnTurnRecorded()
    {
        foreach (var tab in AllTabs)
        {
            tab.RefreshTokenWindow();
        }
    }

    /// <summary>A plain confirmation over the whole window.</summary>
    internal void Confirm(string title, string message, string confirmText, Func<Task> onConfirm) =>
        Confirmation = new ConfirmationViewModel(title, message, confirmText, onConfirm, () => Confirmation = null);

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

    private async Task RemoveTabAsync(TabViewModel tab, bool killProcesses = true)
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
        OnTabStatusChanged();
        await tab.CloseAsync(killProcesses);
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
    private void MoveTabUp(TabViewModel? tab) => MoveTab(tab, -1);

    [RelayCommand]
    private void MoveTabDown(TabViewModel? tab) => MoveTab(tab, 1);

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

    /// <summary>Whether a tab is starting, in a turn, or waiting on the user.</summary>
    public bool AnyTabWorking => AllTabs.Any(t => t.IsWorking || t.Status == TabStatus.Starting);

    /// <summary>Every open tab, what's typed in each, and which are running, for the build this one restarts into.</summary>
    public RestartSnapshot CaptureForRestart() => new()
    {
        Tabs = AllTabs.Select(t => t.State).ToList(),
        SelectedTabId = SelectedTab?.Id,
        Drafts = AllTabs.Where(t => t.Draft is not null).ToDictionary(t => t.Id, t => t.Draft!),
        RunningTabIds = AllTabs.Where(t => t.IsProcessRunning).Select(t => t.Id).ToList(),
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
        await Task.WhenAll(tabs.Select(t => t.CloseAsync(killProcesses: true).AsTask()));
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

    // ---- Sidebar (DESIGN.md §4, "Sidebar") ------------------------------------------------------------------

    public const double DefaultSidebarWidth = 248;
    public const double MinSidebarWidth = 180;
    public const double MaxSidebarWidth = 420;

    /// <summary>The collapsed sidebar: a rail of status icons.</summary>
    public const double RailWidth = 52;

    /// <summary>Below this width the sidebar collapses to its rail by itself, leaving the user's own choice alone.</summary>
    public const double NarrowWidth = 900;

    private bool _isNarrow;

    /// <summary>Whether the sidebar shows only its rail.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSidebarExpanded), nameof(SidebarDisplayWidth))]
    public partial bool IsSidebarCollapsed { get; set; }

    public bool IsSidebarExpanded => !IsSidebarCollapsed;

    /// <summary>The expanded sidebar's width, as the user dragged it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarDisplayWidth))]
    public partial double SidebarWidth { get; set; }

    public double SidebarDisplayWidth => IsSidebarCollapsed ? RailWidth : SidebarWidth;

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
        // In a narrow window, expanding is only for now: once it's wide again, the user's choice comes back.
        if (!_isNarrow)
        {
            _services.State.SidebarCollapsed = IsSidebarCollapsed;
            _services.SaveState();
        }
    }

    /// <summary>Called by the view as the window resizes: a narrow window shows the rail.</summary>
    public void SetAvailableWidth(double width)
    {
        var narrow = width < NarrowWidth;
        if (narrow == _isNarrow)
        {
            return;
        }
        _isNarrow = narrow;
        IsSidebarCollapsed = narrow || _services.State.SidebarCollapsed;
    }

    /// <summary>Dragging the sidebar's edge. <see cref="SaveSidebarWidth"/> keeps the result when the drag ends.</summary>
    public void ResizeSidebar(double width) => SidebarWidth = Math.Clamp(width, MinSidebarWidth, MaxSidebarWidth);

    public void SaveSidebarWidth()
    {
        _services.State.SidebarWidth = SidebarWidth;
        _services.SaveState();
    }

    // ---- Settings -------------------------------------------------------------------------------------------

    [RelayCommand]
    private Task OpenSettingsAsync() => ShowSettingsAsync(null, SelectedTab);

    /// <summary>Opens Settings at a category, for example Quick suffixes from the suffix menu's <b>Edit suffixes…</b>.</summary>
    internal Task OpenSettingsAtAsync(string category) => ShowSettingsAsync(category, SelectedTab);

    /// <summary>
    /// Opens Settings at one of <paramref name="tab"/>'s project pages (DESIGN.md §14, "The project's pages"): Actions for
    /// <b>Add an action…</b>, with a new action started. The Settings window follows the selected tab, so the tab is
    /// selected first when it isn't.
    /// </summary>
    internal Task OpenProjectSettingsAsync(TabViewModel tab, string page, bool startNewAction = false)
    {
        if (!ReferenceEquals(SelectedTab, tab) && AllTabs.Contains(tab))
        {
            SelectedTab = tab;
        }
        return ShowSettingsAsync(page, tab, startNewAction);
    }

    /// <summary>The window gets the project pages of the tab selected as it opens; with no tab, there are none.</summary>
    private Task ShowSettingsAsync(string? category, TabViewModel? tab, bool startNewAction = false) =>
        ShowSettingsWindow is { } show
            ? show(new SettingsOpening(category, tab is null ? null : new ProjectSettingsViewModel(_services, tab, this), startNewAction))
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
            tab.ReloadCustomActions();
        }
    }

    // ---- Links (DESIGN.md §18, "Links"): the selected tab's, in the sidebar ------------------------------------------

    /// <summary>The selected tab's links, from its folder's claudette.json and claudette.local.json.</summary>
    public IReadOnlyList<Core.ProjectTools.ResolvedLink> Links => SelectedTab?.Links ?? [];

    public bool HasLinks => SelectedTab?.HasLinks == true;

    /// <summary>The Links section is folded to its heading. Remembered on this machine.</summary>
    public bool IsLinksCollapsed => _services.State.LinksCollapsed;

    public bool IsLinksExpanded => !IsLinksCollapsed;

    [RelayCommand]
    private void ToggleLinks()
    {
        _services.State.LinksCollapsed = !_services.State.LinksCollapsed;
        _services.SaveState();
        OnPropertyChanged(nameof(IsLinksCollapsed));
        OnPropertyChanged(nameof(IsLinksExpanded));
    }

    [RelayCommand]
    private Task OpenLinkAsync(Core.ProjectTools.ResolvedLink? link) =>
        link is { Url: { } url } ? _services.Platform.OpenUrlAsync(url) : Task.CompletedTask;

    private void OnSelectedTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.Links) or nameof(TabViewModel.HasLinks) or null or "")
        {
            OnPropertyChanged(nameof(Links));
            OnPropertyChanged(nameof(HasLinks));
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
