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
    /// <summary>Cancelled when the tab closes: a start under way stops there, and no new one begins.</summary>
    private readonly CancellationTokenSource _closing = new();
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
        TodoList.Time = services.Time;
        ProcessMonitor = new ProcessMonitorViewModel(services, this);
        ChangedFiles = new ChangedFilesViewModel(services, this);
        McpServers = new McpServersViewModel(() => _session, url => _services.Platform.OpenUrlAsync(url));
        ProjectTools = new ProjectToolsViewModel(services, this);
        RemoteControl = new RemoteControlViewModel(services, this);
        Perforce = new PerforceViewModel(services, this);
        Context = new ContextViewModel(services, this);
        Agents = new AgentMap(services.Time, ModelDisplayName);
        Agents.Changed += OnAgentsChanged;
        Tasks = new RunningTasks(services.Time, Agents);
        Tasks.Changed += OnTasksChanged;
        Find = new ConversationSearch(Items);
        Find.CurrentChanged += OnFindCurrentChanged;
        _conversation = new ConversationBuilder(Items, TodoList, ModelDisplayName)
        {
            ExpandThinking = services.Settings.Appearance.ExpandThinking,
            ShowUnsupportedMessages = services.Settings.Advanced.LogProtocol,
            ShowAllHookRuns = services.Settings.ClaudeCode.ShowAllHookRuns,
            OpenUrl = services.Platform.OpenUrlAsync,
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
        Context.RefreshTokens();
        RestoreLimitWait();
        // Project tools (DESIGN.md §18): the project and the folder's own actions and links, as soon as they're read.
        _ = ProjectTools.RefreshAsync();
    }

    /// <summary>What's saved for this tab.</summary>
    public TabState State { get; }

    public string Id => State.Id;

    public string Folder => State.Folder;

    public string FolderName => Path.GetFileName(Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name ? name : Folder;

    public BatchedCollection<ConversationItem> Items { get; } = [];

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

    /// <summary>The session's MCP servers, for the side panel's MCP page (DESIGN.md §4, "MCP servers").</summary>
    public McpServersViewModel McpServers { get; }

    /// <summary>The session has MCP servers, as its last <c>system/init</c> listed: the side panel offers the MCP page.</summary>
    [ObservableProperty]
    public partial bool HasMcpServers { get; private set; }

    /// <summary>The files Claude changed in this session, and their diffs (DESIGN.md §8).</summary>
    public ChangedFilesViewModel ChangedFiles { get; }

    /// <summary>The folder's project, its actions and links, and the runs of its jobs (DESIGN.md §18, "Project tools").</summary>
    public ProjectToolsViewModel ProjectTools { get; }

    /// <summary>The tab's connection to the Claude app (DESIGN.md §18, "Remote Control").</summary>
    public RemoteControlViewModel RemoteControl { get; }

    /// <summary>Perforce ticket handling and the changelist in the tab title (DESIGN.md §18).</summary>
    public PerforceViewModel Perforce { get; }

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

    /// <summary>Claude Code is in a turn: working, or waiting on the user within it.</summary>
    public bool IsInTurn => _session?.State == SessionState.Working;

    /// <summary>The next start carries on the turn Claudette's restart cut off (DESIGN.md §9, "Working on Claudette").</summary>
    private bool _carryOnInterruptedTurn;

    /// <summary>
    /// Starts the next session with <c>CLAUDE_CODE_RESUME_INTERRUPTED_TURN</c>, so Claude Code carries on the turn the
    /// restart cut off, if the transcript ends in it and is no older than a restart's snapshot can be.
    /// </summary>
    internal void CarryOnInterruptedTurn() => _carryOnInterruptedTurn = true;

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
            AddWorktreeRows(rows);
            rows.Add(new InfoRow("Model", $"{ModelName ?? "Default"} · {EffortName}"));
            if (PermissionMode is not null)
            {
                rows.Add(new InfoRow("Mode", PermissionModeName));
            }
            if (State.SessionStartedAt is { } started)
            {
                rows.Add(new InfoRow("Started", started.ToLocalTime().ToString("g")));
            }
            rows.Add(new InfoRow("Tokens", $"{Context.TokensShort} · {State.Tokens.Turns} turns"));
            if (RunningVersion is { } running)
            {
                // DESIGN.md §12: open tabs keep the version they started with.
                rows.Add(_services.InstalledClaudeVersion is { } installed && installed > running
                    ? new InfoRow("Claude Code", $"Running {running}; {installed} is installed. New tabs use {installed}.")
                    : new InfoRow("Claude Code", running.ToString()));
            }
            if (Context.Detail is { } context)
            {
                rows.Add(new InfoRow("Context", $"{Context.Text} ({context})"));
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
            Perforce.AddInfoRows(rows);
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
        ProjectTools.Runs.UpdateShownRun();
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

    public string EffortName => (Effort is null ? "Default effort" : Capitalize(Effort)) + (IsUltracode ? " · Ultracode" : "");

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

    /// <summary>Bypass asks first (DESIGN.md §7).</summary>
    public InlineConfirmation BypassConfirmation => field ??= new(() => SetModeAsync(PermissionModeInfo.Bypass));

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
            BypassConfirmation.Ask();
            return;
        }
        await SetModeAsync(choice.Value);
    }

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

    /// <summary>The oldest MCP server's request for input still waiting, when no prompt is (DESIGN.md §7).</summary>
    private McpInputItem? WaitingInput => Items.OfType<McpInputItem>().FirstOrDefault(i => i.IsPending);

    /// <summary>A prompt or an MCP server's request waits: the keyboard's allow and deny shortcuts answer it.</summary>
    public bool HasKeyboardAnswer => WaitingPrompt is not null || WaitingInput is not null;

    public bool AcceptWaitingPrompt()
    {
        if (WaitingPrompt is { } prompt)
        {
            return prompt.TryAcceptFromKeyboard();
        }
        // A form sends what's filled in; a link has to be opened first, so there's nothing to accept yet.
        if (WaitingInput is { IsForm: true } input)
        {
            input.SendCommand.Execute(null);
            return true;
        }
        return false;
    }

    public bool DeclineWaitingPrompt()
    {
        if (WaitingPrompt is { } prompt)
        {
            return prompt.TryDeclineFromKeyboard();
        }
        if (WaitingInput is { } input)
        {
            input.DeclineCommand.Execute(null);
            return true;
        }
        return false;
    }

    /// <summary>A model change asks first (DESIGN.md §5: switching drops the prompt cache).</summary>
    public InlineConfirmation<ModelInfo> ModelSwitch => field ??= new(SwitchModelAsync);

    /// <summary>What the model switch's confirmation says.</summary>
    [ObservableProperty]
    public partial string ModelSwitchMessage { get; private set; } = "";

    [RelayCommand]
    private void ChooseModel(ModelInfo? model)
    {
        if (model is null || model == CurrentModelInfo)
        {
            return;
        }
        var message = $"Switch to {model.DisplayName}? Switching models resets this tab's cached context. The new model has to re-read the whole conversation, so your next message will use more of your limits.";
        if (Effort is { } effort && !model.SupportedEffortLevels.Contains(effort))
        {
            message += $" {model.DisplayName} doesn't support {effort} effort, so effort goes back to the model's default.";
        }
        ModelSwitchMessage = message;
        ModelSwitch.Ask(model);
    }

    private async Task SwitchModelAsync(ModelInfo model)
    {
        if (_session is null)
        {
            return;
        }
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

    // ---- Context and tokens (DESIGN.md §4, §6): in ContextViewModel ------------------------------------------------

    /// <summary>The context indicator and ring, what fills the context window, and the token counts.</summary>
    public ContextViewModel Context { get; }

    /// <summary>Summarizes the conversation to free context, like <c>/compact</c> in the terminal (DESIGN.md §6).</summary>
    [RelayCommand(CanExecute = nameof(CanCompact))]
    private async Task CompactAsync()
    {
        _conversation.AddNote("Compacting the conversation…");
        var stamp = NewStamp(fromUser: true);
        _conversation.SentWithoutCard(stamp.Uuid);
        await SendRawAsync("/compact", stamp: stamp);
    }

    private bool CanCompact() => Status is TabStatus.Idle or TabStatus.Unread;

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
    private Task EditSuffixesAsync() => _shell.OpenSettingsAtAsync(SettingsCategory.QuickSuffixes);

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
        _conversation.ShowAllHookRuns = _services.Settings.ClaudeCode.ShowAllHookRuns;
        _conversation.ShowUnsupportedMessages = _services.Settings.Advanced.LogProtocol;
        Context.OnSettingsChanged();
        ProcessMonitor.UpdateSampler();
        _autoContinue.SettingsChanged();
        Perforce.OnSettingsChanged();
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
        var stamp = NewStamp(fromUser: true);
        var card = _conversation.AddUserMessage(text, suffixText, images: images);
        card.SentId = stamp.Uuid;
        // Sent while Claude works, it waits its turn (DESIGN.md §5, "Queued messages").
        card.IsQueued = IsWorking;
        _recall.Add(text);
        _firstPrompt ??= text.Length > 0 ? text : suffixText;
        await SendRawAsync(text, images, suffixText, stamp);
        _ = RequestTitleAsync();
    }

    private bool CanSend() => !IsReadOnly && (Status is not (TabStatus.Starting or TabStatus.Error) || IsWaitingForSignIn) && (ComposerText.Trim().Length > 0 || Chips.Count > 0 || Attachments.Count > 0);

    /// <summary>A new id for a message, and whether the user typed or chose it (DESIGN.md §13, "Wire format").</summary>
    private static MessageStamp NewStamp(bool fromUser) => new(Guid.NewGuid().ToString(), fromUser);

    /// <param name="suffix">Quick suffixes, which Claude Code gets after the message, or beside a slash command (DESIGN.md §5).</param>
    /// <param name="stamp">The message's id, and whether the user typed it. Without one it's Claudette's own, with no card.</param>
    private async Task SendRawAsync(string message, IReadOnlyList<MessageImage>? images = null, string? suffix = null, MessageStamp? stamp = null)
    {
        if (stamp is null)
        {
            stamp = NewStamp(fromUser: false);
            _conversation.SentWithoutCard(stamp.Uuid);
        }
        var pending = new PendingMessage(message, images ?? [], suffix, stamp);
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
                if (IsAskingTrust)
                {
                    // It waits for the answer about the folder (DESIGN.md §7, "Folder trust").
                    _heldForTrust.Add(pending);
                    return;
                }
                // It couldn't start because Claude Code needs a sign-in: the message waits for it.
                HoldForSignIn(pending);
                return;
            }
            _awaitingReply.Add(pending);
            await _session.SendUserMessageAsync(message, pending.Images, suffix, stamp);
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
            // What was waiting its turn is cancelled with it, and comes back to the composer (DESIGN.md §5).
            var receipt = await _session.InterruptAsync(cancelQueued: true);
            TakeBack(receipt.Cancelled);
        }
        catch (Exception ex)
        {
            _conversation.AddNote($"Couldn't stop Claude: {ex.Message}", NoteKind.Error);
        }
    }

    /// <summary>
    /// <b>Cancel</b> on a message that waits its turn: Claude Code takes it back, and its text and images return to the
    /// composer. One Claude Code has already taken stays as it is.
    /// </summary>
    [RelayCommand]
    private async Task CancelQueuedMessageAsync(UserMessageItem? message)
    {
        if (message is not { IsQueued: true, SentId: { } id } || _session is null)
        {
            return;
        }
        if (await _session.CancelQueuedMessageAsync(id))
        {
            TakeBack([id]);
        }
        else if (message.IsQueued)
        {
            _conversation.AddNote("Couldn't take that message back: Claude Code has it already, or can't cancel one message on its own.", NoteKind.Warning);
        }
    }

    /// <summary>
    /// Messages Claude Code cancelled before they ran: their cards go, and what the user wrote goes back to the
    /// composer, ahead of anything typed since. Claudette's own (a check-in) just go. Ids Claudette didn't send are
    /// ignored.
    /// </summary>
    private void TakeBack(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }
        var cards = Items.OfType<UserMessageItem>().Where(m => m.SentId is { } id && ids.Contains(id)).ToList();
        var texts = new List<string>();
        foreach (var card in cards)
        {
            _conversation.Remove(card);
            _awaitingReply.RemoveAll(p => p.Stamp?.Uuid == card.SentId);
            if (card.IsCheckIn || card.IsAutoContinue)
            {
                continue;
            }
            if (card.CopyText.Trim() is { Length: > 0 } text)
            {
                texts.Add(text);
            }
            foreach (var image in card.Images)
            {
                AddImage(image.Data, "Attached image");
            }
        }
        if (texts.Count > 0)
        {
            ComposerText = string.Join("\n\n", ComposerText.Trim() is { Length: > 0 } typed ? [.. texts, typed] : texts);
        }
        if (cards.Count > 0)
        {
            _conversation.AddNote(cards.Count == 1 ? "Took back a message that was waiting its turn." : $"Took back {cards.Count} messages that were waiting their turn.");
        }
    }

    /// <summary>Opens a link clicked in a reply.</summary>
    [RelayCommand]
    private Task OpenLinkAsync(object? link) =>
        link?.ToString() is { Length: > 0 } url ? _services.Platform.OpenUrlAsync(url) : Task.CompletedTask;

    // ---- Copy and times (DESIGN.md §5, "Copy and times") ---------------------------------------------------

    /// <summary>How long a Copy button says "Copied", here and in Settings.</summary>
    public static readonly TimeSpan CopiedFor = TimeSpan.FromSeconds(1.5);

    /// <summary>What says "Copied" now, and the timeout that puts it back.</summary>
    private readonly Dictionary<object, UiTimeout> _copied = [];

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
        if (!_copied.TryGetValue(target, out var timeout))
        {
            _copied[target] = timeout = new UiTimeout(_services.Time, _services.Dispatcher);
        }
        show(true);
        timeout.Restart(CopiedFor, () =>
        {
            _copied.Remove(target);
            show(false);
        });
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
        var stamp = NewStamp(fromUser: false);
        var card = _conversation.AddUserMessage(message, isCheckIn: true);
        card.SentId = stamp.Uuid;
        card.IsQueued = true;
        _ = SendRawAsync(message, stamp: stamp);
        NotifyCheckIn();
    });

    /// <summary>The check-in bar's countdown ticks every second.</summary>
    private UiTicker CheckInTicker => field ??= new(_services.Time, _services.Dispatcher, TimeSpan.FromSeconds(1), () => OnPropertyChanged(nameof(CheckInCountdownText)));

    /// <summary>A check-in counting down to being sent, shown in a bar over the composer; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCheckInCountdown), nameof(CheckInCountdownText))]
    public partial CheckInCountdown? CheckInCountdown { get; private set; }

    public bool HasCheckInCountdown => CheckInCountdown is not null;

    /// <summary>What the bar says, counting down each second.</summary>
    public string? CheckInCountdownText => CheckInCountdown is { } countdown
        ? $"Checking in with Claude in {Math.Max(0, (int)Math.Ceiling((countdown.SendsAt - _services.Time.GetUtcNow()).TotalSeconds))} s."
        : null;

    partial void OnCheckInCountdownChanged(CheckInCountdown? value) => CheckInTicker.Run(value is not null);

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
        if (_closing.IsCancellationRequested || _session is not null || IsSessionMissing || Status == TabStatus.Error && (!Directory.Exists(Folder) || WaitsForSignIn))
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
        // Claude Code runs a folder's own configuration without asking in a session like this one: Claudette asks first.
        if (_closing.IsCancellationRequested || await AskTrustAsync())
        {
            return;
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
            var settingsFolder = State.WithoutProjectSettings ? null : folder;
            var starting = _startingMode = await Task.Run(() => _services.ReadStartingPermissionMode(settingsFolder));
            var environment = new Dictionary<string, string?>(_services.RemoteControl.ClaudeVariables);
            if (_carryOnInterruptedTurn && resume is not null)
            {
                // Only after Claudette's own restart, and only once: Claude Code re-runs the turn without the user.
                environment["CLAUDE_CODE_RESUME_INTERRUPTED_TURN"] = "1";
                environment["CLAUDE_CODE_RESUME_INTERRUPTED_TURN_MAX_AGE_MS"] = ((long)RestartSnapshot.MaxAge.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
            }
            _carryOnInterruptedTurn = false;
            if (settings.ClaudeCode.KeepFileCheckpoints)
            {
                // Copies of files before Claude changes them, so a prompt's changes can be put back (DESIGN.md §5).
                environment["CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING"] = "true";
            }
            var resumeAt = resume is null ? null : State.ResumeAt;
            var options = await Perforce.WithPerforceAsync(await ProjectTools.WithNoteAsync(new ClaudeLaunchOptions
            {
                WorkingDirectory = Folder,
                Resume = resume,
                ForkSession = fork,
                ResumeSessionAt = resumeAt,
                ResumeDropsTurn = resumeAt is null ? null : State.ResumeDropsTurn,
                // Each prompt comes back with its id, the point to rewind or branch from; hook runs come as messages.
                ReplayUserMessages = true,
                IncludeHookEvents = true,
                // MCP servers' requests for input are cards in the conversation, including ones as the servers connect.
                ShowsElicitations = true,
                // Chosen when asked about the folder's own configuration (DESIGN.md §7, "Folder trust").
                SettingSources = State.WithoutProjectSettings ? "user" : null,
                FallbackModel = settings.ClaudeCode.FallbackModel,
                AgentProgressSummaries = settings.ClaudeCode.SubagentProgressSummaries,
                // A worktree tab's first start makes its worktree, or opens it again (DESIGN.md §4, "Worktree tabs").
                Worktree = State.NewWorktree,
                AddDirectories = [.. State.ExtraFolders.Where(Directory.Exists)],
                Model = State.Overrides.Model ?? settings.NewTabs.DefaultModel,
                Effort = State.Overrides.Effort ?? settings.NewTabs.DefaultEffort,
                PermissionMode = chosenMode ?? (resume is null ? starting.LaunchMode : null),
                AdditionalArguments = settings.Advanced.ExtraArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                ProtocolLogPath = _services.ProtocolLogPath(FolderName),
                // The presence file, among others: no pushes to the phone while Claudette is in front (DESIGN.md §10).
                EnvironmentOverrides = environment,
            }));
            // Closed while the start was under way: nothing is started for a tab that's gone.
            _closing.Token.ThrowIfCancellationRequested();
            var session = await sessions.StartAsync(options, _closing.Token);
            if (_closing.IsCancellationRequested)
            {
                await session.DisposeAsync();
                return;
            }
            _session = session;
            if (!fork)
            {
                State.ResumeAt = null;
                State.ResumeDropsTurn = null;
            }
            // A new claude has none of the old one's tasks.
            Tasks.Clear();
            _ = LoadSpinnerVerbsAsync();
            _services.RememberModels(session.Initialization?.Models);
            State.SessionStartedAt ??= _services.Time.GetUtcNow();
            // The installed version is what just started; system/init confirms it with the first turn.
            SetRunningVersion(_services.InstalledClaudeVersion);
            // The copy gets a new session id with its first turn. Until then it's still the original's, so the fork and
            // its point stay saved: started again before that, it's a copy again, not the original.
            _forkAwaitingId = fork;
            if (fork)
            {
                _conversation.AddNote(_forkNote ?? "Opened as a copy. The original session is left as it was.");
                _forkNote = null;
            }
            ProcessMonitor.AttachTree(session);
            Effort = State.Overrides.Effort ?? settings.NewTabs.DefaultEffort;
            PermissionMode = session.PermissionMode;
            _modelId = session.Model ?? State.Overrides.Model;
            ModelName = ModelDisplayName(_modelId) ?? CurrentModelInfo?.DisplayName;
            OnPropertyChanged(nameof(Models));
            OnPropertyChanged(nameof(EffortLevels));
            OnPropertyChanged(nameof(PermissionModeChoices));
            if (State.Ultracode)
            {
                await ApplyUltracodeAsync(session, on: true);
            }
            UpdateOutputStyles(session.Initialization);
            if (chosenMode is null && resume is not null && starting.LaunchMode is { } launchMode
                && session.PermissionMode == PermissionModeInfo.Manual && IsAutoModeAvailable)
            {
                await SwitchModeQuietlyAsync(session, launchMode);
            }
            Status = TabStatus.Idle;
            _pump = PumpAsync(session);
            _contextRefresh = Context.RefreshUsageAsync(session);
            // Before any prompt goes out, so the phone sees the whole turn (DESIGN.md §18, "Remote Control").
            RemoteControl.ConnectOnStart(session);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            // Closed: the start's claude, if there was one, is stopped already.
        }
        catch (Exception ex) when (Core.Auth.SignInErrors.IsSignInFailure(ex))
        {
            OnStartFailedForSignIn(ex);
        }
        catch (ClaudeSessionExitedException exited) when (exited.StartupFailure is { } failure)
        {
            // Claude Code said why it refused to start (DESIGN.md §4, "Why it couldn't start").
            ErrorMessage = failure.Summary;
            _conversation.AddNote($"Claude Code couldn't start: {failure.Message}", NoteKind.Error);
            NotifyProcessError($"Claude Code couldn't start: {failure.Summary}");
            if (failure.Fix == StartupFailureFix.ChooseFolder)
            {
                MarkFolderMissing();
            }
            else
            {
                Status = TabStatus.Error;
            }
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
        OnPropertyChanged(nameof(IsInWorktree));
        OnPropertyChanged(nameof(WorktreeName));
        OnPropertyChanged(nameof(WorktreeTip));
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
            // Up to the point a rewind or branch goes back to, which the resume keeps (DESIGN.md §5, "Rewind and branch").
            var transcript = await TranscriptReader.ReadAsync(path, State.ResumeAt);
            State.SessionStartedAt ??= transcript.StartedAt;
            // The agent map shows the finished tree, with no live status (DESIGN.md §18).
            Agents.IsReplaying = true;
            // Read again after going back (DESIGN.md §5, "Rewind and branch"), the prompts are already there to recall.
            var recallPrompts = _recall.Count == 0;
            // The view hears of the earlier conversation once, as a whole, rather than once per item.
            using var deferred = Items.DeferNotifications();
            foreach (var item in transcript.Items)
            {
                switch (item)
                {
                    case TranscriptTaskNotification notification:
                        Agents.OnTaskNotification(notification);
                        break;
                    case TranscriptPrompt prompt:
                        _conversation.ReplayUserMessage(prompt.Text, prompt.Images, prompt.Time, prompt.Uuid, prompt.ParentUuid);
                        if (recallPrompts)
                        {
                            _recall.Add(prompt.Text);
                        }
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
                if (_forkNote is null)
                {
                    _conversation.AddNote("Resumed.");
                }
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
        Perforce.OnSessionEvent(sessionEvent);
        Context.OnSessionEvent(session, sessionEvent);
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
                HasMcpServers = started.Init.Raw.GetArray("mcp_servers") is { Count: > 0 };
                State.SessionId = started.Init.SessionId;
                OnWorkingFolderReported(started.Init.Cwd);
                if (_forkAwaitingId)
                {
                    // The copy has its own id now and no longer writes to the original.
                    _forkAwaitingId = false;
                    State.ForkOnNextStart = false;
                    State.ResumeAt = null;
                    State.ResumeDropsTurn = null;
                    State.TranscriptPath = null;
                }
                if (started.Init.ClaudeCodeVersion is { } reported && Version.TryParse(reported, out var version))
                {
                    SetRunningVersion(version);
                }
                _modelId = started.Init.Model ?? _modelId;
                ModelName = ModelDisplayName(_modelId) ?? ModelName;
                PermissionMode = started.Init.PermissionMode ?? PermissionMode;
                _services.SaveState();
                break;
            case AssistantMessageReceived or TextDelta or ThinkingDelta or ToolResultsReceived:
                _checkIns.OutputSeen();
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
            case ElicitationRequested elicitation:
                // An MCP server asks for input: the tab waits on the user as it does for a permission (DESIGN.md §7).
                _waitingOnUser.Add(elicitation.Request.RequestId);
                _checkIns.SetWaitingOnUser(true);
                if (Items.OfType<McpInputItem>().LastOrDefault(i => ReferenceEquals(i.Request, elicitation.Request)) is { } input)
                {
                    input.Answered += (_, _) => PermissionResolved(elicitation.Request.RequestId);
                }
                UpdateStatus();
                _services.Notifications.Notify(NotificationKind.NeedsInput, DisplayName, $"{elicitation.Request.ServerName} asks: {Shorten(elicitation.Request.Message, 120)}", Id);
                break;
            case ElicitationCancelled withdrawn:
                PermissionResolved(withdrawn.RequestId);
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
                _services.Usage?.OnTurnCompleted(Id, DisplayName, completed.Result, Folder);
                // Only a tab that syncs writes to the library (DESIGN.md §9, "Session library").
                CopyToLibrary();
                _services.SaveState();
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
                if (_restartForExtraFolders)
                {
                    // After this event is handled: the restart stops the session it came from.
                    _services.Dispatcher.Post(() => _ = RestartForExtraFoldersAsync());
                }
                break;
            case ConversationReset:
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
            // Answered, withdrawn or cancelled with the session: the others are numbered again.
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PromptItem.State))
                {
                    NumberWaitingPrompts();
                }
            };
        }
        NumberWaitingPrompts();
    }

    /// <summary>The prompts waiting in the tab, oldest first (DESIGN.md §7, "Several prompts waiting").</summary>
    private List<PromptItem> WaitingPrompts() => [.. Items.OfType<PromptItem>().Where(p => p.IsPending)];

    /// <summary>Numbers the waiting prompts, so each card says where it stands: "Prompt 2 of 5".</summary>
    private void NumberWaitingPrompts()
    {
        var waiting = WaitingPrompts();
        for (var i = 0; i < waiting.Count; i++)
        {
            waiting[i].Position = i + 1;
            waiting[i].WaitingCount = waiting.Count;
        }
    }

    /// <summary>Goes to the waiting prompt before this one, or the newest from the oldest.</summary>
    [RelayCommand]
    private void ShowPreviousWaitingPrompt(PromptItem? prompt) => ShowWaitingPrompt(prompt, -1);

    /// <summary>Goes to the waiting prompt after this one, or the oldest from the newest.</summary>
    [RelayCommand]
    private void ShowNextWaitingPrompt(PromptItem? prompt) => ShowWaitingPrompt(prompt, 1);

    private void ShowWaitingPrompt(PromptItem? from, int step)
    {
        var waiting = WaitingPrompts();
        if (from is null || waiting.IndexOf(from) is not (>= 0 and var index) || waiting.Count < 2)
        {
            return;
        }
        ScrollToRequested?.Invoke(waiting[(index + step + waiting.Count) % waiting.Count]);
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
    /// <param name="interruptTurn">
    /// Interrupt a turn first, so it ends cleanly. A restart of Claudette leaves it as it is instead, for Claude Code to
    /// carry on afterwards.
    /// </param>
    private async Task StopSessionAsync(bool killProcesses = false, bool interruptTurn = true)
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
        if (session.State == SessionState.Working && interruptTurn)
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
    /// <param name="interruptTurn">Interrupt a turn under way; a restart of Claudette leaves it for Claude Code to carry on.</param>
    public async ValueTask CloseAsync(bool killProcesses, bool interruptTurn = true)
    {
        await _closing.CancelAsync();
        _checkIns.Dispose();
        _autoContinue.Dispose();
        foreach (var timer in _copied.Values)
        {
            timer.Dispose();
        }
        _copied.Clear();
        AgentTicker.Stop();
        TaskTicker.Stop();
        CheckInTicker.Stop();
        // The session's last events aren't applied once it stops, so nothing else would end the turn's line, and its
        // timer would keep this tab alive.
        Working.Dispose();
        _services.Notifications.ClearTab(Id);
        Perforce.Stop();
        ProjectTools.Runs.CloseRuns(killProcesses);
        ChangedFiles.StopReviewSync();
        if (_starting is { } starting)
        {
            // A start under way stops at the cancellation, or finishes: either way, what it started is stopped next.
            try
            {
                await starting;
            }
            catch (Exception)
            {
                // It reported its own failure.
            }
        }
        ReleaseLease();
        await StopSessionAsync(killProcesses, interruptTurn);
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
