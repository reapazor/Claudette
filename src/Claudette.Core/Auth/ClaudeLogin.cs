using System.Text.RegularExpressions;
using Claudette.Core.Processes;

namespace Claudette.Core.Auth;

/// <summary>
/// A running <c>claude auth login</c>: the documented fallback when the sign-in control requests aren't available, and
/// the only way to sign in with SSO (DESIGN.md §11, "Signing in"). As Claude Code 2.1.284 does it with no terminal
/// attached, it opens the browser itself at the address that finishes on its own, prints the address of the page that
/// shows a code instead (<c>If the browser didn't open, visit: …</c>), and reads that code, <c>code#state</c>, from
/// its input.
/// </summary>
public sealed partial class ClaudeLogin : IAsyncDisposable
{
    private readonly IRunningProcess _process;
    private readonly TaskCompletionSource<string?> _url = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cancel = new();
    private readonly List<string> _errors = [];

    private ClaudeLogin(IRunningProcess process, TimeSpan timeout, TimeProvider timeProvider)
    {
        _process = process;
        var output = ReadOutputAsync();
        var errors = ReadErrorsAsync();
        Completion = WaitAsync(output, errors, timeout, timeProvider);
    }

    /// <summary>The arguments for each way of signing in.</summary>
    public static IReadOnlyList<string> Arguments(SignInMethod method) => method switch
    {
        SignInMethod.Console => ["auth", "login", "--console"],
        SignInMethod.Sso => ["auth", "login", "--sso"],
        _ => ["auth", "login"],
    };

    /// <summary>Starts <c>claude auth login</c>. Give up after <paramref name="timeout"/>.</summary>
    public static ClaudeLogin Start(IProcessLauncher launcher, ProcessStartSpec spec, TimeSpan timeout, TimeProvider timeProvider) =>
        new(launcher.Start(spec), timeout, timeProvider);

    /// <summary>
    /// The address of the page that shows a code to paste. Completes once it's printed, or with null if the command
    /// ended without printing one.
    /// </summary>
    public Task<string?> Url => _url.Task;

    /// <summary>
    /// Completes when the command signed in. Fails with a <see cref="SignInFailedException"/> carrying the command's
    /// message when it didn't (timed out, cancelled in the browser, organization not allowed), and is cancelled by
    /// <see cref="Cancel"/>.
    /// </summary>
    public Task Completion { get; }

    /// <summary>
    /// A line the command printed as an error while still running, such as a code it couldn't use ("Invalid code.
    /// Please make sure the full code was copied."). Raised on a background thread.
    /// </summary>
    public event Action<string>? ErrorLine;

    /// <summary>Gives the command the code from the browser's page, as the user pasted it.</summary>
    public ValueTask SubmitCodeAsync(string code, CancellationToken cancellationToken = default) =>
        _process.WriteLineAsync(code.Trim(), cancellationToken);

    /// <summary>Stops waiting and ends the command.</summary>
    public void Cancel()
    {
        _cancel.Cancel();
        _process.Kill();
    }

    /// <summary>The first web address in a line, without the terminal escape codes a hyperlink may be wrapped in.</summary>
    public static string? FindUrl(string line) =>
        UrlPattern().Match(StripEscapes(line)) is { Success: true } match ? match.Value.TrimEnd('.', ',', ')') : null;

    /// <summary>Removes terminal colour codes and OSC 8 hyperlinks, keeping their text.</summary>
    public static string StripEscapes(string text) => EscapePattern().Replace(text, "");

    private async Task ReadOutputAsync()
    {
        await foreach (var line in _process.StandardOutput.ReadAllAsync().ConfigureAwait(false))
        {
            if (!_url.Task.IsCompleted && FindUrl(line) is { } url)
            {
                _url.TrySetResult(url);
            }
        }
    }

    private async Task ReadErrorsAsync()
    {
        await foreach (var line in _process.StandardError.ReadAllAsync().ConfigureAwait(false))
        {
            var clean = StripEscapes(line).Trim();
            if (clean.Length == 0)
            {
                continue;
            }
            lock (_errors)
            {
                _errors.Add(clean);
            }
            ErrorLine?.Invoke(clean);
        }
    }

    private async Task WaitAsync(Task output, Task errors, TimeSpan timeout, TimeProvider timeProvider)
    {
        using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token, timeoutSource.Token);
        int exitCode;
        try
        {
            exitCode = await _process.Exited.WaitAsync(linked.Token).ConfigureAwait(false);
            await Task.WhenAll(output, errors).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !_cancel.IsCancellationRequested)
        {
            _process.Kill();
            throw new SignInFailedException("Sign-in timed out. Try again.");
        }
        finally
        {
            _url.TrySetResult(null);
        }
        _cancel.Token.ThrowIfCancellationRequested();
        if (exitCode != 0)
        {
            string message;
            lock (_errors)
            {
                message = _errors.Count > 0
                    ? string.Join('\n', _errors)
                    : $"'claude auth login' ended with exit code {exitCode}.";
            }
            throw new SignInFailedException(message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.Exited.IsCompleted)
        {
            _process.Kill();
        }
        try
        {
            await Completion.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // How it ended was already reported to whoever was waiting.
        }
        await _process.DisposeAsync().ConfigureAwait(false);
        _cancel.Dispose();
    }

    [GeneratedRegex(@"https?://[^\s""'<>]+")]
    private static partial Regex UrlPattern();

    // CSI sequences (colours) and OSC sequences such as OSC 8 hyperlinks, ended by BEL or ESC \.
    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(\x07|\x1B\\)")]
    private static partial Regex EscapePattern();
}
