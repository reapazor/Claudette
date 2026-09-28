using Claudette.App.Services;
using Claudette.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>Signs in through Claude Code's own flow (DESIGN.md §11, "Signing in").</summary>
public sealed partial class SignInViewModel(AppServices services, Func<Task> onSignedIn) : ViewModelBase
{
    private CancellationTokenSource? _waiting;
    private SignInUrls? _urls;

    [ObservableProperty]
    public partial string Status { get; set; } = "Claude Code needs you to sign in.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsWaiting { get; set; }

    [ObservableProperty]
    public partial bool ShowCodeEntry { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCodeCommand))]
    public partial string Code { get; set; } = "";

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool IsIdle => !IsWaiting;

    [RelayCommand]
    private async Task SignInAsync()
    {
        Error = null;
        ShowCodeEntry = false;
        try
        {
            var utility = await services.GetUtilitySessionAsync();
            _urls = await utility.StartSignInAsync();
            if (_urls.AutomaticUrl is null && _urls.ManualUrl is null)
            {
                Error = "Claude Code didn't return a sign-in address.";
                return;
            }

            IsWaiting = true;
            Status = "Finish signing in in your browser.";
            if (_urls.AutomaticUrl is null)
            {
                await EnterCodeInsteadAsync();
                return;
            }
            await services.Platform.OpenUrlAsync(_urls.AutomaticUrl);

            _waiting?.Dispose();
            _waiting = new CancellationTokenSource();
            await utility.WaitForSignInAsync(_waiting.Token);
            await FinishAsync();
        }
        catch (OperationCanceledException)
        {
            // Cancelled, or switched to entering a code.
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    [RelayCommand]
    private async Task OpenBrowserAgainAsync()
    {
        var url = ShowCodeEntry ? _urls?.ManualUrl : _urls?.AutomaticUrl;
        if (url is not null)
        {
            await services.Platform.OpenUrlAsync(url);
        }
    }

    [RelayCommand]
    private async Task EnterCodeInsteadAsync()
    {
        _waiting?.Cancel();
        ShowCodeEntry = true;
        IsWaiting = true;
        Status = "Sign in in your browser, then paste the code it shows.";
        if (_urls?.ManualUrl is { } manual)
        {
            await services.Platform.OpenUrlAsync(manual);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSubmitCode))]
    private async Task SubmitCodeAsync()
    {
        Error = null;
        try
        {
            var utility = await services.GetUtilitySessionAsync();
            await utility.SubmitSignInCodeAsync(Code);
            await FinishAsync();
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private bool CanSubmitCode() => Code.Trim().Length > 0;

    [RelayCommand]
    private void Cancel()
    {
        _waiting?.Cancel();
        IsWaiting = false;
        ShowCodeEntry = false;
        Code = "";
        Status = "Claude Code needs you to sign in.";
    }

    /// <summary>For users who signed in another way, such as <c>claude auth login</c> in a terminal.</summary>
    [RelayCommand]
    private Task CheckAgainAsync() => onSignedIn();

    private async Task FinishAsync()
    {
        IsWaiting = false;
        Status = "Signed in.";
        await onSignedIn();
    }

    private void Fail(Exception ex)
    {
        IsWaiting = false;
        ShowCodeEntry = false;
        Status = "Claude Code needs you to sign in.";
        Error = $"Sign-in didn't finish: {ex.Message}";
    }
}
