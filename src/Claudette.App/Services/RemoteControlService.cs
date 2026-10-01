using Claudette.Core.Auth;
using Claudette.Core.RemoteControl;
using Microsoft.Extensions.Logging;

namespace Claudette.App.Services;

/// <summary>
/// What the tabs share about Remote Control, the Claude app's connection to them (DESIGN.md §18, "Remote Control"):
/// whether the account can use it, the presence file that keeps pushes off the phone while Claudette is in front, and
/// keeping the computer awake while a tab is connected. Use on the UI thread.
/// </summary>
public sealed class RemoteControlService : IDisposable
{
    private readonly AppServices _services;
    private readonly ISleepBlocker _sleep;
    private readonly PresenceFile _presence;
    private readonly HashSet<string> _connectedTabs = new(StringComparer.Ordinal);

    public RemoteControlService(AppServices services, ISleepBlocker sleep)
    {
        _services = services;
        _sleep = sleep;
        _presence = new PresenceFile(services.Paths.PresenceFile, services.Loggers.CreateLogger("Presence"));
        EnvironmentVariable = name => _services.UserEnvironment.Current is { } environment
            ? environment.GetValueOrDefault(name)
            : Environment.GetEnvironmentVariable(name);
    }

    /// <summary>
    /// A variable of the environment <c>claude</c> starts with: the login shell's when it's used (DESIGN.md §13). Tests
    /// replace it, so the machine running them doesn't decide.
    /// </summary>
    internal Func<string, string?> EnvironmentVariable { get; set; }

    /// <summary>The latest <c>claude auth status</c>, from the account menu. Null until the first.</summary>
    public AuthStatus? Account { get; private set; }

    /// <summary>Why this account can't use Remote Control, or null when it may. A tab's switch is off and disabled then.</summary>
    public string? UnavailableReason => RemoteControlEligibility.Check(Account, EnvironmentVariable) ?? PolicyReason;

    /// <summary>
    /// What Claude Code said when a policy turned Remote Control off, in any tab. Every tab then stops trying to connect,
    /// rather than each trying again whenever it starts, until Claudette restarts or another account signs in.
    /// </summary>
    public string? PolicyReason { get; private set; }

    /// <summary>A tab heard that a policy turned Remote Control off.</summary>
    public void OnTurnedOffByPolicy(string reason)
    {
        if (PolicyReason is not null)
        {
            return;
        }
        PolicyReason = reason;
        AvailabilityChanged?.Invoke();
    }

    public bool IsAvailable => UnavailableReason is null;

    /// <summary>
    /// Raised on the UI thread when the account changes, which can make Remote Control available or not. Tabs that
    /// want to connect then try again.
    /// </summary>
    public event Action? AvailabilityChanged;

    public void UseAccount(AuthStatus? account)
    {
        var before = UnavailableReason;
        if (account?.Email != Account?.Email || account?.OrganizationName != Account?.OrganizationName)
        {
            // Another account's organization may allow it.
            PolicyReason = null;
        }
        Account = account;
        if (UnavailableReason != before)
        {
            AvailabilityChanged?.Invoke();
        }
    }

    /// <summary>The variables every <c>claude</c> Claudette starts gets on top of its environment.</summary>
    public IReadOnlyDictionary<string, string?> ClaudeVariables => new Dictionary<string, string?> { [PresenceFile.Variable] = _presence.Path };

    // ---- Presence (DESIGN.md §10) --------------------------------------------------------------------------------

    /// <summary>The presence file is there while Claudette's window is in front, so Claude Code doesn't push to the phone.</summary>
    public void SetAppActive(bool active) => _presence.SetPresent(active);

    public bool IsPresent => _presence.IsPresent;

    // ---- Keeping the computer awake -------------------------------------------------------------------------------

    /// <summary>How many tabs are connected to the Claude app now.</summary>
    public int ConnectedTabs => _connectedTabs.Count;

    /// <summary>A tab connected or stopped being connected: sleep is held off while any tab is, if Settings allow.</summary>
    public void SetTabConnected(string tabId, bool connected)
    {
        if (connected ? _connectedTabs.Add(tabId) : _connectedTabs.Remove(tabId))
        {
            UpdateKeepAwake();
        }
    }

    /// <summary>Settings → Claude Code → <b>Keep this computer awake while tabs are connected</b> may have changed.</summary>
    public void OnSettingsChanged() => UpdateKeepAwake();

    private void UpdateKeepAwake() =>
        _sleep.SetBlocking(_connectedTabs.Count > 0 && _services.Settings.ClaudeCode.KeepAwakeWhileConnected);

    /// <summary>For Settings → Advanced → Diagnostics.</summary>
    public string DescribeKeepAwake()
    {
        var tabs = _connectedTabs.Count switch
        {
            0 => "No tab is connected to the Claude app.",
            1 => "1 tab is connected to the Claude app.",
            var n => $"{n} tabs are connected to the Claude app.",
        };
        var setting = _services.Settings.ClaudeCode.KeepAwakeWhileConnected ? "" : " Keeping the computer awake is turned off in Settings → Claude Code.";
        return $"{tabs}{setting} {_sleep.Describe()}";
    }

    /// <summary>At exit: the computer can sleep again, and nobody is at Claudette.</summary>
    public void Dispose()
    {
        _connectedTabs.Clear();
        _sleep.SetBlocking(false);
        _sleep.Dispose();
        _presence.Dispose();
    }
}
