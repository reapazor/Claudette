using Claudette.Core.Sessions;
using Claudette.Core.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Core.Tests.Sessions;

/// <summary>The hidden utility session's local commands, such as <c>/usage</c> (DESIGN.md §6, "Data source").</summary>
public sealed class UtilitySessionTests : IDisposable
{
    private readonly TempFolder _temp = new("claudette-utility");
    private readonly FakeTimeProvider _time = new();
    private readonly FakeTransport _transport = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task A_result_that_comes_after_its_command_timed_out_isnt_taken_as_the_next_ones()
    {
        await using var utility = await UtilitySession.StartAsync(new Factory(_transport, _time), _temp.Path, TestContext.Current.CancellationToken);

        var first = utility.RunLocalCommandAsync("/usage", TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await _transport.WaitForSentAsync(m => m["type"]?.GetValue<string>() == "user");
        _time.Advance(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<TimeoutException>(() => first);

        var second = utility.RunLocalCommandAsync("/usage", TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _transport.Emit("""{"type":"result","subtype":"success","is_error":false,"result":"the first one's, late"}""");
        _transport.Emit("""{"type":"result","subtype":"success","is_error":false,"result":"the second one's"}""");

        Assert.Equal("the second one's", await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    private sealed class Factory(FakeTransport transport, TimeProvider time) : IClaudeSessionFactory
    {
        public async Task<ClaudeSession> StartAsync(ClaudeLaunchOptions options, CancellationToken cancellationToken = default)
        {
            var session = new ClaudeSession(transport, time);
            await session.InitializeAsync(cancellationToken);
            return session;
        }
    }
}
