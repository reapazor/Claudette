namespace Claudette.Core.RemoteControl;

/// <summary>
/// Keeps the computer from going to sleep while tabs are connected to the Claude app, so the phone can still reach them
/// (DESIGN.md §18, "Remote Control"). Only system sleep: the display can still turn off. The implementations are in
/// Claudette.Platform: <c>SetThreadExecutionState</c> on Windows, <c>caffeinate</c> on macOS, <c>systemd-inhibit</c>
/// on Linux.
/// </summary>
public interface ISleepBlocker : IDisposable
{
    /// <summary>Why this machine can't be kept awake, for Diagnostics, or null when it can.</summary>
    string? UnavailableReason { get; }

    /// <summary>Whether sleep is being held off now.</summary>
    bool IsBlocking { get; }

    /// <summary>Holds sleep off, or lets it happen again. Asking for what's already so does nothing.</summary>
    void SetBlocking(bool block);

    /// <summary>What it's doing, in words, for Settings → Advanced → Diagnostics.</summary>
    string Describe();
}

/// <summary>No way to keep this machine awake: tests, and platforms without one.</summary>
public sealed class NoSleepBlocker(string reason = "Claudette can't keep this computer awake here.") : ISleepBlocker
{
    public string? UnavailableReason => reason;

    public bool IsBlocking => false;

    public void SetBlocking(bool block)
    {
    }

    public string Describe() => $"Not available: {reason}";

    public void Dispose()
    {
    }
}
