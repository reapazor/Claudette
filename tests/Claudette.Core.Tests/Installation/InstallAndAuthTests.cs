using Claudette.Core.Auth;
using Claudette.Core.Installation;

namespace Claudette.Core.Tests.Installation;

public class InstallAndAuthTests
{
    [Theory]
    [InlineData("2.1.284 (Claude Code)", "2.1.284")]
    [InlineData("2.1.290\n", "2.1.290")]
    [InlineData("claude 10.2.3-beta", "10.2.3")]
    public void Reads_versions(string output, string expected)
    {
        Assert.True(ClaudeLocator.TryParseVersion(output, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Fact]
    public void Rejects_output_without_a_version()
    {
        Assert.False(ClaudeLocator.TryParseVersion("command not found", out _));
    }

    [Fact]
    public void Reads_signed_in_status()
    {
        const string json = """
            {
              "loggedIn": true,
              "authMethod": "claude.ai",
              "apiProvider": "firstParty",
              "email": "someone@example.com",
              "orgName": "Example",
              "subscriptionType": "max",
              "configDirectory": "C:\\Users\\me\\.claude",
              "projectsDirectory": "C:\\Users\\me\\.claude\\projects"
            }
            """;

        Assert.True(ClaudeAuth.TryParseStatus(json, out var status));
        Assert.True(status.LoggedIn);
        Assert.Equal("max", status.SubscriptionType);
        Assert.Equal(@"C:\Users\me\.claude\projects", status.ProjectsDirectory);
    }

    [Fact]
    public void Reads_signed_out_status()
    {
        Assert.True(ClaudeAuth.TryParseStatus("""{"loggedIn": false, "authMethod": "none", "apiProvider": "firstParty"}""", out var status));
        Assert.False(status.LoggedIn);
        Assert.Null(status.Email);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Error: something")]
    [InlineData("""{"other": 1}""")]
    public void Rejects_unreadable_status(string output)
    {
        Assert.False(ClaudeAuth.TryParseStatus(output, out _));
    }
}
