using System.Collections.ObjectModel;
using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.Core.Git;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;
using Claudette.Core.Transcripts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
    public static readonly IReadOnlyList<string> PermissionModes = ["default", "acceptEdits", "plan", "bypassPermissions"];

    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private readonly ConversationBuilder _conversation;
    private readonly CheckInMonitor _checkIns;
    private ClaudeSession? _session;
    private Task? _pump;
    private Task? _starting;
    private bool _restoredTranscript;
    private bool _restartAfterSignIn;
    private int _pendingPermissions;
    private string? _firstPrompt;
    private bool _titleRequested;

    public TabViewModel(AppServices services, ShellViewModel shell, TabState state, bool isRestored)
    {
        _services = services;
        _shell = shell;
        State = state;
        _conversation = new ConversationBuilder(Items, TodoList, ModelDisplayName) { ExpandThinking = services.Settings.Appearance.ExpandThinking };
        _checkIns = new CheckInMonitor(services.Time, () => CheckInSettings, SendCheckInFromTimer, stuck => _services.Dispatcher.Post(() => IsPossiblyStuck = stuck));
        Status = TabStatus.NotStarted;
        _restoredTranscript = !isRestored;
        foreach (var id in state.KeptSuffixes)
        {
            if (services.Settings.QuickSuffixes.FirstOrDefault(s => s.Id == id) is { } kept)
            {
                Chips.Add(new SuffixChip(kept, isKept: true));
            }
        }
        RefreshTokens();
    }

    /// <summary>What's saved for this tab.</summary>
    public TabState State { get; }

    public string Id => State.Id;

    public string Folder => State.Folder;

    public string FolderName => Path.GetFileName(Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name ? name : Folder;

    public ObservableCollection<ConversationItem> Items { get; } = [];

    public TodoList TodoList { get; } = new();

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
        _shell.OnPinChanged(this);
    }

    // ---- Status (DESIGN.md §4, "Status icon") ------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusGlyph), nameof(StatusTip), nameof(InfoRows), nameof(IsWorking), nameof(NeedsInput), nameof(IsBusyStatus), nameof(IsAlertStatus), nameof(IsErrorStatus), nameof(IsUnread))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand), nameof(SendCommand), nameof(RestartCommand))]
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
        TabStatus.Working => "Working",
        TabStatus.NeedsInput => "Needs your input",
        TabStatus.Unread => "Finished while in the background",
        TabStatus.Error => "Claude Code stopped with an error",
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
            if (PermissionMode is { } mode)
            {
                rows.Add(new InfoRow("Mode", mode));
            }
            if (_sessionStartedAt is { } started)
            {
                rows.Add(new InfoRow("Started", started.ToLocalTime().ToString("g")));
            }
            rows.Add(new InfoRow("Tokens", $"{TokensShort} · {State.Tokens.Turns} turns"));
            if (ContextDetail is { } context)
            {
                rows.Add(new InfoRow("Context", $"{ContextText} ({context})"));
            }
            rows.Add(new InfoRow("Status", StatusTip));
            return rows;
        }
    }

    private DateTimeOffset? _sessionStartedAt;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            if (Status == TabStatus.Unread)
            {
                Status = TabStatus.Idle;
            }
            _ = EnsureStartedAsync();
        }
    }

    [ObservableProperty]
    public partial bool IsPossiblyStuck { get; set; }

    // ---- Model, effort and mode (DESIGN.md §5, "Model & effort") ------------------------------------------

    public IReadOnlyList<ModelInfo> Models => _session?.Initialization?.Models.Where(m => m.Value != "default").ToArray() ?? [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelBadge), nameof(InfoRows), nameof(EffortLevels))]
    public partial string? ModelName { get; set; }

    /// <summary>The model id Claude Code reports (for example <c>claude-opus-5-5[1m]</c>).</summary>
    private string? _modelId;

    /// <summary>The effort level in use. Null means the model's default; Claude Code doesn't report it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffortName), nameof(ModelBadge), nameof(InfoRows))]
    public partial string? Effort { get; set; }

    public string EffortName => Effort is null ? "Default effort" : Capitalize(Effort);

    public string ModelBadge => $"{ShortModel(ModelName)} · {(Effort is null ? "Default" : Capitalize(Effort))}";

    public IReadOnlyList<string> EffortLevels => CurrentModelInfo?.SupportedEffortLevels ?? [];

    private ModelInfo? CurrentModelInfo => FindModel(_modelId) ?? _session?.Initialization?.Models.FirstOrDefault(m => m.Value == "default");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InfoRows))]
    public partial string? PermissionMode { get; set; }

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
            _services.SaveState();
        }
        catch (Exception ex)
        {
            _conversation.AddNote($"Couldn't change effort: {ex.Message}", NoteKind.Error);
        }
    }

    // ---- Context and tokens (DESIGN.md §4, §6) -----------------------------------------------------------

    [ObservableProperty]
    public partial string? ContextText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InfoRows))]
    public partial string? ContextDetail { get; set; }

    [ObservableProperty]
    public partial bool IsContextHigh { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InfoRows))]
    public partial string TokensShort { get; set; } = "0 tok";

    public ObservableCollection<TokenRow> TokenRows { get; } = [];

    [ObservableProperty]
    public partial string TokenSummary { get; set; } = "";

    private void RefreshTokens()
    {
        var totals = State.Tokens;
        TokensShort = TokenTotals.Short(totals.Total);
        TokenRows.Clear();
        foreach (var (model, t) in totals.Models.OrderByDescending(m => m.Value.Total))
        {
            TokenRows.Add(new TokenRow(ModelDisplayName(model) ?? model, N(t.Input), N(t.Output), N(t.CacheWrite), N(t.CacheRead), $"${t.EstimatedCostUsd:0.00}"));
        }
        TokenSummary = $"{totals.Turns} turn{(totals.Turns == 1 ? "" : "s")} · {totals.Total:N0} tokens · about ${totals.EstimatedCostUsd:0.00} at list price (an estimate, not your bill)";

        static string N(long n) => n.ToString("N0");
    }

    // ---- Composer and quick suffixes (DESIGN.md §5) -------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string ComposerText { get; set; } = "";

    public ObservableCollection<SuffixChip> Chips { get; } = [];

    public IReadOnlyList<QuickSuffix> AvailableSuffixes => _services.Settings.QuickSuffixes;

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
        _conversation.ExpandThinking = _services.Settings.Appearance.ExpandThinking;
    }

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
        if (text.Length == 0 && suffixes.Length == 0)
        {
            return;
        }
        var suffixText = suffixes.Length > 0 ? string.Join("\n", suffixes) : null;
        var message = suffixText is null ? text : text.Length == 0 ? suffixText : $"{text}\n\n{suffixText}";

        ComposerText = "";
        foreach (var chip in Chips.Where(c => !c.IsKept).ToArray())
        {
            Chips.Remove(chip);
        }
        _conversation.AddUserMessage(text, suffixText);
        _firstPrompt ??= text.Length > 0 ? text : suffixText;
        await SendRawAsync(message);
        _ = RequestTitleAsync();
    }

    private bool CanSend() => Status is not (TabStatus.Starting or TabStatus.Error) && (ComposerText.Trim().Length > 0 || Chips.Count > 0);

    private async Task SendRawAsync(string message)
    {
        try
        {
            await EnsureStartedAsync();
            if (_session is null)
            {
                return;
            }
            await _session.SendUserMessageAsync(message);
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
    });

    // ---- Lifecycle -----------------------------------------------------------------------------------------

    public bool CanRestart => Status is TabStatus.Exited or TabStatus.Error;

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private Task RestartAsync() => EnsureStartedAsync();

    /// <summary>Starts the process if it isn't running: restored tabs start on first selection or message.</summary>
    public async Task EnsureStartedAsync()
    {
        if (_session is not null || Status == TabStatus.Error && !Directory.Exists(Folder))
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
            Status = TabStatus.Error;
            _conversation.AddNote($"The folder '{Folder}' no longer exists. Close this tab, or open the folder again from the new tab picker.", NoteKind.Error);
            return;
        }

        Status = TabStatus.Starting;
        var resume = State.SessionId;
        if (!_restoredTranscript)
        {
            _restoredTranscript = true;
            resume = await RestoreTranscriptAsync();
        }

        var settings = _services.Settings;
        try
        {
            var session = await sessions.StartAsync(new ClaudeLaunchOptions
            {
                WorkingDirectory = Folder,
                Resume = resume,
                Model = State.Overrides.Model ?? settings.NewTabs.DefaultModel,
                Effort = State.Overrides.Effort ?? settings.NewTabs.DefaultEffort,
                PermissionMode = State.Overrides.PermissionMode ?? settings.NewTabs.DefaultPermissionMode,
                AdditionalArguments = settings.Advanced.ExtraArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            });
            _session = session;
            _sessionStartedAt = _services.Time.GetUtcNow();
            Effort = State.Overrides.Effort ?? settings.NewTabs.DefaultEffort;
            PermissionMode = session.PermissionMode;
            _modelId = session.Model ?? State.Overrides.Model;
            ModelName = ModelDisplayName(_modelId) ?? CurrentModelInfo?.DisplayName;
            OnPropertyChanged(nameof(Models));
            OnPropertyChanged(nameof(EffortLevels));
            Status = TabStatus.Idle;
            _pump = PumpAsync(session);
            _ = RefreshContextUsageAsync(session);
        }
        catch (Exception ex)
        {
            Status = TabStatus.Error;
            _conversation.AddNote($"Couldn't start Claude Code: {ex.Message}", NoteKind.Error);
        }
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
        var path = _services.ProjectsDirectory is { } projects ? TranscriptReader.Find(projects, sessionId) : null;
        if (path is null)
        {
            _conversation.AddNote("The earlier conversation couldn't be found (Claude Code may have cleaned it up). Starting a new session in this folder.", NoteKind.Warning);
            State.SessionId = null;
            return null;
        }
        try
        {
            var transcript = await TranscriptReader.ReadAsync(path);
            foreach (var item in transcript.Items)
            {
                switch (item)
                {
                    case TranscriptPrompt prompt:
                        _conversation.AddUserMessage(prompt.Text);
                        break;
                    case TranscriptNote note:
                        _conversation.AddNote(note.Text);
                        break;
                    case TranscriptMessage { Message: AssistantMessage assistant }:
                        _conversation.Apply(new AssistantMessageReceived(assistant));
                        break;
                    case TranscriptMessage { Message: UserMessage results }:
                        _conversation.Apply(new ToolResultsReceived(results));
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
            while (reader.TryRead(out var sessionEvent))
            {
                batch.Add(sessionEvent);
            }
            var events = batch.ToArray();
            batch.Clear();
            _services.Dispatcher.Post(() => Apply(session, events));
        }
    }

    private void Apply(ClaudeSession session, IReadOnlyList<SessionEvent> events)
    {
        if (!ReferenceEquals(session, _session))
        {
            return;
        }
        foreach (var sessionEvent in events)
        {
            _conversation.Apply(sessionEvent);
            switch (sessionEvent)
            {
                case StateChanged { State: SessionState.Working }:
                    _checkIns.TurnStarted();
                    UpdateStatus();
                    break;
                case StateChanged:
                    UpdateStatus();
                    break;
                case TurnStarted started:
                    State.SessionId = started.Init.SessionId;
                    _modelId = started.Init.Model ?? _modelId;
                    ModelName = ModelDisplayName(_modelId) ?? ModelName;
                    PermissionMode = started.Init.PermissionMode ?? PermissionMode;
                    _services.SaveState();
                    break;
                case TextDelta or ThinkingDelta or AssistantMessageReceived or ToolResultsReceived:
                    _checkIns.OutputSeen();
                    break;
                case PermissionRequested requested:
                    _pendingPermissions++;
                    _checkIns.SetWaitingOnUser(true);
                    WatchPermission(requested.Request);
                    UpdateStatus();
                    break;
                case PermissionCancelled:
                    PermissionResolved();
                    break;
                case TurnCompleted completed:
                    _checkIns.TurnEnded();
                    State.SessionId = completed.Result.SessionId ?? State.SessionId;
                    State.Tokens.Add(completed.Result);
                    RefreshTokens();
                    _services.SaveState();
                    _ = RefreshContextUsageAsync(session);
                    if (!IsSelected && !completed.Result.IsError)
                    {
                        Status = TabStatus.Unread;
                    }
                    break;
                case ConversationReset:
                    TodoList.Clear();
                    State.AutoName = null;
                    _titleRequested = false;
                    _firstPrompt = null;
                    NameChanged();
                    break;
                case AuthenticationRequired:
                    _restartAfterSignIn = true;
                    _shell.OnAuthenticationRequired();
                    break;
                case SessionExited exited:
                    _session = null;
                    _checkIns.TurnEnded();
                    _pendingPermissions = 0;
                    Status = exited.Exit.ExitCode == 0 ? TabStatus.Exited : TabStatus.Error;
                    OnPropertyChanged(nameof(CanRestart));
                    break;
            }
        }
    }

    private void WatchPermission(PermissionRequest request)
    {
        var item = Items.OfType<PermissionItem>().LastOrDefault(p => ReferenceEquals(p.Request, request));
        if (item is not null)
        {
            item.Answered += (_, _) => PermissionResolved();
        }
    }

    private void PermissionResolved()
    {
        _pendingPermissions = Math.Max(0, _pendingPermissions - 1);
        if (_pendingPermissions == 0)
        {
            _checkIns.SetWaitingOnUser(false);
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_session is null)
        {
            return;
        }
        Status = _pendingPermissions > 0 ? TabStatus.NeedsInput
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
            return;
        }
        _services.Dispatcher.Post(() =>
        {
            if (!ReferenceEquals(session, _session))
            {
                return;
            }
            ContextText = $"Context {usage.Percentage:0}%";
            var compacts = usage is { AutoCompactEnabled: true, AutoCompactThreshold: { } threshold } ? $" · auto-compacts at {threshold:N0}" : "";
            ContextDetail = $"{usage.TotalTokens:N0} of {usage.MaxTokens:N0} tokens{compacts}";
            IsContextHigh = usage.AutoCompactThreshold is { } limit && usage.AutoCompactEnabled
                ? usage.TotalTokens >= limit * 0.9
                : usage.Percentage >= 80;
        });
    }

    /// <summary>After a sign-in: a session that hit an authentication error restarts on the same session.</summary>
    public async Task OnSignedInAgainAsync()
    {
        if (!_restartAfterSignIn)
        {
            return;
        }
        _restartAfterSignIn = false;
        await StopSessionAsync();
        _conversation.AddNote("Signed in. Send your message again.");
        await EnsureStartedAsync();
    }

    /// <summary>Applies changed per-tab overrides to the running session.</summary>
    public async Task ApplyOverridesAsync(TabOverrides previous)
    {
        _services.SaveState();
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
            var mode = State.Overrides.PermissionMode ?? _services.Settings.NewTabs.DefaultPermissionMode;
            if (State.Overrides.PermissionMode != previous.PermissionMode && mode is not null)
            {
                await _session.SetPermissionModeAsync(mode);
                PermissionMode = mode;
            }
        }
        catch (Exception ex)
        {
            _conversation.AddNote($"Couldn't apply the tab settings: {ex.Message}", NoteKind.Error);
        }
    }

    private async Task StopSessionAsync()
    {
        var session = _session;
        _session = null;
        if (session is null)
        {
            return;
        }
        if (session.State == SessionState.Working)
        {
            // Interrupt first, so the turn ends cleanly with a result (DESIGN.md §13, "Shutdown").
            try
            {
                await session.InterruptAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception)
            {
                // Best effort; the process is stopped next either way.
            }
        }
        await session.DisposeAsync();
        if (_pump is not null)
        {
            await _pump;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _checkIns.Dispose();
        await StopSessionAsync();
    }

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
