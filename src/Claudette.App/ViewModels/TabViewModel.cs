using System.Collections.ObjectModel;
using System.Globalization;
using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.Core.Claude;
using Claudette.Core.Development;
using Claudette.Core.Git;
using Claudette.Core.Library;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;
using Claudette.Core.Status;
using Claudette.Core.Transcripts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Claudette.App.ViewModels;

public enum TabStatus
{
    /// <summary>Restored, but its process hasn't started yet (DESIGN.md §9, "Starting fast").</summary>
    NotStarted,
    Starting,
    Idle,
    Working,
    /// <summary>A permission prompt is waiting.</summary>
    NeedsInput,
    /// <summary>Finished a turn while in the background.</summary>
    Unread,
    Error,
    Exited,
}

/// <summary>The ring on a tab's row (DESIGN.md §4, "Sidebar"): how full its context window is.</summary>
public enum ContextLevel
{
    /// <summary>No context data yet: the ring is hidden.</summary>
    None,
    Normal,
    /// <summary>Near the point where Claude Code compacts by itself: amber.</summary>
    High,
    /// <summary>Nearly full: red.</summary>
    Critical,
}

/// <summary>A quick suffix picked for the next message (DESIGN.md §5, "Quick suffixes").</summary>
public sealed partial class SuffixChip(QuickSuffix suffix, bool isKept) : ObservableObject
{
    public QuickSuffix Suffix { get; } = suffix;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    public partial bool IsKept { get; set; } = isKept;

    public string Label => IsKept ? $"{Suffix.Label} (kept)" : Suffix.Label;
}

/// <summary>An entry of the quick suffix menu (DESIGN.md §5).</summary>
/// <param name="Number">1–9 for the first nine, which those keys pick while the menu is open.</param>
/// <param name="Shortcut">The suffix's own shortcut, as it reads on this OS.</param>
/// <param name="IsOn">The suffix is on the message, as a chip: the menu shows a check mark, and picking it takes it off.</param>
public sealed record SuffixMenuItem(QuickSuffix Suffix, int? Number, string? Shortcut, bool IsOn);

/// <summary>One row of the tab info card (DESIGN.md §4).</summary>
public sealed record InfoRow(string Label, string Value);

/// <summary>A row of the token breakdown popover.</summary>
public sealed record TokenRow(string Model, string Input, string Output, string CacheWrite, string CacheRead, string Cost);

/// <summary>
/// One tab: one Claude Code session in a working folder (DESIGN.md §4). Starts its process lazily, when first selected
/// or sent a message.
/// </summary>
public sealed partial class TabViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private readonly ConversationBuilder _conversation;
    private readonly CheckInMonitor _checkIns;
    private ClaudeSession? _session;
    private Task? _pump;
    private Task? _starting;
    private bool _restoredTranscript;
    private bool _restartAfterSignIn;
    /// <summary>
    /// What's waiting on the user: permission prompts by request id, and the Perforce password prompt. A set, so a
    /// prompt that's resolved twice (answered here as the Claude app answers it, say) is only counted off once.
    /// </summary>
    private readonly HashSet<object> _waitingOnUser = [];
    private string? _firstPrompt;
    private bool _titleRequested;

    public TabViewModel(AppServices services, ShellViewModel shell, TabState state, bool isRestored)
    {
        _services = services;
        _shell = shell;
        State = state;
        ProcessMonitor = new ProcessMonitorViewModel(services, this);
        ChangedFiles = new ChangedFilesViewModel(services, this);
        ProjectTools = new ProjectToolsViewModel(services, this);
        RemoteControl = new RemoteControlViewModel(services, this);
        Agents = new AgentMap(services.Time, ModelDisplayName);
        Agents.Changed += OnAgentsChanged;
        Tasks = new RunningTasks(services.Time, Agents);
        Tasks.Changed += OnTasksChanged;
        _conversation = new ConversationBuilder(Items, TodoList, ModelDisplayName)
        {
            ExpandThinking = services.Settings.Appearance.ExpandThinking,
            ShowUnsupportedMessages = services.Settings.Advanced.LogProtocol,
            Agents = Agents,
            Tasks = Tasks,
            Time = services.Time,
        };
        // A prompt the phone answered is withdrawn by Claude Code (DESIGN.md §18, "Remote Control").
        _conversation.WithdrawnOutcome = RemoteControl.WithdrawnPromptOutcome;
        _checkIns = new CheckInMonitor(services.Time, () => CheckInSettings, SendCheckInFromTimer, stuck => _services.Dispatcher.Post(() => IsPossiblyStuck = stuck),
            countdown => _services.Dispatcher.Post(() => CheckInCountdown = countdown));
        _autoContinue = CreateAutoContinue();
        Status = TabStatus.NotStarted;
        _restoredTranscript = !isRestored;
        // The suffix menu checks the suffixes on the message.
        Chips.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SuffixMenu));
        foreach (var id in state.KeptSuffixes)
        {
            if (services.Settings.QuickSuffixes.FirstOrDefault(s => s.Id == id) is { } kept)
            {
                Chips.Add(new SuffixChip(kept, isKept: true));
            }
        }
        RefreshTokens();
        RestoreLimitWait();
        // Project tools (DESIGN.md §18): the project and the folder's own actions and links, as soon as they're read.
        _ = ProjectTools.RefreshAsync();
    }

    /// <summary>What's saved for this tab.</summary>
    public TabState State { get; }

    public string Id => State.Id;

    public string Folder => State.Folder;

    public string FolderName => Path.GetFileName(Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name ? name : Folder;

    public ObservableCollection<ConversationItem> Items { get; } = [];

    public TodoList TodoList { get; } = new();

    /// <summary>Asks the view to scroll to a conversation item, for example the card that started a process.</summary>
    public event Action<ConversationItem>? ScrollToRequested;

    /// <summary>Asks the view to scroll to <paramref name="item"/>, wherever it is in the conversation.</summary>
    public void ScrollTo(ConversationItem item) => ScrollToRequested?.Invoke(item);

    /// <summary>
    /// The conversation's top-level item that holds <paramref name="item"/>: itself, or the subagent group it's inside,
    /// however deep. Null when it isn't in the conversation. The view brings that into view first, since the
    /// conversation is virtualized and only items in view have controls.
    /// </summary>
    public ConversationItem? TopLevelItemOf(ConversationItem item)
    {
        foreach (var top in Items)
        {
            if (ReferenceEquals(top, item) || top is SubagentItem group && Contains(group, item))
            {
                return top;
            }
        }
        return null;

        static bool Contains(SubagentItem group, ConversationItem item) =>
            group.Items.Any(child => ReferenceEquals(child, item) || child is SubagentItem inner && Contains(inner, item));
    }

    /// <summary>The processes the tab started (DESIGN.md §4, "Process monitor").</summary>
    public ProcessMonitorViewModel ProcessMonitor { get; }

    /// <summary>The files Claude changed in this session, and their diffs (DESIGN.md §8).</summary>
    public ChangedFilesViewModel ChangedFiles { get; }

    /// <summary>The folder's project, its actions and links, and the runs of its jobs (DESIGN.md §18, "Project tools").</summary>
    public ProjectToolsViewModel ProjectTools { get; }

    /// <summary>The tab's connection to the Claude app (DESIGN.md §18, "Remote Control").</summary>
    public RemoteControlViewModel RemoteControl { get; }

    // ---- Name (DESIGN.md §4, "Naming") -------------------------------------------------------------------

    /// <summary>The user's name, else the name Claude Code gave the session, else the folder name.</summary>
    public string DisplayName => State.UserName ?? State.AutoName ?? FolderName;

    public bool HasUserName => State.UserName is not null;

    [ObservableProperty]
    public partial bool IsRenaming { get; set; }

    [ObservableProperty]
    public partial string RenameText { get; set; } = "";

    [RelayCommand]
    private void StartRename()
    {
        RenameText = DisplayName;
        IsRenaming = true;
    }

    [RelayCommand]
    private async Task CommitRenameAsync()
    {
        if (!IsRenaming)
        {
            return;
        }
        IsRenaming = false;
        var name = RenameText.Trim();
        State.UserName = name.Length == 0 || name == State.AutoName ? null : name;
        NameChanged();
        if (State.UserName is { } userName && _services.Settings.General.RenameInClaudeCode && _session is not null)
        {
            try
            {
                await _session.RenameSessionAsync(userName);
            }
            catch (Exception)
            {
                // Optional sync; the name is kept in Claudette either way.
            }
        }
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    [RelayCommand]
    private void ResetName()
    {
        State.UserName = null;
        NameChanged();
    }

    private void NameChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(HasUserName));
        _services.SaveState();
        // The usage history keeps the tab's last name, for its rows once it's closed (DESIGN.md §6).
        _services.Usage?.OnTabRenamed(Id, DisplayName);
    }

    // ---- Pinning (DESIGN.md §4) ---------------------------------------------------------------------------

    public bool IsPinned => State.IsPinned;

    public string PinMenuText => IsPinned ? "Unpin tab" : "Pin tab";

    [RelayCommand]
    private void TogglePin()
    {
        State.IsPinned = !State.IsPinned;
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(PinMenuText));
        OnPropertyChanged(nameof(CloseMissingText));
        _shell.OnPinChanged(this);
    }

    // ---- Status (DESIGN.md §4, "Status icon") ------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusGlyph), nameof(StatusTip), nameof(InfoRows), nameof(IsWorking), nameof(NeedsInput), nameof(IsBusyStatus), nameof(IsAlertStatus), nameof(IsErrorStatus), nameof(IsUnread), nameof(RowDetail), nameof(ShowTaskBadge), nameof(CanSyncNow), nameof(SyncNowTip))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand), nameof(SendCommand), nameof(RestartCommand), nameof(CompactCommand), nameof(SyncNowCommand))]
    public partial TabStatus Status { get; set; }

    public bool IsWorking => Status is TabStatus.Working or TabStatus.NeedsInput;

    public bool NeedsInput => Status == TabStatus.NeedsInput;

    public bool IsBusyStatus => Status is TabStatus.Working or TabStatus.Starting;

    public bool IsAlertStatus => Status == TabStatus.NeedsInput;

    public bool IsErrorStatus => Status == TabStatus.Error;

    public bool IsUnread => Status == TabStatus.Unread;

    public string StatusGlyph => Status switch
    {
        TabStatus.NotStarted or TabStatus.Exited => "○",
        TabStatus.Starting or TabStatus.Working => "●",
        TabStatus.NeedsInput => "!",
        TabStatus.Unread => "•",
        TabStatus.Error => "✕",
        _ => "",
    };

    public string StatusTip => Status switch
    {
        TabStatus.NotStarted => "Not started yet",
        TabStatus.Starting => "Starting…",
        TabStatus.Working => IsPossiblyStuck ? "Possibly stuck: no reply to two check-ins" : "Working",
        TabStatus.NeedsInput => "Needs your input",
        TabStatus.Unread => "Finished while in the background",
        TabStatus.Error => IsFolderMissing ? "Its folder no longer exists"
            : IsWaitingForSignIn ? "Waiting for you to sign in"
            : ErrorMessage ?? "Claude Code stopped with an error",
        TabStatus.Exited => "Not running",
        _ => "Idle",
    };

    /// <summary>
    /// The tab info card (DESIGN.md §4): the one place features add per-tab details, instead of the tab name.
    /// </summary>
    public IReadOnlyList<InfoRow> InfoRows
    {
        get
        {
            var rows = new List<InfoRow> { new("Folder", Folder) };
            if (GitInfo.TryGetBranch(Folder) is { } branch)
            {
                rows.Add(new InfoRow("Branch", branch));
            }
            rows.Add(new InfoRow("Model", $"{ModelName ?? "Default"} · {EffortName}"));
            if (PermissionMode is not null)
            {
                rows.Add(new InfoRow("Mode", PermissionModeName));
            }
            if (State.SessionStartedAt is { } started)
            {
                rows.Add(new InfoRow("Started", started.ToLocalTime().ToString("g")));
            }
            rows.Add(new InfoRow("Tokens", $"{TokensShort} · {State.Tokens.Turns} turns"));
            if (RunningVersion is { } running)
            {
                // DESIGN.md §12: open tabs keep the version they started with.
                rows.Add(_services.InstalledClaudeVersion is { } installed && installed > running
                    ? new InfoRow("Claude Code", $"Running {running}; {installed} is installed. New tabs use {installed}.")
                    : new InfoRow("Claude Code", running.ToString()));
            }
            if (ContextDetail is { } context)
            {
                rows.Add(new InfoRow("Context", $"{ContextText} ({context})"));
            }
            if (Agents.Summary is { } agents)
            {
                rows.Add(new InfoRow("Agents", agents));
            }
            if (Tasks.Summary is { } tasks)
            {
                rows.Add(new InfoRow("Running tasks", tasks));
            }
            ProjectTools.AddInfoRows(rows);
            AddPerforceRows(rows);
            RemoteControl.AddInfoRows(rows);
            AddLimitWaitRows(rows);
            rows.Add(new InfoRow("Status", StatusTip));
            return rows;
        }
    }

    /// <summary>The Claude Code version this tab's process runs, while it runs (DESIGN.md §12).</summary>
    public Version? RunningVersion { get; private set; }

    private void SetRunningVersion(Version? version)
    {
        if (version != RunningVersion)
        {
            RunningVersion = version;
            OnPropertyChanged(nameof(InfoRows));
            _shell.OnRunningVersionsChanged();
        }
    }

    /// <summary>The installed Claude Code version changed: the info card may need its "is installed" note.</summary>
    public void OnClaudeVersionsChanged() => OnPropertyChanged(nameof(InfoRows));

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        Working.SetShown(value);
        if (value)
        {
            ChangedFiles.RefreshIfStale();
            if (Status == TabStatus.Unread)
            {
                Status = TabStatus.Idle;
            }
            RefreshMessageTimes();
            _ = EnsureStartedAsync();
            // Its claudette.json may have changed while another tab was showing (DESIGN.md §18).
            _ = ProjectTools.RefreshFileAsync();
        }
        ProjectTools.UpdateShownRun();
    }

    /// <summary>Two check-ins in a row got no reply (DESIGN.md §5, "Check-ins on long turns"); shown on the tab's row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowDetail), nameof(StatusTip), nameof(InfoRows))]
    public partial bool IsPossiblyStuck { get; set; }

    // ---- Model, effort and mode (DESIGN.md §5, "Model & effort") ------------------------------------------

    public IReadOnlyList<ModelInfo> Models => _session?.Initialization?.Models.Where(m => m.Value != "default").ToArray() ?? [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelBadge), nameof(InfoRows), nameof(EffortLevels), nameof(PermissionModeChoices), nameof(RowDetail))]
    public partial string? ModelName { get; set; }

    /// <summary>The model id Claude Code reports (for example <c>claude-opus-5-5[1m]</c>).</summary>
    private string? _modelId;

    /// <summary>The effort level in use. Null means the model's default; Claude Code doesn't report it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffortName), nameof(ModelBadge), nameof(InfoRows), nameof(RowDetail))]
    public partial string? Effort { get; set; }

    public string EffortName => Effort is null ? "Default effort" : Capitalize(Effort);

    public string ModelBadge => $"{ShortModel(ModelName)} · {(Effort is null ? "Default" : Capitalize(Effort))}";

    /// <summary>
    /// The second line of the tab's row in the sidebar (DESIGN.md §4): the model and effort, or what needs attention
    /// when the tab is waiting on the user or has failed, or when it continues after a usage limit resets.
    /// </summary>
    public string RowDetail => Status is TabStatus.NeedsInput or TabStatus.Error ? StatusTip
        : Status == TabStatus.Working && IsPossiblyStuck ? "Possibly stuck"
        : LimitWaitRowDetail ?? ModelBadge;

    /// <summary>What went wrong when the tab is in the Error status, shown on its row and info card (DESIGN.md §4).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusTip), nameof(RowDetail), nameof(InfoRows))]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>The last line Claude Code wrote to standard error, or the exit code when it wrote nothing.</summary>
    internal static string ExitErrorMessage(TransportExit exit)
    {
        var lastLine = exit.StandardErrorTail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return lastLine ?? $"Claude Code stopped unexpectedly (exit code {exit.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"})";
    }

    public IReadOnlyList<string> EffortLevels => CurrentModelInfo?.SupportedEffortLevels ?? [];

    private ModelInfo? CurrentModelInfo => FindModel(_modelId) ?? _session?.Initialization?.Models.FirstOrDefault(m => m.Value == "default");

    /// <summary>The session's permission mode, as Claude Code reports it (it changes it too, for example after a plan).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InfoRows), nameof(PermissionModeName), nameof(IsBypassMode))]
    public partial string? PermissionMode { get; set; }

    public string PermissionModeName => PermissionModeInfo.Label(PermissionMode);

    /// <summary>Bypass mode gives the tab a warning style (DESIGN.md §7).</summary>
    public bool IsBypassMode => PermissionMode == PermissionModeInfo.Bypass;

    /// <summary>The modes to pick from: Auto only while the model supports it and no settings file turns it off.</summary>
    public IReadOnlyList<PermissionModeChoice> PermissionModeChoices =>
        IsAutoModeAvailable ? PermissionModeInfo.Choices : PermissionModeInfo.Choices.Where(c => !c.IsAuto).ToArray();

    /// <summary>What Claude Code's settings files say about the mode this tab starts in, read as it starts (DESIGN.md §7).</summary>
    private StartingPermissionMode? _startingMode;

    private bool IsAutoModeAvailable => _startingMode?.AutoModeDisabled != true && CurrentModelInfo?.SupportsAutoMode != false;

    /// <summary>The mode this tab starts in when neither its settings nor Claudette's choose one, once it has started.</summary>
    internal string? ClaudeCodeStartingMode => _startingMode is { } starting
        ? starting.Expected == PermissionModeInfo.Auto && !IsAutoModeAvailable ? PermissionModeInfo.Manual : starting.Expected
        : null;

    /// <summary>Bypass waiting for confirmation.</summary>
    [ObservableProperty]
    public partial bool IsConfirmingBypass { get; set; }

    /// <summary>
    /// Switches this session's permission mode (DESIGN.md §7). Session-only: the mode a tab starts in is set in Tab
    /// settings, and Claude Code changes the mode itself too, for example after a plan is approved.
    /// </summary>
    [RelayCommand]
    private async Task ChooseModeAsync(PermissionModeChoice? choice)
    {
        if (choice is null || choice.Value == PermissionMode)
        {
            return;
        }
        if (choice.IsBypass)
        {
            IsConfirmingBypass = true;
            return;
        }
        await SetModeAsync(choice.Value);
    }

    [RelayCommand]
    private Task ConfirmBypassAsync()
    {
        IsConfirmingBypass = false;
        return SetModeAsync(PermissionModeInfo.Bypass);
    }

    [RelayCommand]
    private void CancelBypass() => IsConfirmingBypass = false;

    private async Task SetModeAsync(string mode)
    {
        await EnsureStartedAsync();
        if (_session is null)
        {
            return;
        }
        try
        {
            await _session.SetPermissionModeAsync(mode);
            PermissionMode = mode;
        }
        catch (Exception ex)
        {
            var hint = mode == PermissionModeInfo.Bypass
                ? " Claude Code only allows this in a session that started in Bypass permissions mode; set that in Tab settings and reopen the tab."
                : "";
            _conversation.AddNote($"Couldn't switch to {PermissionModeInfo.Label(mode)} mode: {ex.Message}.{hint}", NoteKind.Error);
        }
    }

    /// <summary>
    /// Switches to the mode the tab starts in when nothing chose one. The user didn't ask for it, so if Claude Code
    /// refuses, the tab stays in the mode it reports, as a new session with the flag would.
    /// </summary>
    private async Task SwitchModeQuietlyAsync(ClaudeSession session, string mode)
    {
        try
        {
            await session.SetPermissionModeAsync(mode);
            PermissionMode = mode;
        }
        catch (Exception)
        {
            // Such as "auto mode unavailable for this model".
        }
    }

    // ---- Prompts from the keyboard (DESIGN.md §7: Ctrl/Cmd+Enter allows, Ctrl/Cmd+Backspace denies) ----------

    /// <summary>The oldest prompt still waiting for an answer.</summary>
    public PromptItem? WaitingPrompt => Items.OfType<PromptItem>().FirstOrDefault(p => p.IsPending);

    public bool AcceptWaitingPrompt() => WaitingPrompt?.TryAcceptFromKeyboard() == true;

    public bool DeclineWaitingPrompt() => WaitingPrompt?.TryDeclineFromKeyboard() == true;

    /// <summary>A model change waiting for confirmation (DESIGN.md §5: switching drops the prompt cache).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingModel), nameof(PendingModelMessage))]
    public partial ModelInfo? PendingModel { get; set; }

    public bool HasPendingModel => PendingModel is not null;

    public string PendingModelMessage
    {
        get
        {
            if (PendingModel is not { } model)
            {
                return "";
            }
            var message = $"Switch to {model.DisplayName}? Switching models resets this tab's cached context. The new model has to re-read the whole conversation, so your next message will use more of your limits.";
            if (Effort is { } effort && !model.SupportedEffortLevels.Contains(effort))
            {
                message += $" {model.DisplayName} doesn't support {effort} effort, so effort goes back to the model's default.";
            }
            return message;
        }
    }

    [RelayCommand]
    private void ChooseModel(ModelInfo? model)
    {
        if (model is not null && model != CurrentModelInfo)
        {
            PendingModel = model;
        }
    }

    [RelayCommand]
    private async Task ConfirmModelSwitchAsync()
    {
        if (PendingModel is not { } model || _session is null)
        {
            PendingModel = null;
            return;
        }
        PendingModel = null;
        try
        {
            await _session.SetModelAsync(model.Value);
            State.Overrides.Model = model.Value;
            _modelId = model.ResolvedModel ?? model.Value;
            ModelName = model.DisplayName;
            if (Effort is { } effort && !model.SupportedEffortLevels.Contains(effort))
            {
                await _session.SetEffortAsync(null);
                Effort = null;
                State.Overrides.Effort = null;
            }
            OnPropertyChanged(nameof(HasOverrides));
            _services.SaveState();
        }
        catch (Exception ex)
        {
            _conversation.AddNote($"Couldn't switch model: {ex.Message}", NoteKind.Error);
        }
    }

    [RelayCommand]
    private void CancelModelSwitch() => PendingModel = null;

    [RelayCommand]
    private async Task ChooseEffortAsync(string? level)
    {
        if (_session is null || level == Effort)
        {
            return;
        }
        try
        {
            await _session.SetEffortAsync(level);
            Effort = level;
            State.Overrides.Effort = level;
            OnPropertyChanged(nameof(HasOverrides));
            _services.SaveState();
        }
        catch (Exception ex)
        {
            _conversation.AddNote($"Couldn't change effort: {ex.Message}", NoteKind.Error);
        }
    }

    // ---- Context and tokens (DESIGN.md §4, §6) -----------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextTip))]
    public partial string? ContextText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InfoRows), nameof(ContextTip))]
    public partial string? ContextDetail { get; set; }

    /// <summary>Near the point where Claude Code compacts by itself: the indicator and the ring turn amber.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextLevel))]
    public partial bool IsContextHigh { get; set; }

    /// <summary>How full the context window is, 0–100. Null until the tab has context data (it hasn't started yet).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextLevel), nameof(IsContextCritical), nameof(ContextSweep), nameof(ShowContextRing))]
    public partial double? ContextPercent { get; set; }

    /// <summary>From this full, the ring on the tab's row turns red (DESIGN.md §4, "Sidebar").</summary>
    public const double CriticalContextPercent = 95;

    /// <summary>What the ring on the tab's row shows: nothing, muted, amber or red.</summary>
    public ContextLevel ContextLevel => ContextPercent is not { } percent ? ContextLevel.None
        : percent >= CriticalContextPercent ? ContextLevel.Critical
        : IsContextHigh ? ContextLevel.High
        : ContextLevel.Normal;

    public bool IsContextCritical => ContextLevel == ContextLevel.Critical;

    /// <summary>The ring's arc, in degrees clockwise from the top.</summary>
    public double ContextSweep => Math.Clamp(ContextPercent ?? 0, 0, 100) * 3.6;

    /// <summary>The ring on the tab's row: once there's context data, unless Settings → Appearance turns it off.</summary>
    public bool ShowContextRing => ContextPercent is not null && _services.Settings.Appearance.ShowContextOnTabs;

    /// <summary>The ring's tooltip: the composer bar's context text and its detail.</summary>
    public string? ContextTip => ContextText is not { } text ? null : ContextDetail is { } detail ? $"{text} ({detail})" : text;

    /// <summary>
    /// What fills the context window, for the flyout the context ring and the composer's indicator open (DESIGN.md §6,
    /// "Per-tab context"). From the same <c>get_context_usage</c> reply as the indicator, or the estimate without it.
    /// </summary>
    [ObservableProperty]
    public partial ContextBreakdown? ContextBreakdown { get; set; }

    /// <summary>Summarizes the conversation to free context, like <c>/compact</c> in the terminal (DESIGN.md §6).</summary>
    [RelayCommand(CanExecute = nameof(CanCompact))]
    private async Task CompactAsync()
    {
        _conversation.AddNote("Compacting the conversation…");
        await SendRawAsync("/compact");
    }

    private bool CanCompact() => Status is TabStatus.Idle or TabStatus.Unread;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InfoRows))]
    public partial string TokensShort { get; set; } = "0 tok";

    public ObservableCollection<TokenRow> TokenRows { get; } = [];

    [ObservableProperty]
    public partial string TokenSummary { get; set; } = "";

    /// <summary>"Clear usage history" with "Also reset per-tab token totals" (DESIGN.md §6).</summary>
    public void ResetTokenTotals()
    {
        State.Tokens = new TokenTotals();
        RefreshTokens();
        _services.SaveState();
    }

    /// <summary>Per-call usage from assistant messages, for live counts mid-turn and the context estimate (DESIGN.md §6).</summary>
    private readonly CallUsage _callUsage = new();

    /// <summary><c>get_context_usage</c> failed for this session, so the context indicator is estimated from each call.</summary>
    private bool _contextUsageUnavailable;

    /// <summary>When Claude Code compacts by itself, from <c>autocompact_state</c>, for the estimate's warning.</summary>
    private AutocompactStateMessage? _autocompact;

    /// <summary>A call finished mid-turn: the token count moves on before the result gives the turn's totals.</summary>
    private void OnCallUsage()
    {
        TokensShort = TokenTotals.Short(State.Tokens.Total + _callUsage.TurnTokens);
        Working.Refresh();
        if (_contextUsageUnavailable)
        {
            ShowEstimatedContext();
        }
    }

    /// <summary>
    /// The context indicator without <c>get_context_usage</c>: the main agent's latest call ÷ its model's context window
    /// (DESIGN.md §6, "Per-tab context"). Unknown until a turn has reported the window.
    /// </summary>
    private void ShowEstimatedContext()
    {
        if (_callUsage.ContextPercentage is not { } percentage || _callUsage.ContextWindow is not { } window)
        {
            return;
        }
        var tokens = _callUsage.ContextTokens ?? 0;
        var compacts = _autocompact is { Enabled: true, Threshold: { } threshold } ? $" · auto-compacts at {threshold:N0}" : "";
        ContextText = $"Context {percentage:0}%";
        ContextDetail = $"about {tokens:N0} of {window:N0} tokens, estimated from the last call{compacts}";
        IsContextHigh = _autocompact is { Enabled: true, Threshold: { } limit } ? tokens >= limit * 0.9 : percentage >= 80;
        ContextPercent = percentage;
        ContextBreakdown = ContextBreakdown.Estimated(tokens, window).KeepingExpanded(ContextBreakdown);
    }

    private void RefreshTokens()
    {
        var totals = State.Tokens;
        TokensShort = TokenTotals.Short(totals.Total + _callUsage.TurnTokens);
        TokenRows.Clear();
        foreach (var (model, t) in totals.Models.OrderByDescending(m => m.Value.Total))
        {
            TokenRows.Add(new TokenRow(ModelDisplayName(model) ?? model, N(t.Input), N(t.Output), N(t.CacheWrite), N(t.CacheRead), $"${t.EstimatedCostUsd:0.00}"));
        }
        TokenSummary = $"{totals.Turns} turn{(totals.Turns == 1 ? "" : "s")} · {totals.Total:N0} tokens · about ${totals.EstimatedCostUsd:0.00} at list price (an estimate, not your bill)";
        _ = RefreshTokenWindowAsync();

        static string N(long n) => n.ToString("N0");
    }

    /// <summary>
    /// This tab's tokens since the current 5-hour window started, the part that counts against the session limit, and
    /// its recent turns for the popover's chart (DESIGN.md §4, "Token stats per tab"). From the usage history.
    /// </summary>
    [ObservableProperty]
    public partial string? TokenWindowText { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<Controls.ChartPoint> TurnPoints { get; set; } = [];

    [ObservableProperty]
    public partial double TurnChartMaximum { get; set; } = 1;

    public void RefreshTokenWindow() => _ = RefreshTokenWindowAsync();

    private async Task RefreshTokenWindowAsync()
    {
        if (_services.Usage is not { } usage)
        {
            return;
        }
        var now = _services.Time.GetUtcNow();
        var windowStart = usage.Current?.Session?.ResetsAt is { } resets ? resets - TimeSpan.FromHours(5) : now - TimeSpan.FromHours(5);
        try
        {
            var (inWindow, turns) = await Task.Run(() =>
            {
                var window = usage.Store.GetTurns(windowStart, now, Id).Sum(t => t.Total);
                var recent = usage.Store.GetTurns(now - TimeSpan.FromDays(7), now, Id)
                    .GroupBy(t => t.Timestamp)
                    .Select(g => new Controls.ChartPoint(g.Key, g.Sum(t => t.Total)))
                    .OrderBy(p => p.Time)
                    .TakeLast(40)
                    .ToArray();
                return (window, recent);
            });
            TokenWindowText = $"This session window: {TokenTotals.Short(inWindow)}";
            TurnPoints = turns;
            TurnChartMaximum = turns.Length > 0 ? turns.Max(p => p.Value) : 1;
        }
        catch (Exception)
        {
            // The usage history is optional here.
        }
    }

    // ---- Composer and quick suffixes (DESIGN.md §5) -------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string ComposerText { get; set; } = "";

    public ObservableCollection<SuffixChip> Chips { get; } = [];

    public IReadOnlyList<QuickSuffix> AvailableSuffixes => _services.Settings.QuickSuffixes;

    /// <summary>
    /// The suffix menu: numbered 1–9 for picking from the keyboard, with each suffix's own shortcut and a check mark on
    /// those already on the message (DESIGN.md §5).
    /// </summary>
    public IReadOnlyList<SuffixMenuItem> SuffixMenu => AvailableSuffixes
        .Select((suffix, index) => new SuffixMenuItem(suffix, index < 9 ? index + 1 : null,
            KeyChord.TryParse(suffix.Shortcut, out var chord) ? chord.Display(Shortcuts.IsMac) : null,
            Chips.Any(c => c.Suffix.Id == suffix.Id)))
        .ToArray();

    /// <summary>Picks the <paramref name="number"/>th suffix (1–9) in the menu, as a click on it does.</summary>
    public bool PickSuffix(int number)
    {
        if (number < 1 || number > Math.Min(9, AvailableSuffixes.Count))
        {
            return false;
        }
        ToggleSuffix(AvailableSuffixes[number - 1]);
        return true;
    }

    /// <summary>A suffix picked in the menu: added as a chip, or taken off when it's already on, kept or not.</summary>
    [RelayCommand]
    private void ToggleSuffix(QuickSuffix? suffix)
    {
        if (suffix is not null && Chips.FirstOrDefault(c => c.Suffix.Id == suffix.Id) is { } chip)
        {
            RemoveChip(chip);
        }
        else
        {
            AddSuffix(suffix);
        }
    }

    /// <summary><b>Edit suffixes…</b> in the suffix menu opens Settings → Quick suffixes.</summary>
    [RelayCommand]
    private Task EditSuffixesAsync() => _shell.OpenSettingsAtAsync("Quick suffixes");

    /// <summary>Settings → Keyboard, for the tab's own shortcuts (Stop, the suffix menu, answering prompts).</summary>
    public KeyboardSettings Keyboard => _services.Settings.Keyboard;

    public ShortcutTips Tips => _services.Tips;

    [RelayCommand]
    private void AddSuffix(QuickSuffix? suffix)
    {
        if (suffix is not null && Chips.All(c => c.Suffix.Id != suffix.Id))
        {
            Chips.Add(new SuffixChip(suffix, isKept: false));
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void RemoveChip(SuffixChip? chip)
    {
        if (chip is not null && Chips.Remove(chip))
        {
            if (chip.IsKept)
            {
                SaveKeptSuffixes();
            }
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void ToggleKeep(SuffixChip? chip)
    {
        if (chip is not null)
        {
            chip.IsKept = !chip.IsKept;
            SaveKeptSuffixes();
        }
    }

    /// <summary>Settings changed, for example the quick suffix list was edited.</summary>
    public void OnSettingsChanged()
    {
        OnPropertyChanged(nameof(AvailableSuffixes));
        OnPropertyChanged(nameof(SuffixMenu));
        _conversation.ExpandThinking = _services.Settings.Appearance.ExpandThinking;
        _conversation.ShowUnsupportedMessages = _services.Settings.Advanced.LogProtocol;
        OnPropertyChanged(nameof(ShowContextRing));
        ProcessMonitor.UpdateSampler();
        _autoContinue.SettingsChanged();
        OnPerforceSettingsChanged();
        ProjectTools.OnSettingsChanged();
    }

    // ---- Restarting into a new build (DESIGN.md §9, "Working on Claudette") -------------------------------

    /// <summary>The message typed but not sent, with its one-off quick suffixes and attached images, or null when there's none.</summary>
    public TabDraft? Draft => ComposerText.Length == 0 && Chips.All(c => c.IsKept) && Attachments.Count == 0
        ? null
        : new TabDraft(ComposerText, Chips.Where(c => !c.IsKept).Select(c => c.Suffix.Id).ToList(), Attachments.Count > 0 ? DraftImages() : null);

    /// <summary>Puts back a draft from the build that restarted into this one.</summary>
    public void RestoreDraft(TabDraft draft)
    {
        ComposerText = draft.Text;
        foreach (var id in draft.SuffixIds)
        {
            AddSuffix(_services.Settings.QuickSuffixes.FirstOrDefault(s => s.Id == id));
        }
        RestoreDraftImages(draft.Images);
    }

    /// <summary>Whether this tab's Claude Code is running.</summary>
    public bool IsProcessRunning => _session is not null;

    private void SaveKeptSuffixes()
    {
        State.KeptSuffixes = Chips.Where(c => c.IsKept).Select(c => c.Suffix.Id).ToList();
        _services.SaveState();
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = ComposerText.Trim();
        var suffixes = Chips.Select(c => c.Suffix.Text).ToArray();
        if (text.Length == 0 && suffixes.Length == 0 && Attachments.Count == 0)
        {
            return;
        }
        var suffixText = suffixes.Length > 0 ? string.Join("\n", suffixes) : null;

        ComposerText = "";
        foreach (var chip in Chips.Where(c => !c.IsKept).ToArray())
        {
            Chips.Remove(chip);
        }
        var images = TakeAttachments();
        _autoContinue.UserSent();
        _conversation.AddUserMessage(text, suffixText, images: images);
        _firstPrompt ??= text.Length > 0 ? text : suffixText;
        await SendRawAsync(text, images, suffixText);
        _ = RequestTitleAsync();
    }

    private bool CanSend() => !IsReadOnly && (Status is not (TabStatus.Starting or TabStatus.Error) || IsWaitingForSignIn) && (ComposerText.Trim().Length > 0 || Chips.Count > 0 || Attachments.Count > 0);

    /// <param name="suffix">Quick suffixes, which Claude Code gets after the message, or beside a slash command (DESIGN.md §5).</param>
    private async Task SendRawAsync(string message, IReadOnlyList<MessageImage>? images = null, string? suffix = null)
    {
        var pending = new PendingMessage(message, images ?? [], suffix);
        // Held while Claude Code needs a sign-in, and sent once it's done (DESIGN.md §11).
        if (HoldForSignIn(pending))
        {
            return;
        }
        try
        {
            await EnsureStartedAsync();
            if (_session is null)
            {
                // It couldn't start because Claude Code needs a sign-in: the message waits for it.
                HoldForSignIn(pending);
                return;
            }
            _awaitingReply.Add(pending);
            await _session.SendUserMessageAsync(message, pending.Images, suffix);
        }
        catch (Exception ex)
        {
            _conversation.AddNote($"Couldn't send the message: {ex.Message}", NoteKind.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(IsWorking))]
    private async Task StopAsync()
    {
        if (_session is null)
        {
            return;
        }
        RemoteControl.OnStoppedHere();
        try
        {
            await _session.InterruptAsync();
        }
        catch (Exception ex)
        {
            _conversation.AddNote($"Couldn't stop Claude: {ex.Message}", NoteKind.Error);
        }
    }

    /// <summary>Opens a link clicked in a reply.</summary>
    [RelayCommand]
    private Task OpenLinkAsync(object? link) =>
        link?.ToString() is { Length: > 0 } url ? _services.Platform.OpenUrlAsync(url) : Task.CompletedTask;

    // ---- Copy and times (DESIGN.md §5, "Copy and times") ---------------------------------------------------

    /// <summary>How long a Copy button says "Copied".</summary>
    public static readonly TimeSpan CopiedFor = TimeSpan.FromSeconds(1.5);

    /// <summary>What says "Copied" now, and the timer that puts it back.</summary>
    private readonly Dictionary<object, ITimer> _copied = [];

    /// <summary><b>Copy message</b> on a user message or a reply: its text, as Markdown for a reply.</summary>
    [RelayCommand]
    private async Task CopyMessageAsync(MessageItem? message)
    {
        if (message is null)
        {
            return;
        }
        await _services.Platform.SetClipboardTextAsync(message.CopyText);
        ShowCopied(message, copied => message.IsCopied = copied);
    }

    /// <summary>
    /// <b>Copy</b> on a code block in a reply: the code as the block shows it, without the Markdown fences.
    /// <paramref name="showCopied"/> switches the block's button to "Copied" and back.
    /// </summary>
    public async Task CopyCodeAsync(string code, object block, Action<bool> showCopied)
    {
        await _services.Platform.SetClipboardTextAsync(code.TrimEnd('\r', '\n'));
        ShowCopied(block, showCopied);
    }

    /// <summary>Says "Copied" on <paramref name="target"/> for <see cref="CopiedFor"/>, timed by the tab's clock.</summary>
    private void ShowCopied(object target, Action<bool> show)
    {
        if (_copied.Remove(target, out var earlier))
        {
            earlier.Dispose();
        }
        show(true);
        ITimer? timer = null;
        timer = _services.Time.CreateTimer(_ => _services.Dispatcher.Post(() =>
        {
            if (_copied.TryGetValue(target, out var current) && ReferenceEquals(current, timer))
            {
                _copied.Remove(target);
                current.Dispose();
                show(false);
            }
        }), null, CopiedFor, Timeout.InfiniteTimeSpan);
        _copied[target] = timer;
    }

    /// <summary>A message's short time says "today" only on the day it was sent; looking at the tab again catches up.</summary>
    private void RefreshMessageTimes()
    {
        foreach (var message in Messages(Items))
        {
            message.RefreshTime();
        }

        static IEnumerable<MessageItem> Messages(IEnumerable<ConversationItem> items) => items.SelectMany(item => item switch
        {
            MessageItem message => [message],
            SubagentItem group => Messages(group.Items),
            _ => [],
        });
    }

    // ---- Check-ins (DESIGN.md §5, "Check-ins on long turns") ----------------------------------------------

    private CheckInSettings CheckInSettings => State.Overrides.CheckIns ?? _services.Settings.CheckIns;

    private void SendCheckInFromTimer(string message) => _services.Dispatcher.Post(() =>
    {
        if (_session is null || !IsWorking)
        {
            return;
        }
        _conversation.AddUserMessage(message, isCheckIn: true);
        _ = SendRawAsync(message);
        NotifyCheckIn();
    });

    private ITimer? _checkInTicker;

    /// <summary>A check-in counting down to being sent, shown in a bar over the composer; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCheckInCountdown), nameof(CheckInCountdownText))]
    public partial CheckInCountdown? CheckInCountdown { get; private set; }

    public bool HasCheckInCountdown => CheckInCountdown is not null;

    /// <summary>What the bar says, counting down each second.</summary>
    public string? CheckInCountdownText => CheckInCountdown is { } countdown
        ? $"Checking in with Claude in {Math.Max(0, (int)Math.Ceiling((countdown.SendsAt - _services.Time.GetUtcNow()).TotalSeconds))} s."
        : null;

    partial void OnCheckInCountdownChanged(CheckInCountdown? value)
    {
        if (value is null)
        {
            StopCheckInTicker();
        }
        else
        {
            _checkInTicker ??= _services.Time.CreateTimer(_ => _services.Dispatcher.Post(() => OnPropertyChanged(nameof(CheckInCountdownText))), null,
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
    }

    private void StopCheckInTicker()
    {
        _checkInTicker?.Dispose();
        _checkInTicker = null;
    }

    /// <summary><b>Send now</b> on the bar: the check-in goes without waiting out the countdown.</summary>
    [RelayCommand]
    private void SendCheckInNow() => _checkIns.SendNow();

    /// <summary>✕ on the bar: this check-in isn't sent, and the next one waits a whole interval again.</summary>
    [RelayCommand]
    private void SkipCheckIn() => _checkIns.Skip();

    // ---- Lifecycle -----------------------------------------------------------------------------------------

    public bool CanRestart => Status is TabStatus.Exited or TabStatus.Error && !IsFolderMissing && !IsSessionMissing;

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private Task RestartAsync()
    {
        // By hand it tries again even while waiting for a sign-in, say after signing in from a terminal (DESIGN.md §11).
        _restartAfterSignIn = false;
        return EnsureStartedAsync();
    }

    /// <summary>Starts the process if it isn't running: restored tabs start on first selection or message.</summary>
    public async Task EnsureStartedAsync()
    {
        if (_session is not null || IsSessionMissing || Status == TabStatus.Error && (!Directory.Exists(Folder) || WaitsForSignIn))
        {
            return;
        }
        // Callers arriving while a start is under way share it.
        var starting = _starting ??= StartAsync();
        try
        {
            await starting;
        }
        finally
        {
            if (ReferenceEquals(_starting, starting))
            {
                _starting = null;
            }
        }
    }

    private async Task StartAsync()
    {
        if (_services.Sessions is not { } sessions)
        {
            return;
        }
        if (!Directory.Exists(Folder))
        {
            MarkFolderMissing();
            return;
        }

        ErrorMessage = null;
        Status = TabStatus.Starting;
        var resume = State.SessionId;
        if (!_restoredTranscript)
        {
            _restoredTranscript = true;
            resume = await RestoreTranscriptAsync();
            if (IsSessionMissing)
            {
                ErrorMessage = "Its earlier conversation is gone";
                Status = TabStatus.Error;
                return;
            }
        }
        // A library session resumes from its local working copy, which Claude Code keeps writing to (DESIGN.md §9).
        if (resume is not null && State.TranscriptPath is { } localCopy && File.Exists(localCopy))
        {
            resume = localCopy;
        }
        var fork = State.ForkOnNextStart && resume is not null;
        // Only a tab that syncs takes part in leases (DESIGN.md §9, "One machine at a time").
        if (resume is not null && !fork && State.SyncToLibrary && !await ClaimLeaseAsync())
        {
            return;
        }

        var settings = _services.Settings;
        try
        {
            // With no mode chosen, a tab starts in auto mode as a terminal session would, where Claude Code's settings
            // allow; claude -p alone starts in Manual. A resumed session is switched once it has started instead, so it
            // can come back in plan mode (DESIGN.md §7, "Starting mode").
            var chosenMode = State.Overrides.PermissionMode ?? settings.NewTabs.DefaultPermissionMode;
            var folder = Folder;
            var starting = _startingMode = await Task.Run(() => _services.ReadStartingPermissionMode(folder));
            var session = await sessions.StartAsync(await WithPerforceAsync(await ProjectTools.WithNoteAsync(new ClaudeLaunchOptions
            {
                WorkingDirectory = Folder,
                Resume = resume,
                ForkSession = fork,
                Model = State.Overrides.Model ?? settings.NewTabs.DefaultModel,
                Effort = State.Overrides.Effort ?? settings.NewTabs.DefaultEffort,
                PermissionMode = chosenMode ?? (resume is null ? starting.LaunchMode : null),
                AdditionalArguments = settings.Advanced.ExtraArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                ProtocolLogPath = _services.ProtocolLogPath(FolderName),
                // The presence file, among others: no pushes to the phone while Claudette is in front (DESIGN.md §10).
                EnvironmentOverrides = _services.RemoteControl.ClaudeVariables,
            })));
            _session = session;
            // A new claude has none of the old one's tasks.
            Tasks.Clear();
            _ = LoadSpinnerVerbsAsync();
            _services.RememberModels(session.Initialization?.Models);
            State.SessionStartedAt ??= _services.Time.GetUtcNow();
            // The installed version is what just started; system/init confirms it with the first turn.
            SetRunningVersion(_services.InstalledClaudeVersion);
            if (fork)
            {
                // The copy gets a new session id with its first turn; it no longer writes to the original.
                State.ForkOnNextStart = false;
                State.TranscriptPath = null;
                _forkAwaitingId = true;
                _conversation.AddNote("Opened as a copy. The original session is left as it was.");
            }
            ProcessMonitor.AttachTree(session);
            Effort = State.Overrides.Effort ?? settings.NewTabs.DefaultEffort;
            PermissionMode = session.PermissionMode;
            _modelId = session.Model ?? State.Overrides.Model;
            ModelName = ModelDisplayName(_modelId) ?? CurrentModelInfo?.DisplayName;
            OnPropertyChanged(nameof(Models));
            OnPropertyChanged(nameof(EffortLevels));
            OnPropertyChanged(nameof(PermissionModeChoices));
            if (chosenMode is null && resume is not null && starting.LaunchMode is { } launchMode
                && session.PermissionMode == PermissionModeInfo.Manual && IsAutoModeAvailable)
            {
                await SwitchModeQuietlyAsync(session, launchMode);
            }
            Status = TabStatus.Idle;
            _pump = PumpAsync(session);
            _contextRefresh = RefreshContextUsageAsync(session);
            // Before any prompt goes out, so the phone sees the whole turn (DESIGN.md §18, "Remote Control").
            RemoteControl.ConnectOnStart(session);
        }
        catch (Exception ex) when (Core.Auth.SignInErrors.IsSignInFailure(ex))
        {
            OnStartFailedForSignIn(ex);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Couldn't start Claude Code: {ex.Message}";
            Status = TabStatus.Error;
            _conversation.AddNote($"Couldn't start Claude Code: {ex.Message}", NoteKind.Error);
            NotifyProcessError($"Couldn't start Claude Code: {ex.Message}");
        }
    }

    // ---- Missing folder (DESIGN.md §9, "Missing folder") -------------------------------------------------------

    /// <summary>The tab's folder no longer exists, for example a deleted clone: the tab can't start until it's found.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderMissingText), nameof(StatusTip), nameof(RowDetail), nameof(InfoRows), nameof(CanRestart))]
    [NotifyCanExecuteChangedFor(nameof(RestartCommand))]
    public partial bool IsFolderMissing { get; set; }

    public string FolderMissingText => $"The folder {Folder} no longer exists. If it moved, choose where it is now and the session carries on there.";

    public string CloseMissingText => IsPinned ? "Unpin and close" : "Close tab";

    internal void MarkFolderMissing()
    {
        IsFolderMissing = true;
        Status = TabStatus.Error;
    }

    // ---- Missing session (DESIGN.md §9, "Old sessions") -----------------------------------------------------------

    /// <summary>
    /// A restored tab's transcript is gone from this machine and the session library (for example after Claude Code's
    /// 30-day cleanup): the tab waits for <b>Start a new session</b> rather than silently starting one.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRestart))]
    [NotifyCanExecuteChangedFor(nameof(RestartCommand))]
    public partial bool IsSessionMissing { get; private set; }

    public const string SessionMissingText =
        "The earlier conversation couldn't be found here or in the session library (Claude Code may have cleaned it up). You can start a new session in this folder.";

    /// <summary><b>Start a new session</b> in the same folder, keeping the tab's name, overrides and suffixes.</summary>
    [RelayCommand]
    private async Task StartNewSessionAsync()
    {
        IsSessionMissing = false;
        State.SessionId = null;
        State.SessionStartedAt = null;
        State.TranscriptPath = null;
        State.ForkOnNextStart = false;
        // The marks were for the old session's changes (DESIGN.md §8, "Reviewed").
        State.ReviewedFiles.Clear();
        _services.SaveState();
        await EnsureStartedAsync();
    }

    /// <summary><b>Choose folder…</b>: where the folder is now, or another clone of the same project.</summary>
    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        if (await _services.Platform.PickFolderAsync($"Where is {FolderName} now?") is { } folder)
        {
            await MoveToFolderAsync(folder);
        }
    }

    /// <summary>
    /// Moves the tab to <paramref name="folder"/> and starts it there, resuming its session from a working copy of the
    /// transcript: Claude Code finds sessions by folder, so a plain resume wouldn't find it from the new one.
    /// </summary>
    internal async Task MoveToFolderAsync(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }
        if (State.SessionId is { } sessionId && _services.Library.FindTranscript(sessionId, State.TranscriptPath) is { } transcript)
        {
            try
            {
                State.TranscriptPath = await SessionLibrary.CopyToWorkingFolderAsync(transcript, sessionId, _services.Paths.LocalSessionsDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _conversation.AddNote($"Couldn't copy the session to carry it on in the new folder: {ex.Message}", NoteKind.Error);
                return;
            }
        }
        _shell.MoveTabToFolder(this, folder);
        IsFolderMissing = false;
        Status = TabStatus.NotStarted;
        OnPropertyChanged(nameof(Folder));
        OnPropertyChanged(nameof(FolderName));
        OnPropertyChanged(nameof(DisplayName));
        _services.Usage?.OnTabRenamed(Id, DisplayName);
        OnPropertyChanged(nameof(InfoRows));
        ChangedFiles.OnFolderChanged();
        ProjectTools.ReloadCustomActions();
        _conversation.AddNote($"Now working in {State.Folder}.");
        await EnsureStartedAsync();
    }

    /// <summary><b>Unpin and close</b> (or <b>Close tab</b>) for a tab whose folder or session is gone. It's not working, so nothing to confirm.</summary>
    [RelayCommand]
    private void UnpinAndClose()
    {
        if (IsPinned)
        {
            TogglePin();
        }
        _shell.CloseTabCommand.Execute(this);
    }

    /// <summary>
    /// Loads a restored tab's earlier conversation from its transcript (DESIGN.md §9). Returns the session to resume,
    /// or null to start a new one when the transcript is gone (for example after Claude Code's 30-day cleanup).
    /// </summary>
    private async Task<string?> RestoreTranscriptAsync()
    {
        if (State.SessionId is not { } sessionId)
        {
            return null;
        }
        var path = _services.Library.FindTranscript(sessionId, State.TranscriptPath);
        if (path is null && _services.Library.Library.GetTranscriptPath(sessionId) is not null)
        {
            // Claude Code cleans up old transcripts; the library copy isn't affected (DESIGN.md §9, "Old sessions").
            try
            {
                path = State.TranscriptPath = await _services.Library.CopyToLocalAsync(sessionId);
            }
            catch (Exception)
            {
                path = null;
            }
        }
        if (path is null)
        {
            // The tab says so and waits: starting a new session is the user's choice (DESIGN.md §9, "Old sessions").
            IsSessionMissing = true;
            return null;
        }
        try
        {
            var transcript = await TranscriptReader.ReadAsync(path);
            State.SessionStartedAt ??= transcript.StartedAt;
            // The agent map shows the finished tree, with no live status (DESIGN.md §18).
            Agents.IsReplaying = true;
            foreach (var item in transcript.Items)
            {
                switch (item)
                {
                    case TranscriptTaskNotification notification:
                        Agents.OnTaskNotification(notification);
                        break;
                    case TranscriptPrompt prompt:
                        _conversation.ReplayUserMessage(prompt.Text, prompt.Images, prompt.Time);
                        break;
                    case TranscriptNote note:
                        _conversation.AddNote(note.Text);
                        break;
                    case TranscriptMessage { Message: AssistantMessage assistant }:
                        var assistantEvent = new AssistantMessageReceived(assistant);
                        _conversation.Replay(assistantEvent, item.Time);
                        ChangedFiles.Record(assistantEvent);
                        break;
                    case TranscriptMessage { Message: UserMessage results }:
                        var resultsEvent = new ToolResultsReceived(results);
                        _conversation.Replay(resultsEvent, item.Time);
                        ChangedFiles.Record(resultsEvent);
                        break;
                }
            }
            if (State.AutoName is null && transcript.Title is { } title)
            {
                State.AutoName = title;
                _titleRequested = true;
                NameChanged();
            }
            if (transcript.Items.Count > 0)
            {
                _titleRequested = true;
                _conversation.AddNote("Resumed.");
            }
        }
        catch (Exception ex)
        {
            _conversation.AddNote($"Couldn't read the earlier conversation: {ex.Message}", NoteKind.Warning);
        }
        finally
        {
            Agents.FinishReplay();
        }
        return sessionId;
    }

    /// <summary>Headless sessions don't get a title on their own, so ask after the first prompt (DESIGN.md §13).</summary>
    private async Task RequestTitleAsync()
    {
        if (_titleRequested || State.AutoName is not null || _session is null || _firstPrompt is null)
        {
            return;
        }
        _titleRequested = true;
        string? title = null;
        try
        {
            title = await _session.GenerateSessionTitleAsync(_firstPrompt);
        }
        catch (Exception)
        {
            // Fall back to the first line of the prompt.
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            title = Shorten(_firstPrompt.Split('\n')[0].Trim(), 40);
        }
        State.AutoName = title;
        NameChanged();
    }

    /// <summary>Reads events off the UI thread and applies them in batches, so fast streaming doesn't flood the UI.</summary>
    private async Task PumpAsync(ClaudeSession session)
    {
        var reader = session.Events;
        var batch = new List<SessionEvent>();
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            // Counted before reading, so the events are always either queued or in flight (see IsSettled).
            Interlocked.Increment(ref _batchesInFlight);
            while (reader.TryRead(out var sessionEvent))
            {
                batch.Add(sessionEvent);
            }
            var events = batch.ToArray();
            batch.Clear();
            _services.Dispatcher.Post(() =>
            {
                try
                {
                    Apply(session, events);
                }
                finally
                {
                    Interlocked.Decrement(ref _batchesInFlight);
                }
            });
        }
    }

    /// <summary>Batches of session events read but not applied yet.</summary>
    private int _batchesInFlight;

    /// <summary>The context usage asked for as the session started.</summary>
    private Task? _contextRefresh;

    /// <summary>
    /// For tests: the start has finished, every session event so far is applied, and the start's context usage request
    /// has finished. A tab shows Idle as soon as its session starts, while the start's own events (its state changes)
    /// are still queued and its context usage request hasn't gone out yet.
    /// </summary>
    internal bool IsSettled =>
        _starting is null or { IsCompleted: true }
        && Volatile.Read(ref _batchesInFlight) == 0
        && _session?.Events is not { CanCount: true, Count: > 0 }
        && _contextRefresh is null or { IsCompleted: true };

    private void Apply(ClaudeSession session, IReadOnlyList<SessionEvent> events)
    {
        if (!ReferenceEquals(session, _session))
        {
            return;
        }
        foreach (var sessionEvent in events)
        {
            try
            {
                ApplyEvent(session, sessionEvent);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // One event Claudette can't show must never take the app, and every tab's claude, down with it
                // (CLAUDE.md, "Parse tolerantly"). Say so once per kind of event, so a stream of them doesn't flood.
                _services.Loggers.CreateLogger("Tab").LogError(ex, "Couldn't apply a {Event} event.", sessionEvent.GetType().Name);
                if (_failedEvents.Add(sessionEvent.GetType().Name))
                {
                    _conversation.AddNote($"Claudette couldn't show part of this conversation ({sessionEvent.GetType().Name}: {ex.Message}). The session carries on.", NoteKind.Warning);
                }
            }
        }
    }

    /// <summary>The kinds of event that failed to apply, each noted once in the conversation.</summary>
    private readonly HashSet<string> _failedEvents = new(StringComparer.Ordinal);

    private void ApplyEvent(ClaudeSession session, SessionEvent sessionEvent)
    {
        if (RemoteControl.InterceptCommand(sessionEvent))
        {
            return;
        }
        _conversation.Apply(sessionEvent);
        ChangedFiles.Record(sessionEvent);
        TrackReplies(sessionEvent);
        ObserveForComposer(sessionEvent);
        OnPerforceSessionEvent(sessionEvent);
        TrackToolsForWorkingLine(sessionEvent);
        if (ApiTrouble.Reports(sessionEvent))
        {
            // Claude's status may explain it: check now rather than at the next poll (DESIGN.md §18, "Service status").
            _services.ServiceStatus.OnApiTrouble();
        }
        switch (sessionEvent)
        {
            case StateChanged { State: SessionState.Working }:
                _checkIns.TurnStarted();
                _autoContinue.TurnStarted();
                UpdateStatus();
                break;
            case StateChanged:
                UpdateStatus();
                break;
            case TurnStarted started:
                State.SessionId = started.Init.SessionId;
                _forkAwaitingId = false;
                if (started.Init.ClaudeCodeVersion is { } reported && Version.TryParse(reported, out var version))
                {
                    SetRunningVersion(version);
                }
                _modelId = started.Init.Model ?? _modelId;
                ModelName = ModelDisplayName(_modelId) ?? ModelName;
                PermissionMode = started.Init.PermissionMode ?? PermissionMode;
                _services.SaveState();
                break;
            case AssistantMessageReceived assistant:
                _checkIns.OutputSeen();
                if (_callUsage.Add(assistant.Message))
                {
                    OnCallUsage();
                }
                break;
            case TextDelta or ThinkingDelta or ToolResultsReceived:
                _checkIns.OutputSeen();
                break;
            case AutocompactStateChanged autocompact:
                _autocompact = autocompact.State;
                break;
            case PermissionRequested requested:
                _waitingOnUser.Add(requested.Request.RequestId);
                _checkIns.SetWaitingOnUser(true);
                WatchPermission(requested.Request);
                UpdateStatus();
                NotifyNeedsInput(requested.Request);
                break;
            case PermissionCancelled cancelled:
                PermissionResolved(cancelled.RequestId);
                break;
            case SystemNotice { Message.Subtype: "status" }:
                // Claude Code reports mode changes it makes itself, such as leaving plan mode.
                PermissionMode = session.PermissionMode ?? PermissionMode;
                break;
            case SystemNotice { Message.Subtype: "bridge_state" or "worker_shutting_down" } remote:
                RemoteControl.OnNotice(remote.Message);
                break;
            case RateLimitUpdated rateLimit:
                _services.Usage?.OnRateLimitEvent(rateLimit.Message);
                _autoContinue.RateLimit(rateLimit.Message.Info);
                break;
            case TurnCompleted completed:
                _checkIns.TurnEnded();
                _autoContinue.TurnEnded(completed.Result);
                State.SessionId = completed.Result.SessionId ?? State.SessionId;
                State.Tokens.Add(completed.Result);
                _callUsage.TurnEnded(completed.Result);
                _services.Usage?.OnTurnCompleted(Id, DisplayName, completed.Result);
                // Only a tab that syncs writes to the library (DESIGN.md §9, "Session library").
                CopyToLibrary();
                RefreshTokens();
                _services.SaveState();
                _ = RefreshContextUsageAsync(session);
                if (!IsSelected && !completed.Result.IsError)
                {
                    Status = TabStatus.Unread;
                }
                if (!completed.Result.IsError)
                {
                    NotifyTurnFinished(completed.Result);
                }
                // Claude may have edited claudette.json or switched branches: the actions and links follow.
                _ = ProjectTools.RefreshFileAsync();
                // The switch changed while Claude worked (DESIGN.md §18, "Remote Control").
                RemoteControl.OnTurnCompleted(session);
                break;
            case ConversationReset:
                _callUsage.ContextReset();
                State.SessionStartedAt = _services.Time.GetUtcNow();
                OnPropertyChanged(nameof(InfoRows));
                TodoList.Clear();
                State.AutoName = null;
                _titleRequested = false;
                _firstPrompt = null;
                NameChanged();
                break;
            case AuthenticationRequired:
                OnAuthenticationRequired();
                break;
            case SessionExited exited:
                _session = null;
                RemoteControl.Reset();
                SetRunningVersion(null);
                _checkIns.TurnEnded();
                _waitingOnUser.Clear();
                ErrorMessage = exited.Exit.ExitCode == 0 ? null : ExitErrorMessage(exited.Exit);
                Status = exited.Exit.ExitCode == 0 ? TabStatus.Exited : TabStatus.Error;
                OnPropertyChanged(nameof(CanRestart));
                _services.Notifications.ClearTab(Id, NotificationKind.NeedsInput);
                if (exited.Exit.ExitCode != 0)
                {
                    NotifyProcessError($"Claude Code stopped unexpectedly (exit code {exited.Exit.ExitCode}).");
                }
                break;
        }
    }

    private void WatchPermission(PermissionRequest request)
    {
        var item = Items.OfType<PromptItem>().LastOrDefault(p => ReferenceEquals(p.Request, request));
        if (item is not null)
        {
            item.Answered += (_, _) => PermissionResolved(request.RequestId);
        }
    }

    private void PermissionResolved(object key)
    {
        if (!_waitingOnUser.Remove(key))
        {
            return;
        }
        if (_waitingOnUser.Count == 0)
        {
            _checkIns.SetWaitingOnUser(false);
            _services.Notifications.ClearTab(Id, NotificationKind.NeedsInput);
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_session is null)
        {
            return;
        }
        Status = _waitingOnUser.Count > 0 ? TabStatus.NeedsInput
            : _session.State == SessionState.Working ? TabStatus.Working
            : Status == TabStatus.Unread ? TabStatus.Unread
            : TabStatus.Idle;
        OnPropertyChanged(nameof(CanRestart));
    }

    private async Task RefreshContextUsageAsync(ClaudeSession session)
    {
        ContextUsage usage;
        try
        {
            usage = await session.GetContextUsageAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Not offered by this Claude Code, or it failed: estimate from the last call instead.
            _services.Dispatcher.Post(() =>
            {
                if (ReferenceEquals(session, _session))
                {
                    _contextUsageUnavailable = true;
                    ShowEstimatedContext();
                }
            });
            return;
        }
        _services.Dispatcher.Post(() =>
        {
            if (!ReferenceEquals(session, _session))
            {
                return;
            }
            _contextUsageUnavailable = false;
            ContextText = $"Context {usage.Percentage:0}%";
            var compacts = usage is { AutoCompactEnabled: true, AutoCompactThreshold: { } threshold } ? $" · auto-compacts at {threshold:N0}" : "";
            ContextDetail = $"{usage.TotalTokens:N0} of {usage.MaxTokens:N0} tokens{compacts}";
            IsContextHigh = usage.AutoCompactThreshold is { } limit && usage.AutoCompactEnabled
                ? usage.TotalTokens >= limit * 0.9
                : usage.Percentage >= 80;
            ContextPercent = usage.Percentage;
            ContextBreakdown = ContextBreakdown.From(usage).KeepingExpanded(ContextBreakdown);
        });
    }

    /// <summary>The tab has its own settings: its menu marks <b>Tab settings…</b> with a dot (DESIGN.md §14).</summary>
    public bool HasOverrides => State.Overrides.HasAny;

    /// <summary>Applies changed per-tab overrides to the running session.</summary>
    public async Task ApplyOverridesAsync(TabOverrides previous)
    {
        OnPropertyChanged(nameof(HasOverrides));
        _services.SaveState();
        ProcessMonitor.UpdateSampler();
        _autoContinue.SettingsChanged();
        if (_session is null)
        {
            return;
        }
        try
        {
            var model = State.Overrides.Model ?? _services.Settings.NewTabs.DefaultModel;
            if (State.Overrides.Model != previous.Model)
            {
                await _session.SetModelAsync(model);
                _modelId = FindModel(model)?.ResolvedModel ?? model;
                ModelName = ModelDisplayName(_modelId) ?? model;
            }
            var effort = State.Overrides.Effort ?? _services.Settings.NewTabs.DefaultEffort;
            if (State.Overrides.Effort != previous.Effort)
            {
                await _session.SetEffortAsync(effort);
                Effort = effort;
            }
            if (State.Overrides.PermissionMode != previous.PermissionMode)
            {
                if ((State.Overrides.PermissionMode ?? _services.Settings.NewTabs.DefaultPermissionMode) is { } mode)
                {
                    await _session.SetPermissionModeAsync(mode);
                    PermissionMode = mode;
                }
                else if (_startingMode?.LaunchMode is { } launchMode && IsAutoModeAvailable)
                {
                    await SwitchModeQuietlyAsync(_session, launchMode);
                }
            }
        }
        catch (Exception ex)
        {
            _conversation.AddNote($"Couldn't apply the tab settings: {ex.Message}", NoteKind.Error);
        }
    }

    /// <param name="killProcesses">Also end every process the session started (DESIGN.md §4, "Cleanup").</param>
    private async Task StopSessionAsync(bool killProcesses = false)
    {
        var session = _session;
        _session = null;
        RemoteControl.Reset();
        SetRunningVersion(null);
        // Its events are no longer applied, so its exit won't clear them: its tasks end with it. On the UI thread, like
        // the events, since closing can finish elsewhere.
        _services.Dispatcher.Post(Tasks.Clear);
        if (session is null)
        {
            await ProcessMonitor.EndTreeAsync(killProcesses);
            return;
        }
        if (session.State == SessionState.Working)
        {
            // Interrupt first, so the turn ends cleanly with a result (DESIGN.md §13, "Shutdown").
            try
            {
                await session.InterruptAsync().WaitAsync(TimeSpan.FromSeconds(3), _services.Time);
            }
            catch (Exception)
            {
                // Best effort; the process is stopped next either way.
            }
        }
        if (killProcesses)
        {
            // Note the children while claude is still their parent; on macOS and Linux they can't be found afterwards.
            await Task.Run(ProcessMonitor.RunningChildProcesses);
        }
        // Let claude exit on its own, so it finishes its transcript; then end what it left running, before disposing
        // the session releases the process tree.
        await session.StopAsync(TimeSpan.FromSeconds(3));
        await ProcessMonitor.EndTreeAsync(killProcesses);
        await session.DisposeAsync();
        if (_pump is not null)
        {
            await _pump;
        }
    }

    /// <summary>Closing the tab. By default everything the tab started is stopped too; the user can keep it running.</summary>
    public async ValueTask CloseAsync(bool killProcesses)
    {
        _checkIns.Dispose();
        _autoContinue.Dispose();
        foreach (var timer in _copied.Values)
        {
            timer.Dispose();
        }
        _copied.Clear();
        StopAgentTicker();
        StopTaskTicker();
        StopCheckInTicker();
        // The session's last events aren't applied once it stops, so nothing else would end the turn's line, and its
        // timer would keep this tab alive.
        Working.Dispose();
        _services.Notifications.ClearTab(Id);
        StopPerforce();
        ProjectTools.CloseRuns(killProcesses);
        ChangedFiles.StopReviewSync();
        ReleaseLease();
        await StopSessionAsync(killProcesses);
        ChangedFiles.CleanUpDiffFiles();
    }

    public ValueTask DisposeAsync() => CloseAsync(killProcesses: true);

    // ---- Helpers --------------------------------------------------------------------------------------------

    private ModelInfo? FindModel(string? id)
    {
        if (id is null || _session?.Initialization?.Models is not { } models)
        {
            return null;
        }
        static string Base(string m) => m.Split('[')[0];
        return models.FirstOrDefault(m => m.Value == id)
            ?? models.Where(m => m.Value != "default").FirstOrDefault(m => m.ResolvedModel == id)
            ?? models.Where(m => m.Value != "default").FirstOrDefault(m => m.ResolvedModel is { } r && Base(r) == Base(id));
    }

    /// <summary>"Opus (1M context)" for <c>claude-opus-5-5[1m]</c>, or the id itself if Claude Code didn't list it.</summary>
    private string? ModelDisplayName(string? id) => id is null ? null : FindModel(id)?.DisplayName ?? id;

    /// <summary>Cuts text to about <paramref name="max"/> characters, at a word boundary where there is one.</summary>
    private static string Shorten(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }
        var cut = text[..max];
        var space = cut.LastIndexOf(' ');
        return (space > max / 2 ? cut[..space] : cut[..(max - 1)]).TrimEnd() + "…";
    }

    private static string ShortModel(string? name) => name?.Split(' ', '(')[0] is { Length: > 0 } s ? s : "Default";

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
