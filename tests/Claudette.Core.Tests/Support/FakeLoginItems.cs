using Claudette.Core.LoginItems;

namespace Claudette.Core.Tests.Support;

/// <summary>
/// Stands in for the OS's login entry (DESIGN.md §9, "Starting at login"): the entry, the MSIX's startup task and the
/// copies of Claudette on the machine, kept in memory. Nothing touches the registry or the user's folders.
/// </summary>
public sealed class FakeLoginItems : ILoginItems
{
    private readonly List<ClaudetteCopy> _installed = [];

    public string? UnavailableReason { get; set; }

    public string SystemSettingsName => "Task Manager's Startup apps";

    public LoginEntryState Entry { get; set; }

    /// <summary>The copy the entry was last written to start, or null when it hasn't been since it was last removed.</summary>
    public ClaudetteCopy? EntryStarts { get; private set; }

    public int Writes { get; private set; }

    /// <summary>The MSIX's startup task, for a fake Claudette running from the package.</summary>
    public FakePackageTask? MsixTask { get; set; }

    public IPackageStartupTask? PackageTask => MsixTask;

    /// <summary>The copies handed over to at login.</summary>
    public List<ClaudetteCopy> Started { get; } = [];

    /// <summary>What writing the entry throws, to see an error reported.</summary>
    public Exception? WriteFailure { get; set; }

    /// <summary>Puts copies of Claudette on the machine, for <see cref="Exists"/>.</summary>
    public FakeLoginItems Install(params ClaudetteCopy[] copies)
    {
        _installed.AddRange(copies);
        return this;
    }

    public void Uninstall(ClaudetteCopy copy) => _installed.RemoveAll(copy.IsSameCopy);

    public LoginEntryState ReadEntry() => Entry;

    public void WriteEntry(ClaudetteCopy copy)
    {
        if (WriteFailure is { } failure)
        {
            throw failure;
        }
        Entry = LoginEntryState.Enabled;
        EntryStarts = copy;
        Writes++;
    }

    public void DeleteEntry()
    {
        Entry = LoginEntryState.Missing;
        EntryStarts = null;
    }

    public bool Exists(ClaudetteCopy copy) => _installed.Any(copy.IsSameCopy);

    public void Start(ClaudetteCopy copy) => Started.Add(copy);
}

/// <summary>The MSIX's startup task, as Windows keeps it: the user and policies can hold it on or off.</summary>
public sealed class FakePackageTask(PackageTaskState state = PackageTaskState.Disabled) : IPackageStartupTask
{
    public PackageTaskState State { get; set; } = state;

    public Task<PackageTaskState> GetStateAsync(CancellationToken cancellationToken = default) => System.Threading.Tasks.Task.FromResult(State);

    public Task<PackageTaskState> RequestEnableAsync(CancellationToken cancellationToken = default)
    {
        if (State == PackageTaskState.Disabled)
        {
            State = PackageTaskState.Enabled;
        }
        return System.Threading.Tasks.Task.FromResult(State);
    }

    public Task DisableAsync(CancellationToken cancellationToken = default)
    {
        if (State == PackageTaskState.Enabled)
        {
            State = PackageTaskState.Disabled;
        }
        return System.Threading.Tasks.Task.CompletedTask;
    }
}
