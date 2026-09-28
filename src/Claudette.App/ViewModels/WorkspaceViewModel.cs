using System.Collections.ObjectModel;
using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// One Claude Code session in a working folder. Milestone 1 has a single one of these; milestone 3 turns it into tabs.
/// </summary>
public sealed partial class WorkspaceViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly AppServices _services;
    private readonly Action _onAuthenticationRequired;
    private readonly ConversationBuilder _conversation;
    private ClaudeSession? _session;
    private Task? _pump;
    private string? _lastSessionId;
    private bool _restartAfterSignIn;

    public WorkspaceViewModel(AppServices services, Action onAuthenticationRequired)
    {
        _services = services;
        _onAuthenticationRequired = onAuthenticationRequired;
        _conversation = new ConversationBuilder(Items);
    }

    public ObservableCollection<ConversationItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolder), nameof(FolderName))]
    [NotifyCanExecuteChangedFor(nameof(RestartCommand))]
    public partial string? Folder { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(RestartCommand), nameof(ChooseFolderCommand))]
    public partial bool IsStarting { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(RestartCommand))]
    [NotifyPropertyChangedFor(nameof(CanRestart))]
    public partial bool HasSession { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial bool IsWorking { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string ComposerText { get; set; } = "";

    [ObservableProperty]
    public partial string? ModelName { get; set; }

    [ObservableProperty]
    public partial string? PermissionMode { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    /// <summary>Context window use, for example "Context 41%" (DESIGN.md §6, "Per-tab context").</summary>
    [ObservableProperty]
    public partial string? ContextText { get; set; }

    [ObservableProperty]
    public partial string? ContextDetail { get; set; }

    /// <summary>True near the auto-compact threshold, or above 80% without one.</summary>
    [ObservableProperty]
    public partial bool IsContextHigh { get; set; }

    public bool HasFolder => Folder is not null;

    public string FolderName => Folder is null ? "" : Path.GetFileName(Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name ? name : Folder;

    public bool CanRestart => HasFolder && !HasSession && !IsStarting;

    [RelayCommand(CanExecute = nameof(CanChooseFolder))]
    private async Task ChooseFolderAsync()
    {
        if (await _services.Platform.PickFolderAsync("Choose a working folder") is { } folder)
        {
            await OpenFolderAsync(folder);
        }
    }

    /// <summary>Ends any current session and starts a new one in <paramref name="folder"/>.</summary>
    public async Task OpenFolderAsync(string folder)
    {
        if (!Directory.Exists(folder))
        {
            _conversation.AddNote($"'{folder}' doesn't exist.", NoteKind.Error);
            return;
        }
        await EndSessionAsync();
        Items.Clear();
        _lastSessionId = null;
        Folder = folder;
        await StartSessionAsync(resume: null);
    }

    private bool CanChooseFolder() => !IsStarting;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = ComposerText.Trim();
        if (_session is null || text.Length == 0)
        {
            return;
        }
        ComposerText = "";
        _conversation.AddUserMessage(text);
        try
        {
            await _session.SendUserMessageAsync(text);
        }
        catch (Exception ex)
        {
            _conversation.AddNote($"Couldn't send the message: {ex.Message}", NoteKind.Error);
        }
    }

    private bool CanSend() => HasSession && !IsStarting && ComposerText.Trim().Length > 0;

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

    /// <summary>Starts Claude Code again on the same session, for example after it exited.</summary>
    [RelayCommand(CanExecute = nameof(CanRestart))]
    private Task RestartAsync() => StartSessionAsync(_lastSessionId);

    /// <summary>Called after a sign-in. A session that hit an authentication error is restarted on the same session.</summary>
    public async Task OnSignedInAgainAsync()
    {
        if (!_restartAfterSignIn || Folder is null)
        {
            return;
        }
        _restartAfterSignIn = false;
        await EndSessionAsync();
        _conversation.AddNote("Signed in. Send your message again.");
        await StartSessionAsync(_lastSessionId);
    }

    private async Task StartSessionAsync(string? resume)
    {
        if (Folder is null || _services.Sessions is not { } sessions)
        {
            return;
        }
        IsStarting = true;
        StatusText = "Starting Claude Code…";
        try
        {
            var session = await sessions.StartAsync(new ClaudeLaunchOptions { WorkingDirectory = Folder, Resume = resume });
            _session = session;
            HasSession = true;
            PermissionMode = session.PermissionMode;
            ModelName = FriendlyModelName(session, session.Model);
            StatusText = "Ready";
            _pump = PumpAsync(session);
            _ = RefreshContextUsageAsync(session);
        }
        catch (Exception ex)
        {
            StatusText = "Not running";
            _conversation.AddNote($"Couldn't start Claude Code: {ex.Message}", NoteKind.Error);
        }
        finally
        {
            IsStarting = false;
            OnPropertyChanged(nameof(CanRestart));
        }
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
                case StateChanged state:
                    IsWorking = state.State == SessionState.Working;
                    StatusText = state.State switch
                    {
                        SessionState.Working => "Working…",
                        SessionState.Idle => "Ready",
                        SessionState.Exited => "Not running",
                        _ => StatusText,
                    };
                    break;
                case TurnStarted started:
                    _lastSessionId = started.Init.SessionId;
                    ModelName = FriendlyModelName(session, started.Init.Model);
                    PermissionMode = started.Init.PermissionMode ?? PermissionMode;
                    break;
                case TurnCompleted completed:
                    _lastSessionId = completed.Result.SessionId ?? _lastSessionId;
                    _ = RefreshContextUsageAsync(session);
                    break;
                case AuthenticationRequired:
                    _restartAfterSignIn = true;
                    _onAuthenticationRequired();
                    break;
                case SessionExited:
                    _session = null;
                    HasSession = false;
                    IsWorking = false;
                    StatusText = "Not running";
                    OnPropertyChanged(nameof(CanRestart));
                    break;
            }
        }
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
            // Not critical: keep showing the last value.
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

    private static string? FriendlyModelName(ClaudeSession session, string? model)
    {
        if (model is null)
        {
            return session.Initialization?.Models.FirstOrDefault(m => m.Value == "default")?.DisplayName ?? "Default";
        }
        // init reports e.g. "claude-opus-5-5[1m]"; prefer a named entry ("Opus (1M context)") over the "default" alias.
        var models = session.Initialization?.Models ?? [];
        var match = models.FirstOrDefault(m => m.Value == model)
            ?? models.Where(m => m.Value != "default").FirstOrDefault(m => m.ResolvedModel == model)
            ?? models.Where(m => m.Value != "default").FirstOrDefault(m => m.ResolvedModel is { } r && WithoutSuffix(r) == WithoutSuffix(model));
        return match?.DisplayName ?? model;

        static string WithoutSuffix(string id) => id.Split('[')[0];
    }

    private async Task EndSessionAsync()
    {
        var session = _session;
        _session = null;
        HasSession = false;
        IsWorking = false;
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

    public async ValueTask DisposeAsync() => await EndSessionAsync();
}
