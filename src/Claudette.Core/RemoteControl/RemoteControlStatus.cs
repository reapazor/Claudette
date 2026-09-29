namespace Claudette.Core.RemoteControl;

/// <summary>Where a tab stands with Remote Control, its connection to claude.ai and the Claude app (DESIGN.md §18).</summary>
public enum RemoteControlState
{
    NotConnected,

    /// <summary>Claudette asked Claude Code to connect and is waiting for the answer.</summary>
    Connecting,

    Connected,

    /// <summary>Claude Code said it can't connect, and why: the account, the endpoint or a policy.</summary>
    Unavailable,
}

/// <summary>A tab's Remote Control connection.</summary>
/// <param name="Url">The session on claude.ai, when Claude Code gave its address: what <b>Open in the Claude app</b> opens.</param>
/// <param name="Detail">
/// What Claude Code said: why it isn't connected or available, that a connected session is reconnecting, or a reply
/// Claudette couldn't read as either.
/// </param>
public sealed record RemoteControlStatus(RemoteControlState State, string? Url = null, string? Detail = null)
{
    public static RemoteControlStatus NotConnected { get; } = new(RemoteControlState.NotConnected);

    public static RemoteControlStatus Connecting { get; } = new(RemoteControlState.Connecting);

    public bool IsConnected => State == RemoteControlState.Connected;
}

/// <summary>The <c>state</c> of a <c>system/bridge_state</c> message: how Claude Code's link to claude.ai is doing.</summary>
public enum RemoteBridgeState
{
    /// <summary>A state this Claudette doesn't know yet. It changes nothing.</summary>
    Unknown,

    /// <summary>Registered with claude.ai; devices can find the session.</summary>
    Ready,

    Connected,

    /// <summary>The link dropped and Claude Code is getting it back, for example after the network changed.</summary>
    Reconnecting,

    /// <summary>The link couldn't be made, or was lost for good.</summary>
    Failed,

    /// <summary>A policy turned Remote Control off: the organization's, or <c>disableRemoteControl</c>.</summary>
    PolicyDisabled,
}
