using System.Collections;

namespace Claudette.Core.Claude;

/// <summary>
/// Builds the environment for a <c>claude</c> child process (DESIGN.md §13, "Clean environment").
/// </summary>
public static class ClaudeEnvironment
{
    /// <summary>
    /// Variables that Claude Code sets for the processes it starts. If Claudette was itself started from inside a
    /// Claude Code session, these are inherited and make <c>claude</c> behave as that session's child: in the
    /// milestone 1 spike it ignored the given credentials and reported "Not logged in". User configuration such as
    /// <c>CLAUDE_CONFIG_DIR</c> or <c>CLAUDE_CODE_USE_BEDROCK</c> is deliberately not on this list.
    /// Tracked in compat/surface.yaml.
    /// </summary>
    public static readonly IReadOnlySet<string> SessionVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CLAUDECODE",
        "CLAUDE_CODE_ENTRYPOINT",
        "CLAUDE_CODE_CHILD_SESSION",
        "CLAUDE_CODE_SESSION_ID",
        "CLAUDE_CODE_SESSION_ATTENDED",
        "CLAUDE_CODE_MESSAGING_SOCKET",
        "CLAUDE_CODE_MESSAGING_TOKEN",
        "CLAUDE_CODE_EXECPATH",
        "CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING",
        "CLAUDE_CODE_ENABLE_TASKS",
        "CLAUDE_CODE_SSE_PORT",
        "CLAUDE_PID",
        "CLAUDE_EFFORT",
        "CLAUDE_AGENT_SDK_VERSION",
        "AI_AGENT",
    };

    /// <summary>The current process environment, cleaned, with <paramref name="overrides"/> applied.</summary>
    public static IReadOnlyDictionary<string, string> Create(IReadOnlyDictionary<string, string?>? overrides = null) =>
        Create(Environment.GetEnvironmentVariables(), overrides);

    public static IReadOnlyDictionary<string, string> Create(IDictionary source, IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var result = new Dictionary<string, string>(comparer);
        foreach (DictionaryEntry entry in source)
        {
            if (entry.Key is string key && entry.Value is string value && !SessionVariables.Contains(key))
            {
                result[key] = value;
            }
        }
        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                if (value is null)
                {
                    result.Remove(key);
                }
                else
                {
                    result[key] = value;
                }
            }
        }
        return result;
    }
}
