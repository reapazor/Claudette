using Claudette.Core.Composer;
using Claudette.Core.Development;

namespace Claudette.App.Services;

/// <summary>
/// Unsent messages on disk (DESIGN.md §5, "Drafts and the stash"): each tab's draft, written a moment after it changes,
/// and the stash every tab shares. Writes run one after another off the UI thread, in the order they were asked for, and
/// stop while Claudette hands over to a new build, which owns the files then.
/// </summary>
public sealed class DraftService
{
    private readonly AppServices _services;
    private readonly DraftStore _store;
    private readonly Lock _lock = new();
    private Task _writes = Task.CompletedTask;
    private List<StashedDraft>? _stash;

    public DraftService(AppServices services)
    {
        _services = services;
        _store = new DraftStore(services.Paths.DraftsDirectory);
    }

    /// <summary>A tab's draft from the last time Claudette ran, or null.</summary>
    public TabDraft? Load(string tabId) => _store.Load(tabId);

    /// <summary>Writes a tab's draft, or deletes it when it's null or empty.</summary>
    public Task SaveAsync(string tabId, TabDraft? draft) => Enqueue(() => _store.Save(tabId, draft));

    /// <summary>Deletes the drafts of tabs that weren't restored: they won't be back.</summary>
    public Task KeepOnlyAsync(IReadOnlyCollection<string> tabIds) => Enqueue(() => _store.KeepOnly(tabIds));

    /// <summary>Waits for every write asked for so far: at shutdown, and in tests.</summary>
    public Task FlushAsync()
    {
        lock (_lock)
        {
            return _writes;
        }
    }

    // ---- The stash ------------------------------------------------------------------------------------------------

    /// <summary>The stash, newest first. Read from disk the first time it's needed.</summary>
    public IReadOnlyList<StashedDraft> Stash => _stash ??= [.. _store.LoadStash()];

    /// <summary>Raised on the UI thread when an entry is stashed or taken out, so every tab's composer follows.</summary>
    public event Action? StashChanged;

    /// <summary>Puts a message in the stash. Called on the UI thread.</summary>
    public void AddToStash(StashedDraft entry)
    {
        _stash = [entry, .. Stash];
        StashChanged?.Invoke();
        _ = Enqueue(() => _store.AddToStash(entry));
    }

    /// <summary>Takes an entry out of the stash; false when it's not there (taken in another tab). Called on the UI thread.</summary>
    public bool RemoveFromStash(string id)
    {
        if (Stash.FirstOrDefault(s => s.Id == id) is not { } entry)
        {
            return false;
        }
        _stash = [.. Stash.Where(s => !ReferenceEquals(s, entry))];
        StashChanged?.Invoke();
        _ = Enqueue(() => _store.RemoveFromStash(id));
        return true;
    }

    private Task Enqueue(Action write)
    {
        if (_services.SuspendSaving)
        {
            return Task.CompletedTask;
        }
        lock (_lock)
        {
            // The store never throws, so one failed write doesn't stop the rest.
            return _writes = _writes.ContinueWith(_ =>
            {
                if (!_services.SuspendSaving)
                {
                    write();
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }
}
