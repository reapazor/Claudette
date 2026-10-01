using Claudette.Core.Protocol;
using Claudette.Core.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Core.Tests.Protocol;

/// <summary>Protocol logs leave out sign-in secrets (DESIGN.md §13, "Logging").</summary>
public class ProtocolRedactionTests
{
    [Fact]
    public void The_pasted_sign_in_code_and_its_state_are_redacted()
    {
        var line = """{"type":"control_request","request_id":"r1","request":{"subtype":"claude_oauth_callback","authorizationCode":"abc123","state":"xyz789"}}""";

        var redacted = ProtocolRedaction.Redact(line);

        Assert.DoesNotContain("abc123", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("xyz789", redacted, StringComparison.Ordinal);
        Assert.Contains("\"subtype\":\"claude_oauth_callback\"", redacted, StringComparison.Ordinal);
        Assert.Contains("\"request_id\":\"r1\"", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sign_in_address_keeps_everything_but_its_code_and_state()
    {
        var line = """{"type":"control_response","response":{"subtype":"success","request_id":"r1","response":{"manualUrl":"https://claude.ai/oauth/authorize?code=true&client_id=c1&state=s3cr3t&code_challenge=ch","automaticUrl":"http://localhost:5000/callback?code=c0de#frag"}}}""";

        var redacted = ProtocolRedaction.Redact(line);

        Assert.DoesNotContain("s3cr3t", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("c0de", redacted, StringComparison.Ordinal);
        Assert.Contains("https://claude.ai/oauth/authorize?code=[redacted]&client_id=c1&state=[redacted]&code_challenge=ch", redacted, StringComparison.Ordinal);
        Assert.Contains("http://localhost:5000/callback?code=[redacted]#frag", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("access_token")]
    [InlineData("refreshToken")]
    [InlineData("apiKey")]
    [InlineData("password")]
    public void Secrets_under_their_usual_names_are_redacted_at_any_depth(string key)
    {
        var line = $$$"""{"type":"x","nested":[{"{{{key}}}":"hunter2"},{"{{{key}}}":{"deep":"hunter2"}}]}""";

        var redacted = ProtocolRedaction.Redact(line);

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"text","text":"café ✓ <b>"}]}}""")]
    [InlineData("""{"type":"system","subtype":"bridge_state","state":"connected"}""")]
    [InlineData("""{"type":"user","text":"see https://example.com/?page=2"}""")]
    [InlineData("not json, but says \"password\"")]
    public void Lines_without_secrets_are_written_as_they_were(string line)
    {
        Assert.Same(line, ProtocolRedaction.Redact(line));
    }

    [Fact]
    public void Text_around_a_secret_keeps_its_characters()
    {
        var redacted = ProtocolRedaction.Redact("""{"note":"café ✓ <b>","apiKey":"k"}""");

        Assert.Equal("""{"note":"café ✓ <b>","apiKey":"[redacted]"}""", redacted);
    }

    [Fact]
    public async Task A_protocol_log_never_holds_the_sign_in_code()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("signin.log");
        var inner = new FakeTransport();
        var transport = new LoggingTransport(inner, new ProtocolLog(path, new FakeTimeProvider()));

        await transport.SendAsync("""{"type":"control_request","request_id":"r","request":{"subtype":"claude_oauth_callback","authorizationCode":"abc123","state":"xyz789"}}""",
            TestContext.Current.CancellationToken);
        await transport.DisposeAsync();

        var log = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("abc123", log, StringComparison.Ordinal);
        Assert.DoesNotContain("xyz789", log, StringComparison.Ordinal);
        // What went to Claude Code is untouched.
        Assert.Equal("abc123", inner.Sent.Single()["request"]?["authorizationCode"]?.GetValue<string>());
    }
}
