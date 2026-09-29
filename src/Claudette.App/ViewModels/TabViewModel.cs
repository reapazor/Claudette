using System.Collections.ObjectModel;
using System.Globalization;
using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.Core.Development;
using Claudette.Core.Git;
using Claudette.Core.Library;
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

/// <summary>An entry of the quick suffix menu (DESIGN.md §5).</summary>
/// <param name="Number">1–9 for the first nine, which those keys pick while the menu is open.</param>
/// <param name="Shortcut">The suffix's own shortcut, as it reads on this OS.</param>
public sealed record SuffixMenuItem(QuickSuffix Suffix, int? Number, string? Shortcut);

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
    private int _pendingPermissions;
    private string? _firstPrompt;
    private bool _titleRequested;

    public TabViewModel(AppServices services, ShellViewModel shell, TabState state, bool isRestored)
    {
        _services = services;
        _shell = shell;
        State = state;
        _conversation = new ConversationBuilder(Items, TodoList, ModelDisplayName)
        {
            ExpandThinking = services.Settings.Appearance.ExpandThinking,
            ShowUnsupportedMessages = services.Settings.Advanced.LogProtocol,
        };
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
        OnPropertyChanged(nameof(CloseMissingText));
        _shell.OnPinChanged(this);
    }

    // ---- Status (DESIGN.md §4, "Status icon") ------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusGlyph), nameof(StatusTip), nameof(InfoRows), nameof(IsWorking), nameof(NeedsInput), nameof(IsBusyStatus), nameof(IsAlertStatus), nameof(IsErrorStatus), nameof(IsUnread), nameof(RowDetail))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand), nameof(SendCommand), nameof(RestartCommand), nameof(CompactCommand))]
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
        TabStatus.Error => IsFolderMissing ? "Its folder no longer exists" : ErrorMessage ?? "Claude Code stopped with an error",
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
        if (value)
        {
            if (Status == TabStatus.Unread)
            {
                Status = TabStatus.Idle;
            }
            _ = EnsureStartedAsync();
        }
    }

    /// <summary>Two check-ins in a row got no reply (DESIGN.md §5, "Check-ins on long turns"); shown on the tab's row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowDetail), nameof(StatusTip), nameof(InfoRows))]
    public partial bool IsPossiblyStuck { get; set; }

    // ---- Model, effort and mode (DESIGN.md §5, "Model & effort") ------------------------------------------

    public IReadOnlyList<ModelInfo> Models => _session?.Initialization?.Models.Where(m => m.Value != "default").ToArray() ?? [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelBadge), nameof(InfoRows), nameof(EffortLevels), nameof(RowDetail))]
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
    /// when the tab is waiting on the user or has failed.
    /// </summary>
    public string RowDetail => Status is TabStatus.NeedsInput or TabStatus.Error ? StatusTip
        : Status == TabStatus.Working && IsPossiblyStuck ? "Possibly stuck"
        : ModelBadge;

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

    public IReadOnlyList<PermissionModeChoice> PermissionModeChoices => PermissionModeInfo.Choices;

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
    public partial string? ContextText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InfoRows))]
    public partial string? ContextDetail { get; set; }

    [ObservableProperty]
    public partial bool IsContextHigh { get; set; }

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

    /// <summary>A call finished mid-turn: the token count moves on before the result gives the turn's totals.</summary>
    private void OnCallUsage()
    {
        TokensShort = TokenTotals.Short(State.Tokens.Total + _callUsage.TurnTokens);
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
        ContextText = $"Context {percentage:0}%";
        ContextDetail = $"about {_callUsage.ContextTokens:N0} of {window:N0} tokens, estimated from the last call";
        IsContextHigh = percentage >= 80;
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

    /// <summary>The suffix menu: numbered 1–9 for picking from the keyboard, with each suffix's own shortcut (DESIGN.md §5).</summary>
    public IReadOnlyList<SuffixMenuItem> SuffixMenu => AvailableSuffixes
        .Select((suffix, index) => new SuffixMenuItem(suffix, index < 9 ? index + 1 : null,
            KeyChord.TryParse(suffix.Shortcut, out var chord) ? chord.Display(Shortcuts.IsMac) : null))
        .ToArray();

    /// <summary>Picks the <paramref name="number"/>th suffix (1–9) in the menu.</summary>
    public bool PickSuffix(int number)
    {
        if (number < 1 || number > Math.Min(9, AvailableSuffixes.Count))
        {
            return false;
        }
        AddSuffix(AvailableSuffixes[number - 1]);
        return true;
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
        UpdateSampler();
    }

    // ---- Restarting into a new build (DESIGN.md §9, "Working on Claudette") -------------------------------

    /// <summary>The message typed but not sent, with its one-off quick suffixes, or null when there's none.</summary>
    public TabDraft? Draft => ComposerText.Length == 0 && Chips.All(c => c.IsKept)
        ? null
        : new TabDraft(ComposerText, Chips.Where(c => !c.IsKept).Select(c => c.Suffix.Id).ToList());

    /// <summary>Puts back a draft from the build that restarted into this one.</summary>
    public void RestoreDraft(TabDraft draft)
    {
        ComposerText = draft.Text;
        foreach (var id in draft.SuffixIds)
        {
            AddSuffix(_services.Settings.QuickSuffixes.FirstOrDefault(s => s.Id == id));
        }
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

    private bool CanSend() => !IsReadOnly && Status is not (TabStatus.Starting or TabStatus.Error) && (ComposerText.Trim().Length > 0 || Chips.Count > 0);

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
        NotifyCheckIn();
    });

    // ---- Lifecycle -----------------------------------------------------------------------------------------

    public bool CanRestart => Status is TabStatus.Exited or TabStatus.Error && !IsFolderMissing && !IsSessionMissing;

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private Task RestartAsync() => EnsureStartedAsync();

    /// <summary>Starts the process if it isn't running: restored tabs start on first selection or message.</summary>
    public async Task EnsureStartedAsync()
    {
        if (_session is not null || IsSessionMissing || Status == TabStatus.Error && !Directory.Exists(Folder))
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
        if (resume is not null && !fork && !await ClaimLeaseAsync())
        {
            return;
        }

        var settings = _services.Settings;
        try
        {
            var session = await sessions.StartAsync(new ClaudeLaunchOptions
            {
                WorkingDirectory = Folder,
                Resume = resume,
                ForkSession = fork,
                Model = State.Overrides.Model ?? settings.NewTabs.DefaultModel,
                Effort = State.Overrides.Effort ?? settings.NewTabs.DefaultEffort,
                PermissionMode = State.Overrides.PermissionMode ?? settings.NewTabs.DefaultPermissionMode,
                AdditionalArguments = settings.Advanced.ExtraArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                ProtocolLogPath = _services.ProtocolLogPath(FolderName),
            });
            _session = session;
            _services.RememberModels(session.Initialization?.Models);
            State.SessionStartedAt ??= _services.Time.GetUtcNow();
            // The installed version is what just started; system/init confirms it with the first turn.
            SetRunningVersion(_services.InstalledClaudeVersion);
            if (fork)
            {
                // The copy gets a new session id with its first turn; it no longer writes to the original.
                State.ForkOnNextStart = false;
                State.TranscriptPath = null;
                _conversation.AddNote("Opened as a copy. The original session is left as it was.");
            }
            AttachProcessTree(session);
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
        OnPropertyChanged(nameof(InfoRows));
        OnPropertyChanged(nameof(IsGitRepository));
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
                        var assistantEvent = new AssistantMessageReceived(assistant);
                        _conversation.Apply(assistantEvent);
                        RecordFileChanges(assistantEvent);
                        break;
                    case TranscriptMessage { Message: UserMessage results }:
                        var resultsEvent = new ToolResultsReceived(results);
                        _conversation.Apply(resultsEvent);
                        RecordFileChanges(resultsEvent);
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
            RecordFileChanges(sessionEvent);
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
                case PermissionRequested requested:
                    _pendingPermissions++;
                    _checkIns.SetWaitingOnUser(true);
                    WatchPermission(requested.Request);
                    UpdateStatus();
                    NotifyNeedsInput(requested.Request);
                    break;
                case PermissionCancelled:
                    PermissionResolved();
                    break;
                case SystemNotice { Message.Subtype: "task_started" } task:
                    OnTaskStarted(task.Message);
                    break;
                case SystemNotice { Message.Subtype: "status" }:
                    // Claude Code reports mode changes it makes itself, such as leaving plan mode.
                    PermissionMode = session.PermissionMode ?? PermissionMode;
                    break;
                case RateLimitUpdated rateLimit:
                    _services.Usage?.OnRateLimitEvent(rateLimit.Message);
                    break;
                case TurnCompleted completed:
                    _checkIns.TurnEnded();
                    State.SessionId = completed.Result.SessionId ?? State.SessionId;
                    State.Tokens.Add(completed.Result);
                    _callUsage.TurnEnded(completed.Result);
                    _services.Usage?.OnTurnCompleted(Id, completed.Result);
                    if (State.SessionId is not null)
                    {
                        _ = _services.Library.SaveAfterTurnAsync(LibraryRecord(), State.TranscriptPath);
                    }
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
                    _restartAfterSignIn = true;
                    _shell.OnAuthenticationRequired();
                    break;
                case SessionExited exited:
                    _session = null;
                    SetRunningVersion(null);
                    _checkIns.TurnEnded();
                    _pendingPermissions = 0;
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
    }

    private void WatchPermission(PermissionRequest request)
    {
        var item = Items.OfType<PromptItem>().LastOrDefault(p => ReferenceEquals(p.Request, request));
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

    /// <summary>The tab has its own settings: its menu marks <b>Tab settings…</b> with a dot (DESIGN.md §14).</summary>
    public bool HasOverrides => State.Overrides.HasAny;

    /// <summary>Applies changed per-tab overrides to the running session.</summary>
    public async Task ApplyOverridesAsync(TabOverrides previous)
    {
        OnPropertyChanged(nameof(HasOverrides));
        _services.SaveState();
        UpdateSampler();
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

    /// <param name="killProcesses">Also end every process the session started (DESIGN.md §4, "Cleanup").</param>
    private async Task StopSessionAsync(bool killProcesses = false)
    {
        var session = _session;
        _session = null;
        SetRunningVersion(null);
        if (session is null)
        {
            await EndProcessTreeAsync(killProcesses);
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
        if (killProcesses)
        {
            // Note the children while claude is still their parent; on macOS and Linux they can't be found afterwards.
            RunningChildProcesses();
        }
        // Let claude exit on its own, so it finishes its transcript; then end what it left running, before disposing
        // the session releases the process tree.
        await session.StopAsync(TimeSpan.FromSeconds(3));
        await EndProcessTreeAsync(killProcesses);
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
        _services.Notifications.ClearTab(Id);
        ReleaseLease();
        await StopSessionAsync(killProcesses);
        CleanUpDiffFiles();
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
