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
    public void A_worktree_and_extra_folders_add_their_flags()
    {
        var args = ClaudeArguments.ForStreamingSession(new ClaudeLaunchOptions
        {
            WorkingDirectory = ".",
            Worktree = "brisk-otter",
            AddDirectories = ["/src/lib", "/src/docs"],
            Resume = "abc",
        });
        var plain = ClaudeArguments.ForStreamingSession(new ClaudeLaunchOptions { WorkingDirectory = "." });

        // One --add-dir each: the option takes several values, so a value is never read as anything else.
        Assert.Equal(["--worktree", "brisk-otter", "--add-dir", "/src/lib", "--add-dir", "/src/docs", "--resume", "abc"],
            args.SkipWhile(a => a != "--worktree").Take(8));
        Assert.DoesNotContain("--worktree", plain);
        Assert.DoesNotContain("--add-dir", plain);
    }

    [Fact]
    public void Opening_a_copy_forks_the_resumed_session()
    {
        var args = ClaudeArguments.ForStreamingSession(new ClaudeLaunchOptions { WorkingDirectory = ".", Resume = "abc", ForkSession = true });
        var without = ClaudeArguments.ForStreamingSession(new ClaudeLaunchOptions { WorkingDirectory = ".", ForkSession = true });

        Assert.Equal(["--resume", "abc", "--fork-session"], args.SkipWhile(a => a != "--resume"));
        Assert.DoesNotContain("--fork-session", without);
    }

    [Fact]
    public void A_name_is_what_other_sessions_message_it_by()
    {
        var args = ClaudeArguments.ForStreamingSession(new ClaudeLaunchOptions { WorkingDirectory = ".", Resume = "abc", Name = "Art page" });
        var without = ClaudeArguments.ForStreamingSession(new ClaudeLaunchOptions { WorkingDirectory = "." });

        Assert.Equal(["--name", "Art page"], args.SkipWhile(a => a != "--name").Take(2));
        Assert.DoesNotContain("--name", without);
    }

    [Fact]
    public void Rewinding_branching_hooks_and_a_fallback_model_add_their_flags()
    {
        var args = ClaudeArguments.ForStreamingSession(new ClaudeLaunchOptions
        {
            WorkingDirectory = ".",
            Resume = "abc",
            ForkSession = true,
            ResumeSessionAt = "a-9",
            ResumeDropsTurn = "u-9",
            ReplayUserMessages = true,
            IncludeHookEvents = true,
            FallbackModel = "sonnet",
            SettingSources = "user",
        });
        // Without a session to resume, there's nothing to resume at.
        var fresh = ClaudeArguments.ForStreamingSession(new ClaudeLaunchOptions { WorkingDirectory = ".", ResumeSessionAt = "a-9", ResumeDropsTurn = "u-9" });

        Assert.Equal(
            ["--fallback-model", "sonnet", "--setting-sources", "user", "--resume", "abc", "--fork-session", "--resume-session-at", "a-9", "--resume-drops-turn", "u-9", "--replay-user-messages", "--include-hook-events"],
            args.SkipWhile(a => a != "--fallback-model"));
        Assert.DoesNotContain("--resume-session-at", fresh);
        Assert.DoesNotContain("--resume-drops-turn", fresh);
    }

    private static IDictionary Source(params (string Key, string Value)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Value);
}
