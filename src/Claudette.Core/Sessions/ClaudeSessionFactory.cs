using Claudette.Core.Claude;
using Claudette.Core.Processes;
using Claudette.Core.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudette.Core.Sessions;

public interface IClaudeSessionFactory
{
    /// <summary>Starts a <c>claude</c> process and completes the <c>initialize</c> handshake.</summary>
    Task<ClaudeSession> StartAsync(ClaudeLaunchOptions options, CancellationToken cancellationToken = default);
}

/// <param name="environment">The user environment each <c>claude</c> starts from (DESIGN.md §13). Null: Claudette's own.</param>
public sealed class ClaudeSessionFactory(
    string claudePath,
    IProcessLauncher launcher,
    TimeProvider timeProvider,
    ILoggerFactory? loggerFactory = null,
    ProtocolDiagnostics? diagnostics = null,
    UserEnvironment? environment = null) : IClaudeSessionFactory
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    private static IReadOnlyDictionary<string, string?> WithStartupFailureResults(IReadOnlyDictionary<string, string?>? overrides)
    {
        var merged = overrides is null ? new Dictionary<string, string?>(StringComparer.Ordinal) : new Dictionary<string, string?>(overrides, StringComparer.Ordinal);
        merged.TryAdd(StartupFailure.ResultsVariable, "1");
        return merged;
    }

    public async Task<ClaudeSession> StartAsync(ClaudeLaunchOptions options, CancellationToken cancellationToken = default)
    {
        var userEnvironment = environment is null ? null : await environment.GetAsync(cancellationToken).ConfigureAwait(false);
        var spec = new ProcessStartSpec(claudePath, ClaudeArguments.ForStreamingSession(options))
        {
            WorkingDirectory = options.WorkingDirectory,
            // A refused start says why in a result, for every reason (DESIGN.md §4, "Why it couldn't start").
            Environment = ClaudeEnvironment.From(userEnvironment, WithStartupFailureResults(options.EnvironmentOverrides)),
            TrackProcessTree = true,
        };
        // The log first: if it can't be opened, no claude has been started to leave running.
        var log = options.ProtocolLogPath is { } logPath ? new ProtocolLog(logPath, timeProvider) : null;
        IClaudeTransport transport;
        try
        {
            transport = new ProcessClaudeTransport(launcher.Start(spec), _loggerFactory.CreateLogger<ProcessClaudeTransport>());
        }
        catch
        {
            log?.Dispose();
            throw;
        }
        if (log is not null)
        {
            transport = new LoggingTransport(transport, log);
        }
        var session = new ClaudeSession(transport, timeProvider, _loggerFactory.CreateLogger<ClaudeSession>(), diagnostics, options.ShowsElicitations);
        try
        {
            await session.InitializeAsync(options.Hooks, options.AgentProgressSummaries, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
