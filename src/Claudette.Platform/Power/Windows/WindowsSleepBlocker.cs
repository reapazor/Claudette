using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Claudette.Core.RemoteControl;

namespace Claudette.Platform.Power.Windows;

/// <summary>
/// Holds off system sleep with <c>SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)</c>, and lets it happen
/// again with <c>ES_CONTINUOUS</c> alone (DESIGN.md §18, "Remote Control"). The display can still turn off. The state
/// belongs to the thread that set it and ends with it, so a thread of its own sets and clears it and lives until
/// Claudette closes, rather than a pool thread that could be reused or retired.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsSleepBlocker : ISleepBlocker
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _changed = new(0);
    private readonly Thread _thread;
    private bool _wanted;
    private bool _stopping;
    private volatile bool _blocking;
    private volatile string? _error;

    public WindowsSleepBlocker()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Claudette keep awake" };
        _thread.Start();
    }

    public string? UnavailableReason => null;

    public bool IsBlocking => _blocking;

    public void SetBlocking(bool block)
    {
        lock (_lock)
        {
            if (_stopping || _wanted == block)
            {
                return;
            }
            _wanted = block;
        }
        _changed.Release();
    }

    public string Describe() =>
        _error is { } error ? $"Windows refused to keep the computer awake ({error})."
        : _blocking ? "Keeping the computer awake (SetThreadExecutionState). The display can still sleep."
        : "Not keeping the computer awake now.";

    public void Dispose()
    {
        lock (_lock)
        {
            if (_stopping)
            {
                return;
            }
            _stopping = true;
            _wanted = false;
        }
        _changed.Release();
        // Clearing the state is quick; the thread ends right after it.
        if (_thread.Join(TimeSpan.FromSeconds(2)))
        {
            _changed.Dispose();
        }
    }

    private void Run()
    {
        while (true)
        {
            _changed.Wait();
            bool wanted, stopping;
            lock (_lock)
            {
                wanted = _wanted;
                stopping = _stopping;
            }
            if (wanted != _blocking)
            {
                // Returns the previous state, or 0 when it failed.
                if (SetThreadExecutionState(wanted ? EsContinuous | EsSystemRequired : EsContinuous) == 0)
                {
                    _error = $"error {Marshal.GetLastPInvokeError()}";
                }
                else
                {
                    _error = null;
                    _blocking = wanted;
                }
            }
            if (stopping)
            {
                return;
            }
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint SetThreadExecutionState(uint flags);
}
