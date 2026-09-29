using Claudette.Core.RemoteControl;

namespace Claudette.App.Tests.Support;

/// <summary>Stands in for keeping the computer awake (DESIGN.md §18): records each change instead of touching the OS.</summary>
internal sealed class FakeSleepBlocker : ISleepBlocker
{
    private readonly List<bool> _changes = [];

    public string? UnavailableReason => null;

    public bool IsBlocking { get; private set; }

    /// <summary>Each time blocking started (true) or stopped (false).</summary>
    public IReadOnlyList<bool> Changes
    {
        get
        {
            lock (_changes)
            {
                return [.. _changes];
            }
        }
    }

    public bool IsDisposed { get; private set; }

    public void SetBlocking(bool block)
    {
        if (block == IsBlocking)
        {
            return;
        }
        IsBlocking = block;
        lock (_changes)
        {
            _changes.Add(block);
        }
    }

    public string Describe() => IsBlocking ? "Keeping the computer awake (fake)." : "Not keeping the computer awake now.";

    public void Dispose() => IsDisposed = true;
}
