using Claudette.Core.Perforce;
using Claudette.Core.Processes;
using Claudette.Core.Sessions;
using Claudette.IntegrationTests.Support;

namespace Claudette.IntegrationTests;

/// <summary>
/// Hook callbacks through <c>fake-claude</c>, and <c>p4 login</c> reading its password from a real pipe (DESIGN.md §13,
/// §18). The p4 here is a small shell script, so those tests run on macOS and Linux only.
/// </summary>
public sealed class PerforceIntegrationTests : IDisposable
{
    private readonly TempFolder _work = new("claudette-p4");
    private readonly ClaudeSessionFactory _factory = new(FakeClaude.Path, new ProcessLauncher(), TimeProvider.System);

    public void Dispose() => _work.Dispose();

    [Fact]
    public async Task A_PreToolUse_hook_is_called_back_before_the_command_runs()
    {
        var calls = new List<HookInput>();
        await using var session = await StartAsync(new HookRegistration("PreToolUse", "Bash", async (input, _) =>
        {
            lock (calls)
            {
                calls.Add(input);
            }
            await Task.Delay(200, TestContext.Current.CancellationToken);
            return HookOutputs.Continue();
        }, TimeSpan.FromSeconds(30)));

        await session.SendUserMessageAsync("RUN_BASH p4 edit -c 12345 a.cpp", TestContext.Current.CancellationToken);
        var (done, seen) = await session.ReadUntilAsync<TurnCompleted>();

        var call = Assert.Single(calls);
        Assert.Equal(("PreToolUse", "Bash", "p4 edit -c 12345 a.cpp"), (call.EventName, call.ToolName, call.Command));
        Assert.Equal("Done with the tool.", done.Result.Result);
        var result = seen.OfType<ToolResultsReceived>().Single().Message;
        Assert.Equal(call.ToolUseId, result.Content.OfType<Core.Protocol.ToolResultBlock>().Single().ToolUseId);
    }

    [Fact]
    public async Task A_hook_that_doesnt_answer_in_time_is_withdrawn_and_the_command_doesnt_run()
    {
        var withdrawn = new TaskCompletionSource();
        await using var session = await StartAsync(new HookRegistration("PreToolUse", "Bash", async (_, token) =>
        {
            await using (token.Register(() => withdrawn.TrySetResult()))
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            return HookOutputs.Continue();
        }, TimeSpan.FromSeconds(1)));

        await session.SendUserMessageAsync("RUN_BASH p4 sync", TestContext.Current.CancellationToken);
        var (done, seen) = await session.ReadUntilAsync<TurnCompleted>();

        await withdrawn.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("The hook timed out.", done.Result.Result);
        Assert.True(seen.OfType<ToolResultsReceived>().Single().Message.Content.OfType<Core.Protocol.ToolResultBlock>().Single().IsError);
    }

    [Fact]
    public async Task P4_login_reads_the_password_from_a_real_pipe()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake p4 is a shell script.");
        var p4 = WriteFakeP4();
        var client = new PerforceClient(new ProcessLauncher(), TimeProvider.System, p4);
        var target = new PerforceTarget(_work.Path);
        var workspace = await client.DetectAsync(target, TestContext.Current.CancellationToken);
        Assert.NotNull(workspace);
        Assert.Equal(TicketState.NotLoggedIn, (await client.GetTicketStatusAsync(target, workspace, TestContext.Current.CancellationToken)).State);

        var wrong = await client.LoginAsync(target, workspace, "wrong", allHosts: false, TestContext.Current.CancellationToken);
        var right = await client.LoginAsync(target, workspace, "s3cret", allHosts: true, TestContext.Current.CancellationToken);

        Assert.Equal((false, "Password invalid."), (wrong.Succeeded, wrong.Message));
        Assert.Equal((true, "User matt logged in."), (right.Succeeded, right.Message));
        var status = await client.GetTicketStatusAsync(target, workspace, TestContext.Current.CancellationToken);
        Assert.Equal((TicketState.Valid, TimeSpan.FromHours(12)), (status.State, status.ExpiresIn));
        var commandLines = await File.ReadAllTextAsync(_work.Combine("p4-args.txt"), TestContext.Current.CancellationToken);
        Assert.Contains("-p ssl:perforce:1666 -u matt login -a", commandLines, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", commandLines, StringComparison.Ordinal);
    }

    private async Task<ClaudeSession> StartAsync(HookRegistration hook) =>
        await _factory.StartAsync(new ClaudeLaunchOptions { WorkingDirectory = _work.Path, Hooks = [hook] }, TestContext.Current.CancellationToken);

    /// <summary>
    /// A p4 that knows <c>-ztag info</c>, <c>set -q P4PORT</c>, <c>-ztag login -s</c> and <c>login</c>, which reads the
    /// password from standard input. It notes each command line in p4-args.txt, and the ticket in a file.
    /// </summary>
    private string WriteFakeP4()
    {
        var path = _work.Combine("p4");
        File.WriteAllText(path, $$"""
            #!/bin/sh
            echo "$*" >> p4-args.txt
            case "$*" in
              *"-ztag info"*) printf '... userName matt\n... clientName matt-ws\n... clientRoot %s\n... serverAddress perforce:1666\n' "{{_work.Path}}" ;;
              *"set -q P4PORT"*) echo "P4PORT=ssl:perforce:1666" ;;
              *"login -s"*)
                if [ -f ticket ]; then printf '... User matt\n... TicketExpiration 43200\n'; else echo "Perforce password (P4PASSWD) invalid or unset." >&2; exit 1; fi ;;
              *login*)
                printf 'Enter password: '
                read password
                if [ "$password" = "s3cret" ]; then touch ticket; echo; echo "User matt logged in."; else echo; echo "Password invalid." >&2; exit 1; fi ;;
            esac
            """.Replace("\r\n", "\n", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }
}
