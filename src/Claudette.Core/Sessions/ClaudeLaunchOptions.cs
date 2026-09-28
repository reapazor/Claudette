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

    /// <summary>False adds <c>--no-session-persistence</c>, as the utility session does.</summary>
    public bool PersistSession { get; init; } = true;

    public bool IncludePartialMessages { get; init; } = true;

    public IReadOnlyList<string> AdditionalArguments { get; init; } = [];

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
        AddOption(args, "--model", options.Model);
        AddOption(args, "--effort", options.Effort);
        AddOption(args, "--permission-mode", options.PermissionMode);
        AddOption(args, "--resume", options.Resume);
        if (!options.PersistSession)
        {
            args.Add("--no-session-persistence");
        }
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
