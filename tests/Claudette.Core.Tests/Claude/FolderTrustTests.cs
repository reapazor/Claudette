using Claudette.Core.Claude;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.Claude;

/// <summary>What a folder's own configuration runs in a <c>claude -p</c> session, and which folders are trusted (DESIGN.md §7, "Folder trust").</summary>
public sealed class FolderTrustTests : IDisposable
{
    private readonly TempFolder _root = new("claudette-trust");

    public void Dispose() => _root.Dispose();

    private string Project => _root.Combine("repo", "api");

    [Fact]
    public void A_folder_without_its_own_configuration_runs_nothing()
    {
        Directory.CreateDirectory(Project);
        // Settings that run nothing don't count.
        _root.Write("repo/api/.claude/settings.json", """{"permissions":{"defaultMode":"plan"},"spinnerVerbs":{"verbs":["Pondering"]}}""");

        Assert.Empty(FolderTrust.WhatRuns(Project));
    }

    [Fact]
    public void Lists_hooks_commands_environment_names_plugins_and_local_allow_rules()
    {
        _root.Write("repo/api/.claude/settings.json", """
            {
              // Comments and trailing commas, as Claude Code allows.
              "hooks": {
                "PreToolUse": [{ "matcher": "Bash", "hooks": [{ "type": "command", "command": "./guard.sh" }] }],
                "SessionStart": [{ "hooks": [{ "type": "command", "command": "npm install" }, { "type": "http", "url": "https://hooks.example/start" }] }],
              },
              "apiKeyHelper": "vault read key",
              "env": { "NODE_OPTIONS": "--require ./x.js", "SECRET_TOKEN": "abc123" },
              "enabledPlugins": { "formatter@acme": true, "off@acme": false },
              "permissions": { "allow": ["Bash(rm:*)"] },
            }
            """);
        _root.Write("repo/api/.claude/settings.local.json", """{"permissions":{"allow":["Bash(npm test:*)"]},"awsAuthRefresh":"aws sso login"}""");

        var found = FolderTrust.WhatRuns(Project);

        var settings = Path.Combine(".claude", "settings.json");
        var local = Path.Combine(".claude", "settings.local.json");
        Assert.Equal(
            [
                new ProjectSetting(ProjectSettingKind.Hook, settings, "PreToolUse (Bash): ./guard.sh"),
                new ProjectSetting(ProjectSettingKind.Hook, settings, "SessionStart: npm install"),
                new ProjectSetting(ProjectSettingKind.Hook, settings, "SessionStart: sends to https://hooks.example/start"),
                new ProjectSetting(ProjectSettingKind.Helper, settings, "apiKeyHelper: vault read key"),
                new ProjectSetting(ProjectSettingKind.Environment, settings, "NODE_OPTIONS, SECRET_TOKEN"),
                new ProjectSetting(ProjectSettingKind.Plugin, settings, "formatter@acme"),
                // The shared file's allow rules wait for Claude Code's own trust; the local file's apply.
                new ProjectSetting(ProjectSettingKind.Helper, local, "awsAuthRefresh: aws sso login"),
                new ProjectSetting(ProjectSettingKind.AllowRule, local, "Bash(npm test:*)"),
            ],
            found);
        // Values can be secrets: only the names are shown.
        Assert.DoesNotContain(found, s => s.Text.Contains("abc123", StringComparison.Ordinal));
    }

    [Fact]
    public void Lists_mcp_servers_from_the_folder_up_and_skills_with_hooks()
    {
        _root.Write("repo/api/.mcp.json", """{"mcpServers":{"github":{"command":"npx","args":["-y","@acme/github"]}}}""");
        // A parent's .mcp.json counts too, past the repository; a name already listed nearer is left out.
        _root.Write(".mcp.json", """{"mcpServers":{"docs":{"type":"http","url":"https://mcp.example/docs"},"github":{"command":"other"}}}""");
        _root.Write("repo/api/.claude/skills/deploy/SKILL.md", "---\nname: deploy\nhooks:\n  PreToolUse: []\n---\nDeploys.\n");
        _root.Write("repo/api/.claude/skills/notes/SKILL.md", "---\nname: notes\n---\nhooks: in the body don't count\n");
        // Not valid JSON: skipped, as Claude Code skips it.
        _root.Write("repo/.mcp.json", "{ not json");

        var found = FolderTrust.WhatRuns(Project);

        Assert.Equal(
            [
                new ProjectSetting(ProjectSettingKind.SkillHook, Path.Combine(".claude", "skills", "deploy", "SKILL.md"), "deploy"),
                new ProjectSetting(ProjectSettingKind.McpServer, ".mcp.json", "github: npx -y @acme/github"),
                new ProjectSetting(ProjectSettingKind.McpServer, Path.Combine("..", "..", ".mcp.json"), "docs: https://mcp.example/docs"),
            ],
            found.Where(s => s.File.StartsWith(".claude", StringComparison.Ordinal) || s.File.EndsWith(".mcp.json", StringComparison.Ordinal))
                .Where(s => !s.File.StartsWith(Path.Combine("..", "..", ".."), StringComparison.Ordinal)));
    }

    [Fact]
    public void A_folder_inside_a_trusted_one_is_trusted_but_not_a_sibling_or_a_parent()
    {
        var trusted = new[] { _root.Combine("repo") + Path.DirectorySeparatorChar };

        Assert.True(FolderTrust.IsTrusted(trusted, _root.Combine("repo")));
        Assert.True(FolderTrust.IsTrusted(trusted, Project));
        Assert.False(FolderTrust.IsTrusted(trusted, _root.Combine("repo-other")));
        Assert.False(FolderTrust.IsTrusted(trusted, _root.Path));
        Assert.False(FolderTrust.IsTrusted([], Project));
    }
}
