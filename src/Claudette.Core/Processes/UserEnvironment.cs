using System.Collections;
using System.Globalization;
using Claudette.Core.Claude;
using Claudette.Core.Diffs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudette.Core.Processes;

/// <summary>
/// The environment Claudette starts the user's processes with: <c>claude</c>, and git, <c>p4</c>, diff tools and
/// Homebrew, and the <c>PATH</c> they're found on (DESIGN.md §13, "Login shell environment"). On macOS and Linux, when
/// Claudette wasn't started from a terminal and Settings → Claude Code allows it, the login shell's environment is
/// read once in the background and merged in, as a terminal would have it. Otherwise it's Claudette's own.
/// </summary>
/// <remarks>Safe to use from any thread.</remarks>
public sealed class UserEnvironment
{
    /// <summary>What the shell sets about itself. They describe the shell Claudette ran, not the user's setup.</summary>
    public static readonly IReadOnlySet<string> ShellBookkeeping = new HashSet<string>(StringComparer.Ordinal) { "_", "PWD", "OLDPWD", "SHLVL" };

    /// <summary>Claudette's own variables, such as <c>CLAUDETTE_HOME</c>, always come from Claudette's environment.</summary>
    public const string ClaudettePrefix = "CLAUDETTE_";

    private const int MaxNamesShown = 30;

    private readonly ILoginShell? _loginShell;
    private readonly Func<bool> _isEnabled;
    private readonly ILogger _logger;
    private readonly IReadOnlyDictionary<string, string>? _own;
    private readonly Lock _lock = new();
    private Task? _reading;
    private volatile LoginShellResult? _result;
    private volatile Merged? _merged;

    /// <param name="loginShell">Reads the login shell. Null never reads one: tests, and the default.</param>
    /// <param name="isEnabled">Settings → Claude Code → <b>Use my login shell's environment</b>, read each time it matters.</param>
    /// <param name="ownEnvironment">Claudette's own environment. By default, this process's.</param>
    public UserEnvironment(ILoginShell? loginShell, Func<bool> isEnabled, ILogger? logger = null, IReadOnlyDictionary<string, string>? ownEnvironment = null)
    {
        _loginShell = loginShell;
        _isEnabled = isEnabled;
        _logger = logger ?? NullLogger.Instance;
        _own = ownEnvironment;
        Probe = new FileProbe(() => PathVariable);
    }

    /// <summary>Claudette's own environment, with no login shell.</summary>
    public static UserEnvironment Inherited { get; } = new(null, static () => false);

    /// <summary>Finds programs on the <see cref="PathVariable"/>, for diff tool presets, P4V and Homebrew.</summary>
    public IFileProbe Probe { get; }

    /// <summary>
    /// Starts reading the login shell in the background, if it's turned on, needed, and hasn't been read this run.
    /// Called at launch, and when the setting changes, so turning it on reads it then. Does nothing otherwise.
    /// </summary>
    public void Start()
    {
        if (_loginShell is not { NotNeeded: null } shell || !_isEnabled())
        {
            return;
        }
        lock (_lock)
        {
            _reading ??= Task.Run(() => ReadAsync(shell));
        }
    }

    /// <summary>
    /// The environment to start a user's process with, or null to start it with Claudette's own. The first call after
    /// the login shell started being read waits for it, up to the shell's timeout.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>?> GetAsync(CancellationToken cancellationToken = default)
    {
        Start();
        Task? reading;
        lock (_lock)
        {
            reading = _reading;
        }
        if (reading is not null && _isEnabled())
        {
            await reading.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        return Current;
    }

    /// <summary>What <see cref="GetAsync"/> gives, without waiting: null until the login shell has been read.</summary>
    public IReadOnlyDictionary<string, string>? Current => _isEnabled() ? _merged?.Environment : null;

    /// <summary>The <c>PATH</c> the user's processes get.</summary>
    public string? PathVariable => Current is { } environment
        ? environment.GetValueOrDefault("PATH") ?? Environment.GetEnvironmentVariable("PATH")
        : Environment.GetEnvironmentVariable("PATH");

    /// <summary>
    /// <paramref name="spec"/> ready to start as a user's process, once the login shell is read: with its environment,
    /// and a bare program name (<c>git</c>, <c>p4</c>) found on its <c>PATH</c>, because .NET looks for one on
    /// Claudette's own. Unchanged when the login shell isn't used.
    /// </summary>
    public async Task<ProcessStartSpec> ApplyAsync(ProcessStartSpec spec, CancellationToken cancellationToken = default) =>
        Apply(spec, await GetAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>As <see cref="ApplyAsync"/>, without waiting for the login shell, for a start that can't wait.</summary>
    public ProcessStartSpec Apply(ProcessStartSpec spec) => Apply(spec, Current);

    public static ProcessStartSpec Apply(ProcessStartSpec spec, IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null)
        {
            return spec;
        }
        var fileName = spec.FileName;
        if (IsBareName(fileName) && FileProbe.FindIn(environment.GetValueOrDefault("PATH"), fileName) is { } found)
        {
            fileName = found;
        }
        return spec with { FileName = fileName, Environment = spec.Environment ?? environment };
    }

    /// <summary>
    /// Claudette's own environment with the login shell's on top: the shell's values win, except for Claudette's own
    /// variables (<see cref="ClaudettePrefix"/>), the session variables Claude Code sets for its children (removed for
    /// <c>claude</c> anyway, <see cref="ClaudeEnvironment.SessionVariables"/>) and the shell's
    /// <see cref="ShellBookkeeping"/>. Variables only Claudette has are kept.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Merge(IReadOnlyDictionary<string, string> own, IReadOnlyDictionary<string, string> login)
    {
        var result = new Dictionary<string, string>(own, Comparer);
        foreach (var (key, value) in login)
        {
            if (!key.StartsWith(ClaudettePrefix, StringComparison.OrdinalIgnoreCase)
                && !ClaudeEnvironment.SessionVariables.Contains(key)
                && !ShellBookkeeping.Contains(key))
            {
                result[key] = value;
            }
        }
        return result;
    }

    /// <summary>This process's environment.</summary>
    public static IReadOnlyDictionary<string, string> Snapshot()
    {
        var result = new Dictionary<string, string>(Comparer);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                result[key] = value;
            }
        }
        return result;
    }

    /// <summary>
    /// For Settings → Advanced → Diagnostics: whether the login shell's environment is used, which shell and how long
    /// it took, or why not. Names variables, but never their values.
    /// </summary>
    public string Describe()
    {
        if (_loginShell is not { } shell)
        {
            return "Not used: Claudette has no login shell to read here.";
        }
        if (!_isEnabled())
        {
            return "Not used: turned off in Settings → Claude Code.";
        }
        if (shell.NotNeeded is { } notNeeded)
        {
            return notNeeded switch
            {
                LoginShellOutcome.NotNeededOnWindows => "Not used: not needed on Windows, where apps get your full environment.",
                LoginShellOutcome.StartedFromTerminal => "Not used: Claudette was started from a terminal, so it has your shell's environment already.",
                _ => $"Not used: {notNeeded}.",
            };
        }
        if (_result is not { } result)
        {
            return $"Reading the environment of {shell.Shell}…";
        }
        if (result.Outcome != LoginShellOutcome.Used || _merged is not { } merged)
        {
            return $"Not used: {result.Shell} {result.Detail ?? "failed"}. New processes get Claudette's own environment.";
        }
        var took = result.Duration is { } duration ? $", read in {duration.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s" : "";
        var changed = merged.Changed.Count == 0
            ? "none added or changed"
            : $"{merged.Changed.Count} added or changed: {string.Join(", ", merged.Changed.Take(MaxNamesShown))}"
              + (merged.Changed.Count > MaxNamesShown ? $" and {merged.Changed.Count - MaxNamesShown} more" : "");
        return $"Used: {result.Shell}{took}. {result.Environment?.Count ?? 0} variables, {changed}.";
    }

    private async Task ReadAsync(ILoginShell shell)
    {
        LoginShellResult result;
        try
        {
            result = await shell.ReadAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The shell promises not to throw; if it does anyway, nothing waits on a failed task.
            _logger.LogWarning(ex, "Reading the login shell's environment failed.");
            result = new LoginShellResult(LoginShellOutcome.Failed, shell.Shell, Detail: "failed");
        }
        if (result is { Outcome: LoginShellOutcome.Used, Environment: { } login })
        {
            var own = _own ?? Snapshot();
            var environment = Merge(own, login);
            var changed = environment
                .Where(e => !own.TryGetValue(e.Key, out var before) || before != e.Value)
                .Select(e => e.Key)
                .Order(StringComparer.Ordinal)
                .ToArray();
            _merged = new Merged(environment, changed);
            _logger.LogInformation("Using the environment of {Shell}, read in {Duration}: {Count} variables, {Changed} added or changed.",
                result.Shell, result.Duration, login.Count, changed.Length);
        }
        else
        {
            _logger.LogWarning("Not using the login shell's environment: {Shell} {Detail}", result.Shell, result.Detail);
        }
        _result = result;
    }

    private static bool IsBareName(string fileName) =>
        fileName.Length > 0 && !Path.IsPathRooted(fileName) && fileName.IndexOfAny(['/', Path.DirectorySeparatorChar]) < 0;

    private static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <param name="Changed">The names whose values differ from Claudette's own, for Diagnostics.</param>
    private sealed record Merged(IReadOnlyDictionary<string, string> Environment, IReadOnlyList<string> Changed);
}
