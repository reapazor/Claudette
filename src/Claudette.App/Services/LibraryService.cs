using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Git;
using Claudette.Core.History;
using Claudette.Core.Library;
using Claudette.Core.Settings;
using Claudette.Core.Transcripts;
using Microsoft.Extensions.Logging;

namespace Claudette.App.Services;

/// <summary>
/// The session library (DESIGN.md §9): copies the transcript and record of each tab that syncs into the library folder
/// after its turns, keeps a lease on those sessions so two machines don't write the same one, and syncs Claudette's
/// settings through the library when that's turned on (DESIGN.md §14, "Settings sync").
/// </summary>
public sealed class LibraryService : IDisposable
{
    /// <summary>Claude Code finishes writing the transcript around the result message; give it a moment.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1);

    /// <summary>What syncs (DESIGN.md §14). The path to claude, the machine name, the library folder, the diff tool, folders and tabs stay per machine.</summary>
    /// <summary>Synced sections; <see cref="ApplySettings"/> copies each of them back.</summary>
    private static readonly string[] SyncedSettings = ["appearance", "newTabs", "usage", "checkIns", "quickSuffixes", "processes", "notifications", "keyboard"];

    /// <summary>Synced as one value each: the shortcut overrides come and go by command id.</summary>
    private static readonly string[] SyncedLeaves = ["keyboard.bindings"];

    private readonly AppServices _services;
    private readonly ILogger _logger;
    private readonly ITimer _syncTimer;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private bool _applyingSync;

    public LibraryService(AppServices services)
    {
        _services = services;
        _logger = services.Loggers.CreateLogger<LibraryService>();
        Library = new SessionLibrary(LibraryFolder, services.Time);
        Leases = new LeaseManager(MachineName, services.Time);
        Leases.LeaseLost += (sessionId, machine) => services.Dispatcher.Post(() => LeaseLost?.Invoke(sessionId, machine));
        // Pick up settings changed on other machines.
        _syncTimer = services.Time.CreateTimer(_ => services.Dispatcher.Post(() => _ = SyncSettingsAsync()), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public SessionLibrary Library { get; private set; }

    /// <summary>Claude Code's sessions on this machine, for History. Kept, so rescans only read changed files.</summary>
    public HistoryIndex? History => _services.ProjectsDirectory is { } projects
        ? _history is { } index && index.ProjectsDirectory == projects ? index : _history = new HistoryIndex(projects)
        : null;

    private HistoryIndex? _history;

    public LeaseManager Leases { get; }

    public string LibraryFolder => _services.Settings.Sessions.LibraryFolder is { Length: > 0 } folder ? folder : _services.Paths.DefaultLibraryDirectory;

    /// <summary>This machine's name in History and leases.</summary>
    public string MachineName => _services.Settings.Sessions.MachineName is { Length: > 0 } name ? name : Environment.MachineName;

    /// <summary>Raised on the UI thread when another machine took over a session open here.</summary>
    public event Action<string, string>? LeaseLost;

    /// <summary>The library folder setting may have changed.</summary>
    public void OnSettingsChanged()
    {
        if (!FolderHistory.SamePath(Library.LibraryFolder, LibraryFolder))
        {
            Library = new SessionLibrary(LibraryFolder, _services.Time);
        }
        if (!_applyingSync)
        {
            _ = SyncSettingsAsync();
        }
    }

    /// <summary>
    /// A session's transcript on this machine: the local working copy of a library session, Claude Code's own copy, or
    /// null.
    /// </summary>
    public string? FindTranscript(string sessionId, string? localCopy)
    {
        if (localCopy is not null && File.Exists(localCopy))
        {
            return localCopy;
        }
        if (_services.ProjectsDirectory is { } projects && TranscriptReader.Find(projects, sessionId) is { } path)
        {
            return path;
        }
        var local = Path.Combine(_services.Paths.LocalSessionsDirectory, $"{sessionId}.jsonl");
        return File.Exists(local) ? local : null;
    }

    /// <summary>
    /// Brings a library session to this machine: copies its transcript to the local working folder, which Claude Code
    /// resumes from and keeps writing to (DESIGN.md §9, "Resume").
    /// </summary>
    public Task<string> CopyToLocalAsync(string sessionId) => Library.CopyToLocalAsync(sessionId, _services.Paths.LocalSessionsDirectory);

    /// <summary>
    /// Copies a tab's session into the library in the background, and takes its lease (DESIGN.md §9, "Writing"): after
    /// each turn of a tab that syncs, and when a tab starts syncing. Never throws.
    /// </summary>
    /// <param name="stillSyncing">
    /// Asked after the settle delay and again before the lease is taken, so a tab that stopped syncing meanwhile writes
    /// nothing and doesn't take its lease back.
    /// </param>
    public async Task CopyToLibraryAsync(SessionRecord record, string? localCopy, Func<bool> stillSyncing)
    {
        try
        {
            await Task.Delay(SettleDelay, _services.Time).ConfigureAwait(false);
            if (!stillSyncing() || FindTranscript(record.SessionId, localCopy) is not { } transcript)
            {
                return;
            }
            record.Machine = MachineName;
            if (record.Folder is { } folder)
            {
                record.Project = ProjectIdentity.Read(folder);
                if (record.Project is not null)
                {
                    record.HadUncommittedChanges = await _services.Git.HasUncommittedChangesAsync(folder).ConfigureAwait(false) == true;
                }
            }
            var subagents = Path.Combine(Path.GetDirectoryName(transcript)!, record.SessionId, SessionLibrary.SubagentsFolderName);
            await Library.SaveAsync(record, transcript, subagents).ConfigureAwait(false);
            if (stillSyncing())
            {
                Leases.Acquire(record.SessionId, Library.GetSessionFolder(record.SessionId));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't copy session {SessionId} to the library.", record.SessionId);
        }
    }

    public LeaseStatus CheckLease(string sessionId) => Leases.Check(Library.GetSessionFolder(sessionId));

    /// <summary>Settings → Sessions → Keep library sessions for. <paramref name="keep"/> is the sessions open here in tabs that sync.</summary>
    public void Prune(IReadOnlySet<string> keep)
    {
        try
        {
            Library.Prune(_services.Settings.Sessions.KeepLibrarySessions.ToTimeSpan(), keep);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't prune the session library.");
        }
    }

    // ---- Settings sync (DESIGN.md §14) ----------------------------------------------------------------------------

    /// <summary>Whether the library already holds settings from another machine: the first-time question.</summary>
    public bool HasSyncedSettings() => SettingsSync.HasRemoteValues(SettingsSync.ReadFile(Library.SettingsSyncFile));

    /// <summary>"Replace them with this machine's": publishes every synced setting from here.</summary>
    public async Task PublishAllSettingsAsync()
    {
        var local = FlattenSettings();
        var result = SettingsSync.PublishAll(local, _services.Time.GetUtcNow(), MachineName);
        await SettingsSync.WriteFileAsync(Library.SettingsSyncFile, result.Remote).ConfigureAwait(true);
        _services.State.SettingsSync = result.State;
        _services.SaveState();
    }

    /// <summary>
    /// Merges this machine's settings with the synced file: newer synced changes are applied here, local edits are
    /// published. Runs on the UI thread, which owns the settings. Does nothing while sync is off.
    /// </summary>
    public async Task SyncSettingsAsync()
    {
        if (!_services.Settings.Sessions.SyncSettings || !await _syncLock.WaitAsync(0).ConfigureAwait(true))
        {
            return;
        }
        try
        {
            var remote = await Task.Run(() => SettingsSync.ReadFile(Library.SettingsSyncFile)).ConfigureAwait(true);
            var result = SettingsSync.Merge(FlattenSettings(), _services.State.SettingsSync ?? new SettingsSyncState(), remote, _services.Time.GetUtcNow(), MachineName);
            if (result.ToApply.Count > 0)
            {
                ApplySettings(result.ToApply);
            }
            if (result.RemoteChanged)
            {
                await SettingsSync.WriteFileAsync(Library.SettingsSyncFile, result.Remote).ConfigureAwait(true);
            }
            _services.State.SettingsSync = result.State;
            _services.SaveState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't sync settings through the library.");
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private Dictionary<string, JsonNode?> FlattenSettings() =>
        SettingsSync.Flatten(SettingsJson(_services.Settings), SyncedSettings, SyncedLeaves);

    private static JsonObject SettingsJson(AppSettings settings) =>
        JsonSerializer.SerializeToNode(settings, JsonFileStore<AppSettings>.Options)!.AsObject();

    private void ApplySettings(IReadOnlyDictionary<string, JsonNode?> values)
    {
        var json = SettingsJson(_services.Settings);
        foreach (var (path, value) in values)
        {
            SettingsSync.Apply(json, path, value);
        }
        var updated = json.Deserialize<AppSettings>(JsonFileStore<AppSettings>.Options);
        if (updated is null)
        {
            return;
        }
        var live = _services.Settings;
        live.Appearance = updated.Appearance;
        live.NewTabs = updated.NewTabs;
        live.Usage = updated.Usage;
        live.CheckIns = updated.CheckIns;
        live.QuickSuffixes = updated.QuickSuffixes;
        live.Processes = updated.Processes;
        live.Notifications = updated.Notifications;
        live.Keyboard = updated.Keyboard;
        _applyingSync = true;
        try
        {
            _services.SaveSettings();
        }
        finally
        {
            _applyingSync = false;
        }
    }

    public void Dispose()
    {
        _syncTimer.Dispose();
        Leases.Dispose();
        _syncLock.Dispose();
    }
}
