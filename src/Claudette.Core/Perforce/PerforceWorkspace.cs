namespace Claudette.Core.Perforce;

/// <summary>
/// Where Claudette runs <c>p4</c> for a tab: the tab's folder, so Perforce resolves its settings from the usual
/// sources (P4CONFIG files, <c>p4 set</c>, P4ENVIRO, the environment) the same way Claude's own <c>p4</c> commands do,
/// plus the server and user from Settings → Perforce's per-folder overrides, if any (DESIGN.md §18).
/// </summary>
public sealed record PerforceTarget(string Folder, string? Port = null, string? User = null)
{
    /// <summary><c>-p</c> and <c>-u</c>, for the overrides that are set.</summary>
    public IReadOnlyList<string> GlobalOptions()
    {
        var options = new List<string>();
        if (!string.IsNullOrWhiteSpace(Port))
        {
            options.Add("-p");
            options.Add(Port);
        }
        if (!string.IsNullOrWhiteSpace(User))
        {
            options.Add("-u");
            options.Add(User);
        }
        return options;
    }
}

/// <summary>A folder that is inside a Perforce workspace, from <c>p4 -ztag info</c> and <c>p4 set</c> (DESIGN.md §18).</summary>
/// <param name="Port">The P4PORT <c>p4</c> connects to from this folder, when it's set anywhere; null means Perforce's default.</param>
/// <param name="User">The Perforce user.</param>
/// <param name="Client">The workspace (client) name.</param>
/// <param name="ClientRoot">The workspace's root folder, when it has one.</param>
/// <param name="ServerAddress">The server's own address, as it reports it.</param>
/// <param name="UsesSingleSignOn">P4LOGINSSO is set, or the server requires single sign-on: Claudette can't log in by itself.</param>
public sealed record PerforceWorkspace(
    string? Port,
    string User,
    string Client,
    string? ClientRoot,
    string? ServerAddress,
    string? ServerVersion,
    bool UsesSingleSignOn)
{
    /// <summary>The server as shown, and as passwords are stored under: the P4PORT, else the server's own address.</summary>
    public string Server => Port ?? ServerAddress ?? "perforce:1666";

    /// <summary>
    /// The note added to the session with <c>--append-system-prompt</c>, so Claude knows to use <c>p4</c> here rather
    /// than assume git, and leaves logging in to Claudette (DESIGN.md §18, "Detecting a Perforce workspace").
    /// </summary>
    public string SystemPromptNote(string? user = null)
    {
        var root = ClientRoot is null ? "" : $", rooted at {ClientRoot}";
        return $"This folder is in a Perforce workspace: server {Server}, user {user ?? User}, workspace (client) {Client}{root}. "
            + "Use p4 for source control here rather than assuming git. "
            + "The app you're running in keeps the Perforce login fresh: never run p4 login or pass a password yourself. "
            + "If a p4 command fails because the session expired, wait for the message saying the login was renewed, then retry it.";
    }

    /// <summary>
    /// Reads <c>p4 -ztag info</c> run in <paramref name="folder"/>. Null unless the folder is in an existing workspace:
    /// the client must be known (not <c>*unknown*</c>) and the folder under its root.
    /// </summary>
    /// <param name="port">The P4PORT from <c>p4 set</c> or the per-folder override, if any.</param>
    /// <param name="loginSso">The P4LOGINSSO from <c>p4 set</c>, if any.</param>
    public static PerforceWorkspace? FromInfo(string infoZtag, string folder, string? port, string? loginSso)
    {
        var info = ZTag.ParseSingle(infoZtag);
        if (!info.TryGetValue("userName", out var user) || user.Length == 0
            || !info.TryGetValue("clientName", out var client) || client.Length == 0 || client == "*unknown*")
        {
            return null;
        }
        var root = info.GetValueOrDefault("clientRoot") is { Length: > 0 } r && r != "null" ? r : null;
        if (root is not null && !IsUnder(folder, root))
        {
            return null;
        }
        var sso = !string.IsNullOrWhiteSpace(loginSso)
            || string.Equals(info.GetValueOrDefault("ssoAuth"), "required", StringComparison.OrdinalIgnoreCase);
        return new PerforceWorkspace(
            string.IsNullOrWhiteSpace(port) ? null : port.Trim(),
            user,
            client,
            root,
            info.GetValueOrDefault("serverAddress"),
            info.GetValueOrDefault("serverVersion"),
            sso);
    }

    /// <summary>The value of <c>p4 set -q NAME</c>: <c>NAME=value</c>, or nothing when it isn't set.</summary>
    public static string? ParseSetting(string setOutput, string name)
    {
        foreach (var raw in setOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                var value = line[(name.Length + 1)..];
                // Without -q, p4 set adds where the value came from, such as " (config '/ws/.p4config')".
                var source = value.LastIndexOf(" (", StringComparison.Ordinal);
                if (source > 0 && value.EndsWith(')'))
                {
                    value = value[..source];
                }
                return value.Length > 0 ? value : null;
            }
        }
        return null;
    }

    private static bool IsUnder(string folder, string root)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            // A drive or file system root keeps its separator.
            var prefix = Path.EndsInDirectorySeparator(top) ? top : top + Path.DirectorySeparatorChar;
            return full.Equals(top, comparison) || full.StartsWith(prefix, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
