using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.Core.ScratchPads;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Claudette.App.ViewModels;

/// <summary>
/// A project's scratch pad (DESIGN.md §18, "Scratch pad"): the text every tab in the project shows on the side panel's
/// Scratch Pad page, one object for all of them (<see cref="ScratchPadService"/>). What's typed is saved to this
/// machine's copy a second after typing stops, and a pad with a git remote then syncs with the session library. When
/// another machine changed it too, <see cref="Conflict"/> asks what it should become.
/// </summary>
public sealed partial class ScratchPadViewModel : ObservableObject, IDisposable
{
    /// <summary>How long after typing stops the pad is saved.</summary>
    internal static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    /// <summary>How long closing Claudette waits for the library before leaving a pad's changes for next time.</summary>
    internal static readonly TimeSpan FlushSyncTimeout = TimeSpan.FromSeconds(3);

    private readonly AppServices _services;
    private readonly ScratchPadStore _store;
    private readonly ILogger _logger;
    private readonly UiTimeout _save;
    private readonly UiTimeout _copied;

    /// <summary>One sync or resolution at a time.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>What this machine's file holds, or will once the write under way finishes.</summary>
    private ScratchPadFile _saved;

    /// <summary>The local writes, one after another in the order they were made.</summary>
    private Task _writing = Task.CompletedTask;

    /// <summary>A sync was asked for while one ran: it runs again once that's done.</summary>
    private bool _syncAgain;

    /// <summary>The text is being set from a sync or a resolution, not typed.</summary>
    private bool _applying;

    public ScratchPadViewModel(AppServices services, ScratchPadStore store, ScratchPadProject project, string name)
    {
        _services = services;
        _store = store;
        _logger = services.Loggers.CreateLogger<ScratchPadViewModel>();
        _save = new UiTimeout(services.Time, services.Dispatcher);
        _copied = new UiTimeout(services.Time, services.Dispatcher);
        Project = project;
        Name = name;
        ClearConfirmation = new InlineConfirmation(ClearAsync);
        _saved = Load() ?? Empty;
        ShowText(_saved.Text);
        UpdateSyncText();
    }

    public ScratchPadProject Project { get; }

    /// <summary>The project's folder name, as its tab group is labeled.</summary>
    public string Name { get; }

    /// <summary>The page's first line.</summary>
    public string Summary => $"Shared by the tabs in {Name}";

    /// <summary>The pad's text, as the page edits it. Typing saves it a second after it stops.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyCanExecuteChangedFor(nameof(CopyAllCommand))]
    public partial string Text { get; set; } = "";

    partial void OnTextChanged(string value)
    {
        if (!_applying)
        {
            _save.Restart(SaveDelay, () => _ = SaveAsync());
        }
    }

    public bool IsEmpty => Text.Length == 0;

    /// <summary>
    /// The pad can't be typed in: this machine's copy couldn't be read, so it isn't saved over, or a choice about both
    /// machines' changes is being saved.
    /// </summary>
    public bool IsReadOnly => IsUnreadable || IsResolving;

    /// <summary>This machine's copy couldn't be read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadOnly))]
    public partial bool IsUnreadable { get; private set; }

    /// <summary>Sets the text shown without saving it: what was read, or what a sync brought.</summary>
    private void ShowText(string text)
    {
        _applying = true;
        try
        {
            Text = text;
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>
    /// Text was added (<see cref="Append"/>): the page brings it into view and selects it. Its start and length in
    /// <see cref="Text"/>.
    /// </summary>
    public event Action<int, int>? Added;

    // ---- Where it's kept, and how syncing went ---------------------------------------------------------------------

    /// <summary>Where the pad is kept, under <see cref="Summary"/>: synced through the library, or why not.</summary>
    [ObservableProperty]
    public partial string SyncText { get; private set; } = "";

    /// <summary>The detail for <see cref="SyncText"/>, as its tooltip: what went wrong, for example.</summary>
    [ObservableProperty]
    public partial string? SyncDetail { get; private set; }

    /// <summary><see cref="SyncText"/> is a problem to fix rather than where the pad is kept.</summary>
    [ObservableProperty]
    public partial bool HasSyncProblem { get; private set; }

    private string? _problem;
    private string? _problemDetail;

    private void UpdateSyncText()
    {
        var (text, detail) = _problem is not null ? (_problem, _problemDetail) : Project switch
        {
            { IsShared: true } when _services.Settings.Sessions.LibraryFolder is { Length: > 0 } =>
                ("Synced to your other machines through the session library", _store.LibraryPath(Project.Id)),
            { IsShared: true } =>
                ("On this machine. To see it on your other machines too, keep the session library in a synced folder (Settings → Sessions).", null),
            { IsInRepository: true } => ("On this machine only: the repository has no remote to know it by on another machine.", null),
            _ => ("On this machine only: the folder isn't in a git repository.", null),
        };
        SyncText = text;
        SyncDetail = detail;
        HasSyncProblem = _problem is not null;
    }

    private void SetProblem(string? problem, string? detail = null)
    {
        _problem = problem;
        _problemDetail = detail;
        UpdateSyncText();
    }

    /// <summary>Settings changed: the library may be in another folder now.</summary>
    internal void OnSettingsChanged() => UpdateSyncText();

    // ---- Adding to it -------------------------------------------------------------------------------------------

    /// <summary>
    /// Adds <paramref name="text"/> at the end, after a blank line, and saves at once. Nothing happens for text that's
    /// only blank. Returns whether anything was added.
    /// </summary>
    public bool Append(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsUnreadable)
        {
            return false;
        }
        var addition = ScratchPadText.Append(Text, text);
        Text = addition.Text;
        _ = SaveAsync();
        Added?.Invoke(addition.Start, addition.Length);
        return true;
    }

    // ---- Copy all and Clear -------------------------------------------------------------------------------------

    /// <summary><b>Copy all</b> says "Copied" for a moment.</summary>
    [ObservableProperty]
    public partial bool IsCopied { get; private set; }

    [RelayCommand(CanExecute = nameof(HasText))]
    private async Task CopyAllAsync()
    {
        await _services.Platform.SetClipboardTextAsync(Text);
        IsCopied = true;
        _copied.Restart(TabViewModel.CopiedFor, () => IsCopied = false);
    }

    private bool HasText() => Text.Length > 0;

    /// <summary><b>Clear…</b> asks first, in place.</summary>
    public InlineConfirmation ClearConfirmation { get; }

    private Task ClearAsync()
    {
        Text = "";
        return SaveAsync();
    }

    // ---- Both changed (DESIGN.md §18, "Scratch pad", "Other machines") ----------------------------------------------

    /// <summary>Another machine changed the pad too, without seeing this machine's change: the banner over it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConflict), nameof(ConflictText), nameof(TheirText))]
    public partial ScratchPadConflict? Conflict { get; private set; }

    public bool HasConflict => Conflict is not null;

    /// <summary>"DESKTOP-01 also changed this scratch pad (Mon 14:05)."</summary>
    public string? ConflictText => Conflict?.Theirs is { } theirs
        ? $"{(theirs.Machine.Length > 0 ? theirs.Machine : "Another machine")} also changed this scratch pad{When(theirs.ChangedAt)}. Which should it keep?"
        : null;

    /// <summary>The other machine's text, shown in the banner to choose by.</summary>
    public string? TheirText => Conflict?.Theirs.Text;

    /// <summary>A choice is being saved: the pad and the banner wait for it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadOnly))]
    public partial bool IsResolving { get; private set; }

    /// <summary><b>Keep both</b>: this machine's text, then the lines only theirs has.</summary>
    [RelayCommand]
    private Task KeepBothAsync() => ResolveAsync(conflict => ScratchPadText.KeepBoth(Text, conflict.Theirs.Text));

    [RelayCommand]
    private Task UseTheirsAsync() => ResolveAsync(conflict => conflict.Theirs.Text);

    [RelayCommand]
    private Task KeepMineAsync() => ResolveAsync(_ => Text);

    private async Task ResolveAsync(Func<ScratchPadConflict, string> choose)
    {
        if (Conflict is not { } conflict || IsResolving)
        {
            return;
        }
        IsResolving = true;
        await _gate.WaitAsync();
        try
        {
            SaveTyped();
            var text = choose(conflict);
            var local = _saved;
            var (machine, now) = (_services.Library.MachineName, _services.Time.GetUtcNow());
            var result = await Task.Run(() => _store.Resolve(Project.Id, local, conflict, text, machine, now));
            switch (result.Kind)
            {
                case ScratchPadSyncKind.Pushed or ScratchPadSyncKind.Pulled:
                    TakeLocal(result.Local);
                    Conflict = null;
                    SetProblem(null);
                    break;
                case ScratchPadSyncKind.Conflict:
                    Conflict = result.Conflict;
                    break;
                default:
                    ShowProblem(result);
                    break;
            }
        }
        finally
        {
            _gate.Release();
            IsResolving = false;
        }
        // A sync client's other copies, if any.
        await SyncAsync();
    }

    // ---- Saving and syncing ---------------------------------------------------------------------------------------

    /// <summary>Saves what's typed to this machine's copy, then syncs it.</summary>
    internal Task SaveAsync()
    {
        _save.Cancel();
        return SaveTyped() ? SyncAsync() : Task.CompletedTask;
    }

    /// <summary>Gives what's typed a new revision and writes it here, if it changed. True when it did.</summary>
    private bool SaveTyped()
    {
        _save.Cancel();
        if (Text == _saved.Text || IsUnreadable)
        {
            return false;
        }
        _saved = _saved with
        {
            Text = Text,
            Revision = ScratchPadSync.NewRevision(),
            Machine = _services.Library.MachineName,
            ChangedAt = _services.Time.GetUtcNow(),
            Remote = Project.Remote,
            PathInRepo = Project.IsShared ? Project.PathInRepo : null,
        };
        WriteLocal(_saved);
        return true;
    }

    /// <summary>
    /// Brings this machine's copy and the library's together: when the page shows, when Claudette comes to the front,
    /// once a minute, and after each save. Only for a pad with a git remote.
    /// </summary>
    public async Task SyncAsync()
    {
        if (IsUnreadable && !Reload())
        {
            return;
        }
        if (!Project.IsShared || _services.SuspendSaving)
        {
            return;
        }
        if (!await _gate.WaitAsync(0))
        {
            _syncAgain = true;
            return;
        }
        try
        {
            do
            {
                _syncAgain = false;
                var local = _saved;
                var result = await Task.Run(() => _store.Sync(Project.Id, local));
                Apply(local, result);
            }
            while (_syncAgain && !_services.SuspendSaving);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Apply(ScratchPadFile synced, ScratchPadSyncResult result)
    {
        switch (result.Kind)
        {
            case ScratchPadSyncKind.Pushed:
                // The library has that revision now, and anything saved here since carries on from it.
                _saved = _saved with { Synced = synced.Revision };
                WriteLocal(_saved);
                break;
            case ScratchPadSyncKind.Pulled or ScratchPadSyncKind.Adopted:
                // Not over what was saved or typed meanwhile: that save's sync finds both changed, and asks.
                if (_saved.Revision != synced.Revision || Text != _saved.Text)
                {
                    return;
                }
                TakeLocal(result.Local);
                break;
        }
        if (result.Kind is ScratchPadSyncKind.Unavailable or ScratchPadSyncKind.NewerFormat)
        {
            ShowProblem(result);
            return;
        }
        SetProblem(null);
        Conflict = result.Conflict;
    }

    private void ShowProblem(ScratchPadSyncResult result)
    {
        if (result.Kind == ScratchPadSyncKind.NewerFormat)
        {
            SetProblem("A newer Claudette changed this pad in the session library, so it isn't synced from here. Update Claudette to sync it again.");
        }
        else
        {
            _logger.LogInformation("Couldn't sync scratch pad {Id}: {Reason}", Project.Id, result.Reason);
            SetProblem("Couldn't reach the session library, so the pad is saved on this machine for now. It's copied there once it can be.", result.Reason);
        }
    }

    /// <summary>This machine's copy becomes <paramref name="local"/>: saved here, and shown.</summary>
    private void TakeLocal(ScratchPadFile local)
    {
        _saved = local;
        WriteLocal(local);
        ShowText(local.Text);
    }

    private ScratchPadFile Empty => new() { Remote = Project.Remote, PathInRepo = Project.IsShared ? Project.PathInRepo : null };

    private ScratchPadFile? Load()
    {
        try
        {
            var file = _store.ReadLocal(Project.Id, _services.Time.GetUtcNow());
            IsUnreadable = false;
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't read scratch pad {Id}.", Project.Id);
            IsUnreadable = true;
            SetProblem("Couldn't read this pad's file, so nothing is saved over it. It's tried again in a minute.", ex.Message);
            return null;
        }
    }

    /// <summary>Tries again to read a copy that couldn't be read: true once it has been.</summary>
    private bool Reload()
    {
        var file = Load();
        if (IsUnreadable)
        {
            return false;
        }
        SetProblem(null);
        _saved = file ?? Empty;
        ShowText(_saved.Text);
        return true;
    }

    /// <summary>Writes this machine's copy in the background, after any write before it.</summary>
    private void WriteLocal(ScratchPadFile file)
    {
        if (_services.SuspendSaving)
        {
            return;
        }
        var previous = _writing;
        _writing = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            try
            {
                _store.WriteLocal(Project.Id, file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Couldn't save scratch pad {Id}.", Project.Id);
                _services.Dispatcher.Post(() => SetProblem("Couldn't save the pad on this machine.", ex.Message));
            }
        });
    }

    /// <summary>
    /// For closing Claudette, a restart into a new build, or the last tab in the project closing: saves what's typed,
    /// and syncs it if the library answers within <see cref="FlushSyncTimeout"/>.
    /// </summary>
    public async Task FlushAsync()
    {
        SaveTyped();
        await _writing;
        if (Project.IsShared && !_services.SuspendSaving)
        {
            await Task.WhenAny(SyncAsync(), Task.Delay(FlushSyncTimeout, _services.Time));
        }
        await _writing;
    }

    /// <summary>The local writes are done: for tests.</summary>
    internal Task Writing => _writing;

    public void Dispose()
    {
        _save.Dispose();
        _copied.Dispose();
    }

    private string When(DateTimeOffset at) => at == DateTimeOffset.MinValue ? "" : $" ({MessageTimes.Short(at, _services.Time)})";
}
