using System.Collections;
using Claudette.Core.Claude;
using Claudette.Core.Sessions;

namespace Claudette.Core.Tests.Claude;

public class ClaudeEnvironmentTests
{
    [Fact]
    public void Removes_variables_inherited_from_a_parent_claude_session()
    {
        var env = ClaudeEnvironment.Create(Source(
            ("CLAUDECODE", "1"),
            ("CLAUDE_CODE_ENTRYPOINT", "sdk-ts"),
            ("CLAUDE_CODE_CHILD_SESSION", "1"),
            ("CLAUDE_CODE_MESSAGING_SOCKET", "pipe"),
            ("AI_AGENT", "claude"),
            ("PATH", "/usr/bin")));

        Assert.Equal(["PATH"], env.Keys);
    }

    [Fact]
    public void Keeps_user_configuration()
    {
        var env = ClaudeEnvironment.Create(Source(
            ("CLAUDE_CONFIG_DIR", "/cfg"),
            ("CLAUDE_CODE_USE_BEDROCK", "1"),
            ("ANTHROPIC_BASE_URL", "http://gateway")));

        Assert.Equal("/cfg", env["CLAUDE_CONFIG_DIR"]);
        Assert.Equal("1", env["CLAUDE_CODE_USE_BEDROCK"]);
        Assert.Equal("http://gateway", env["ANTHROPIC_BASE_URL"]);
    }

    [Fact]
    public void Applies_overrides_and_removals()
    {
        var env = ClaudeEnvironment.Create(
            Source(("KEEP", "a"), ("DROP", "b")),
            new Dictionary<string, string?> { ["ADD"] = "c", ["DROP"] = null });

        Assert.Equal("a", env["KEEP"]);
        Assert.Equal("c", env["ADD"]);
        Assert.False(env.ContainsKey("DROP"));
    }

    [Fact]
    public void Streaming_session_arguments()
    {
        var args = ClaudeArguments.ForStreamingSession(new ClaudeLaunchOptions
        {
            WorkingDirectory = ".",
            Model = "sonnet",
            Effort = "high",
            Resume = "abc",
            PersistSession = false,
        });

        Assert.Equal(
            ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--permission-prompt-tool", "stdio",
             "--include-partial-messages", "--thinking-display", "summarized", "--forward-subagent-text",
             "--model", "sonnet", "--effort", "high", "--resume", "abc", "--no-session-persistence"],
            args);
    }

    [Fact]
    public void Opening_a_copy_forks_the_resumed_session()
    {
        var args = ClaudeArguments.ForStreamingSession(new ClaudeLaunchOptions { WorkingDirectory = ".", Resume = "abc", ForkSession = true });
        var without = ClaudeArguments.ForStreamingSession(new ClaudeLaunchOptions { WorkingDirectory = ".", ForkSession = true });

        Assert.Equal(["--resume", "abc", "--fork-session"], args.SkipWhile(a => a != "--resume"));
        Assert.DoesNotContain("--fork-session", without);
    }

    private static IDictionary Source(params (string Key, string Value)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Value);
}
