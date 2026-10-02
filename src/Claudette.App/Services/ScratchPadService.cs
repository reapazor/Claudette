using Claudette.App.ViewModels;
using Claudette.Core.ScratchPads;

namespace Claudette.App.Services;

/// <summary>
/// The scratch pads (DESIGN.md §18, "Scratch pad"): one per project, the same object for every open tab in it. Keeps the
/// pads tabs have open, and syncs those with a git remote through the session library once a minute and when Claudette
/// comes to the front. A pad no tab has open any more is saved and let go.
/// </summary>
public sealed class ScratchPadService : IDisposable
{
    /// <summary>How often the open pads look for other machines' changes.</summary>
    internal static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(1);

    /// <summary>Coming to the front syncs at most this often, so switching windows back and forth doesn't.</summary>
    internal static readonly TimeSpan ActivationSyncInterval = TimeSpan.FromSeconds(10);

    private readonly AppServices _services;
    private readonly Dictionary<string, Entry> _pads = new(StringComparer.Ordinal);
    private readonly ITimer _timer;
    private DateTimeOffset _lastActivationSync = DateTimeOffset.MinValue;

    public ScratchPadService(AppServices services)
    {
        _services = services;
        Store = new ScratchPadStore(services.Paths.ScratchPadsDirectory, () => services.Library.LibraryFolder);
        _timer = services.Time.CreateTimer(_ => services.Dispatcher.Post(SyncAll), null, SyncInterval, SyncInterval);
    }

    internal ScratchPadStore Store { get; }

    /// <summary>The pads open now: for tests.</summary>
    internal IEnumerable<ScratchPadViewModel> Open() => _pads.Values.Select(e => e.Pad);

    /// <summary>
    /// The pad for a tab in <paramref name="folder"/>, which it gives back with <see cref="Release"/> when it closes or
    /// moves to another folder.
    /// </summary>
    /// <param name="name">What to call the project: its tab group's folder name.</param>
    public ScratchPadViewModel Acquire(string folder, string name)
    {
        var project = ScratchPadProject.For(folder);
        if (!_pads.TryGetValue(project.Id, out var entry))
        {
            entry = new Entry(new ScratchPadViewModel(_services, Store, project, name));
            _pads[project.Id] = entry;
            _ = entry.Pad.SyncAsync();
        }
        entry.Users++;
        return entry.Pad;
    }

    /// <summary>A tab has finished with <paramref name="pad"/>: once no tab has it, it's saved and let go.</summary>
    public void Release(ScratchPadViewModel pad)
    {
        if (!_pads.TryGetValue(pad.Project.Id, out var entry) || entry.Pad != pad || --entry.Users > 0)
        {
            return;
        }
        _ = LetGoAsync(entry);
    }

    private async Task LetGoAsync(Entry entry)
    {
        await entry.Pad.FlushAsync();
        // A tab may have opened it again meanwhile.
        if (entry.Users == 0 && _pads.TryGetValue(entry.Pad.Project.Id, out var current) && current == entry)
        {
            _pads.Remove(entry.Pad.Project.Id);
            entry.Pad.Dispose();
        }
    }

    /// <summary>Claudette's window came to the front: look for other machines' changes, unless it just did.</summary>
    public void OnAppActivated()
    {
        var now = _services.Time.GetUtcNow();
        if (now - _lastActivationSync < ActivationSyncInterval)
        {
            return;
        }
        _lastActivationSync = now;
        SyncAll();
    }

    /// <summary>Settings changed: the session library may be in another folder now.</summary>
    public void OnSettingsChanged()
    {
        foreach (var entry in _pads.Values)
        {
            entry.Pad.OnSettingsChanged();
        }
        SyncAll();
    }

    private void SyncAll()
    {
        foreach (var entry in _pads.Values.ToList())
        {
            _ = entry.Pad.SyncAsync();
        }
    }

    /// <summary>Saves every pad, for closing Claudette or restarting into a new build.</summary>
    public Task FlushAsync() => Task.WhenAll(_pads.Values.Select(e => e.Pad.FlushAsync()).ToList());

    public void Dispose()
    {
        _timer.Dispose();
        foreach (var entry in _pads.Values)
        {
            entry.Pad.Dispose();
        }
        _pads.Clear();
    }

    private sealed class Entry(ScratchPadViewModel pad)
    {
        public ScratchPadViewModel Pad { get; } = pad;

        public int Users { get; set; }
    }
}
