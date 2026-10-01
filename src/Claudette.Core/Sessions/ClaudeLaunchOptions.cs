namespace Claudette.Core.Sessions;

/// <summary>How to start one <c>claude</c> process in streaming mode (DESIGN.md §13, "Launch").</summary>
public sealed record ClaudeLaunchOptions
{
    public required string WorkingDirectory { get; init; }

    public string? Model { get; init; }

    public string? Effort { get; init; }

    public string? PermissionMode { get; init; }

    /// <summary>A session ID, or the path of a <c>.jsonl</c> transcript, to resume.</summary>
    public string? Resume { get; init; }

    /// <summary>With <see cref="Resume"/>: continue as a new session (a copy) instead of the original (DESIGN.md §9, "One machine at a time").</summary>
    public bool ForkSession { get; init; }

    /// <summary>
    /// With <see cref="Resume"/>: resume only up to and including this transcript entry (its <c>uuid</c>), dropping what
    /// came after, to rewind the conversation or branch from a message (DESIGN.md §5, "Rewind and branch").
    /// </summary>
    public string? ResumeSessionAt { get; init; }

    /// <summary>
    /// With <see cref="ResumeSessionAt"/>: the prompt <c>uuid</c> of the turn the truncated resume means to drop. Claude
    /// Code refuses the resume, rather than drop more, when anything else is after the point.
    /// </summary>
    public string? ResumeDropsTurn { get; init; }

    /// <summary>
    /// Echo each prompt back with its <c>uuid</c> (<c>--replay-user-messages</c>): the points the conversation and its files
    /// can be rewound to.
    /// </summary>
    public bool ReplayUserMessages { get; init; }

    /// <summary>Hook runs as <c>system/hook_*</c> messages (<c>--include-hook-events</c>), for the conversation's hook rows (DESIGN.md §5).</summary>
    public bool IncludeHookEvents { get; init; }

    /// <summary>The model to fall back to when the chosen one is overloaded (<c>--fallback-model</c>). Null: none.</summary>
    public string? FallbackModel { get; init; }

    /// <summary>False adds <c>--no-session-persistence</c>, as the utility session does.</summary>
    public bool PersistSession { get; init; } = true;

    public bool IncludePartialMessages { get; init; } = true;

    /// <summary>
    /// <c>summarized</c> returns thinking text; newer models otherwise send empty thinking blocks (DESIGN.md §5).
    /// Null leaves Claude Code's default. The flag isn't in <c>--help</c>; the Agent SDKs pass it.
    /// </summary>
    public string? ThinkingDisplay { get; init; } = "summarized";

    /// <summary>Sends subagents' text and thinking too, so subagent groups can show them (DESIGN.md §5).</summary>
    public bool ForwardSubagentText { get; init; } = true;

    public IReadOnlyList<string> AdditionalArguments { get; init; } = [];

    /// <summary>Writes the session's raw protocol traffic to this file (DESIGN.md §13, "Logging"). Null: no log.</summary>
    public string? ProtocolLogPath { get; init; }

    /// <summary>Added to the default system prompt with <c>--append-system-prompt</c>, such as the Perforce workspace note (DESIGN.md §18).</summary>
    public string? AppendSystemPrompt { get; init; }

    /// <summary>Hook callbacks registered with <c>initialize</c> (DESIGN.md §13, "Hook callbacks"). Not command-line arguments.</summary>
    public IReadOnlyList<HookRegistration> Hooks { get; init; } = [];

    /// <summary>
    /// <c>--setting-sources</c>: the settings Claude Code loads, such as <c>user</c> to leave out the folder's own
    /// (DESIGN.md §7, "Folder trust"). Null: all of them.
    /// </summary>
    public string? SettingSources { get; init; }

    /// <summary>
    /// The host shows MCP servers' requests for input, from the start (<see cref="ClaudeSession.ShowsElicitations"/>).
    /// Not a command-line argument.
    /// </summary>
    public bool ShowsElicitations { get; init; }

    /// <summary>Applied on top of the clean environment. A null value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?> EnvironmentOverrides { get; init; } = new Dictionary<string, string?>();
}

public static class ClaudeArguments
{
    /// <summary>The command-line arguments for a long-running stream-json session.</summary>
    public static IReadOnlyList<string> ForStreamingSession(ClaudeLaunchOptions options)
    {
        var args = new List<string>
        {
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            // Sends permission prompts to Claudette as can_use_tool control requests.
            "--permission-prompt-tool", "stdio",
        };
        if (options.IncludePartialMessages)
        {
            args.Add("--include-partial-messages");
        }
        AddOption(args, "--thinking-display", options.ThinkingDisplay);
        if (options.ForwardSubagentText)
        {
            args.Add("--forward-subagent-text");
        }
        AddOption(args, "--model", options.Model);
        AddOption(args, "--effort", options.Effort);
        AddOption(args, "--permission-mode", options.PermissionMode);
        AddOption(args, "--fallback-model", options.FallbackModel);
        AddOption(args, "--setting-sources", options.SettingSources);
        AddOption(args, "--resume", options.Resume);
        if (options.ForkSession && options.Resume is not null)
        {
            args.Add("--fork-session");
        }
        if (options.Resume is not null)
        {
            AddOption(args, "--resume-session-at", options.ResumeSessionAt);
            if (options.ResumeSessionAt is not null)
            {
                AddOption(args, "--resume-drops-turn", options.ResumeDropsTurn);
            }
        }
        if (options.ReplayUserMessages)
        {
            args.Add("--replay-user-messages");
        }
        if (options.IncludeHookEvents)
        {
            args.Add("--include-hook-events");
        }
        if (!options.PersistSession)
        {
            args.Add("--no-session-persistence");
        }
        AddOption(args, "--append-system-prompt", options.AppendSystemPrompt);
        args.AddRange(options.AdditionalArguments);
        return args;
    }

    private static void AddOption(List<string> args, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            args.Add(name);
            args.Add(value);
        }
    }
}
