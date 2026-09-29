using Claudette.App.Services;
using Claudette.Core.Auth;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Claudette.App.ViewModels;

/// <summary>
/// Signs in through Claude Code's own flow (DESIGN.md §11, "Signing in"): the launch screen, and the dialog the
/// sign-in banner opens. The utility session's control requests come first; when they aren't available it runs
/// <c>claude auth login</c> instead, which is also the only way to sign in with SSO.
/// </summary>
public sealed partial class SignInViewModel : ViewModelBase
{
    public const string NeedsSignInText = "Claude Code needs you to sign in.";
    private const string FinishInBrowserText = "Finish signing in in your browser.";

    private readonly AppServices _services;
    private readonly Func<Task> _onSignedIn;
    private readonly Action? _close;
    private readonly ILogger _logger;
    private CancellationTokenSource? _waiting;
    private SignInUrls? _urls;
    private UtilitySession? _utility;
    private ClaudeLogin? _login;
    private SignInMethod _method = SignInMethod.ClaudeAi;

    // Bumped by every start and cancel, so an attempt that was replaced can't finish or fail the current one.
    private int _attempt;

    /// <param name="onSignedIn">Checks the sign-in with <c>claude auth status</c> and goes on from there.</param>
    /// <param name="close">Closes the dialog; null on the launch screen, which has nothing behind it.</param>
    public SignInViewModel(AppServices services, Func<Task> onSignedIn, Action? close = null)
    {
        _services = services;
        _onSignedIn = onSignedIn;
        _close = close;
        _logger = services.Loggers.CreateLogger<SignInViewModel>();
    }

    [ObservableProperty]
    public partial string Status { get; set; } = NeedsSignInText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsWaiting { get; set; }

    public bool IsIdle => !IsWaiting;

    [ObservableProperty]
    public partial bool ShowCodeEntry { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCodeCommand))]
    public partial string Code { get; set; } = "";

    /// <summary>Why the last sign-in didn't finish: Claude Code's own message where there is one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(SignInText))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    /// <summary><b>Try again</b> after a failure, which repeats the same kind of sign-in (DESIGN.md §11).</summary>
    public string SignInText => HasError ? "Try again" : "Sign in";

    /// <summary>The kind of account being signed in to, when it isn't the default one.</summary>
    [ObservableProperty]
    public partial string? MethodText { get; set; }

    /// <summary>Signing in through <c>claude auth login</c> rather than the utility session.</summary>
    [ObservableProperty]
    public partial bool IsUsingCommand { get; set; }

    /// <summary>A dialog over the tabs, which can be closed; the launch screen can't.</summary>
    public bool CanClose => _close is not null;

    [RelayCommand]
    private Task SignInAsync() => StartAsync(HasError ? _method : SignInMethod.ClaudeAi);

    /// <summary><b>More options</b>: an Anthropic Console account, for API usage billing.</summary>
    [RelayCommand]
    private Task SignInWithConsoleAsync() => StartAsync(SignInMethod.Console);

    /// <summary><b>More options</b>: single sign-on.</summary>
    [RelayCommand]
    private Task SignInWithSsoAsync() => StartAsync(SignInMethod.Sso);

    private async Task StartAsync(SignInMethod method)
    {
        StopWaiting();
        var attempt = ++_attempt;
        var waiting = _waiting = new CancellationTokenSource();
        _method = method;
        _urls = null;
        _utility = null;
        Error = null;
        Code = "";
        ShowCodeEntry = false;
        IsWaiting = true;
        IsUsingCommand = false;
        MethodText = method switch
        {
            SignInMethod.Console => "Signing in with an Anthropic Console account (API usage billing).",
            SignInMethod.Sso => "Signing in with single sign-on (SSO).",
            _ => null,
        };
        Status = "Starting sign-in…";
        try
        {
            if (method != SignInMethod.Sso && await TryStartInAppAsync(method, waiting.Token) is { } utility)
            {
                Status = FinishInBrowserText;
                if (_urls?.AutomaticUrl is { } automatic)
                {
                    await _services.Platform.OpenUrlAsync(automatic);
                }
                else
                {
                    await EnterCodeInsteadAsync();
                }
                await utility.WaitForSignInAsync(waiting.Token);
            }
            else
            {
                await RunCommandAsync(method, attempt, waiting.Token);
            }
            if (attempt == _attempt)
            {
                await FinishAsync();
            }
        }
        catch (OperationCanceledException) when (waiting.IsCancellationRequested)
        {
            // Cancelled, or replaced by another sign-in.
        }
        catch (Exception ex)
        {
            if (attempt == _attempt)
            {
                Fail(ex);
            }
        }
    }

    /// <summary>
    /// Starts sign-in through the utility session's <c>claude_authenticate</c>. Null when that isn't available, for
    /// example a Claude Code that doesn't know the request, so the caller falls back to <c>claude auth login</c>.
    /// </summary>
    private async Task<UtilitySession?> TryStartInAppAsync(SignInMethod method, CancellationToken cancellationToken)
    {
        try
        {
            var utility = await _services.GetUtilitySessionAsync(cancellationToken);
            var urls = await utility.StartSignInAsync(method == SignInMethod.ClaudeAi, cancellationToken);
            if (urls.AutomaticUrl is null && urls.ManualUrl is null)
            {
                _logger.LogWarning("claude_authenticate returned no sign-in address; falling back to claude auth login.");
                return null;
            }
            _urls = urls;
            _utility = utility;
            return utility;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sign-in through the utility session isn't available; falling back to claude auth login.");
            return null;
        }
    }

    /// <summary>
    /// <c>claude auth login</c>: it opens the browser itself and prints the address of the page that shows a code,
    /// which <b>Open browser again</b> and <b>Enter a code instead</b> use.
    /// </summary>
    private async Task RunCommandAsync(SignInMethod method, int attempt, CancellationToken cancellationToken)
    {
        if (_services.Auth is not { } auth)
        {
            throw new InvalidOperationException("Claude Code hasn't been found yet.");
        }
        IsUsingCommand = true;
        var login = _login = auth.StartLogin(method);
        login.ErrorLine += line => _services.Dispatcher.Post(() =>
        {
            // A code it couldn't use: it keeps waiting for another one.
            if (attempt == _attempt && ShowCodeEntry)
            {
                Error = line;
            }
        });
        var url = await login.Url.WaitAsync(cancellationToken);
        _urls = new SignInUrls(null, url);
        Status = FinishInBrowserText;
        await login.Completion.WaitAsync(cancellationToken);
    }

    [RelayCommand]
    private async Task OpenBrowserAgainAsync()
    {
        // The command only tells Claudette the address of the page with a code, so that page needs the code field.
        if (IsUsingCommand)
        {
            ShowCodeEntry = true;
        }
        var url = ShowCodeEntry ? _urls?.ManualUrl : _urls?.AutomaticUrl;
        if (url is not null)
        {
            await _services.Platform.OpenUrlAsync(url);
        }
    }

    /// <summary>
    /// For a browser on another device, or a firewall blocking the local port: the page at the manual address shows a
    /// code to paste here. The sign-in already under way keeps waiting, so it still finishes either way.
    /// </summary>
    [RelayCommand]
    private async Task EnterCodeInsteadAsync()
    {
        ShowCodeEntry = true;
        Status = "Sign in in your browser, then paste the code it shows.";
        if (_urls?.ManualUrl is { } manual)
        {
            await _services.Platform.OpenUrlAsync(manual);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSubmitCode))]
    private async Task SubmitCodeAsync()
    {
        var attempt = _attempt;
        Error = null;
        try
        {
            if (_login is { } login)
            {
                // claude auth login reads the code from its input; it finishes the sign-in on its own.
                await login.SubmitCodeAsync(Code);
            }
            else if (_utility is { } utility)
            {
                // The answer comes once the sign-in finished, which the waiting sign-in sees too.
                await utility.SubmitSignInCodeAsync(Code);
            }
        }
        catch (Exception ex)
        {
            if (attempt == _attempt)
            {
                StopWaiting();
                Fail(ex);
            }
        }
    }

    private bool CanSubmitCode() => Code.Trim().Length > 0;

    [RelayCommand]
    private void Cancel()
    {
        StopWaiting();
        _attempt++;
        Error = null;
        MethodText = null;
        IsUsingCommand = false;
        Status = NeedsSignInText;
    }

    /// <summary>Closes the dialog, stopping a sign-in under way.</summary>
    [RelayCommand]
    private void Close()
    {
        Cancel();
        _close?.Invoke();
    }

    /// <summary>For users who signed in another way, such as <c>claude auth login</c> in a terminal.</summary>
    [RelayCommand]
    private Task CheckAgainAsync() => _onSignedIn();

    /// <summary><c>claude auth status</c> still reports no sign-in after the user said they signed in.</summary>
    public void ShowStillSignedOut()
    {
        StopWaiting();
        Status = NeedsSignInText;
        Error = "Claude Code still reports that you're not signed in.";
    }

    /// <summary><c>claude auth status</c> couldn't be read.</summary>
    public void ShowCheckFailed(string message)
    {
        StopWaiting();
        Status = NeedsSignInText;
        Error = message;
    }

    private async Task FinishAsync()
    {
        StopWaiting();
        Status = "Signed in.";
        MethodText = null;
        await _onSignedIn();
    }

    private void Fail(Exception ex)
    {
        StopWaiting();
        Status = "Sign-in didn't finish.";
        // Claude Code's own words where it gave some: timed out, cancelled, organization not allowed.
        Error = ex switch
        {
            SignInFailedException failed => failed.Message,
            ControlRequestException rejected => rejected.Error,
            TimeoutException => "Sign-in timed out. Try again.",
            ClaudeSessionExitedException => "Claude Code stopped while signing in.",
            _ => ex.Message,
        };
    }

    /// <summary>Stops waiting on the current sign-in, and ends <c>claude auth login</c> if it's running.</summary>
    private void StopWaiting()
    {
        IsWaiting = false;
        ShowCodeEntry = false;
        Code = "";
        // Not disposed: the attempt it belonged to may still be reading its token.
        _waiting?.Cancel();
        _waiting = null;
        if (_login is { } login)
        {
            _login = null;
            login.Cancel();
            _ = login.DisposeAsync().AsTask();
        }
    }
}
