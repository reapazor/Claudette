using System.Text.Json.Nodes;
using Claudette.Core.Protocol;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Core.Tests.Protocol;

public class ControlChannelTests
{
    [Fact]
    public async Task The_timeout_starts_before_the_request_is_sent()
    {
        // The clock moves on while the request is being sent, as when a test sees it go out and advances the clock.
        var time = new FakeTimeProvider();
        var timeout = TimeSpan.FromMinutes(10);
        var channel = new ControlChannel((_, _) =>
        {
            time.Advance(timeout);
            return ValueTask.CompletedTask;
        }, time);

        var request = channel.RequestAsync(new JsonObject { ["subtype"] = "claude_oauth_wait_for_completion" }, timeout, TestContext.Current.CancellationToken);

        Assert.True(request.IsCompleted, "The request should have timed out rather than wait for a clock that already passed its deadline.");
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => request);
        Assert.Contains("claude_oauth_wait_for_completion", ex.Message, StringComparison.Ordinal);
    }
}
