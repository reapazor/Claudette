using Claudette.App.Services;
using Claudette.Core.Auth;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Claudette.App.ViewModels;

/// <summary>
/// Runs the startup checks (Claude Code installed, then signed in) and shows the matching page (DESIGN.md §2, §11).
/// </summary>
public sealed partial class MainWindowViewModel(AppServices services, string? initialFolder = null) : ViewModelBase, IAsyncDisposable
{
    private WorkspaceViewModel? _workspace;
    private string? _initialFolder = initialFolder;

    [ObservableProperty]
    public partial ViewModelBase CurrentPage { get; set; } = new BusyViewModel("Starting…");

    [ObservableProperty]
    public partial string? AccountText { get; set; }

    public Task StartAsync() => CheckInstallAsync(null);

    private async Task CheckInstallAsync(string? path)
    {
        CurrentPage = new BusyViewModel("Looking for Claude Code…");
        var result = await services.Locator.LocateAsync(path);
        if (!result.IsUsable)
        {
            CurrentPage = new SetupViewModel(result, services.Platform, CheckInstallAsync);
            return;
        }
        services.UseInstall(result.Install!);
        await CheckSignInAsync();
    }

    private async Task CheckSignInAsync()
    {
        if (CurrentPage is not SignInViewModel)
        {
            CurrentPage = new BusyViewModel("Checking your sign-in…");
        }
        AuthStatus status;
        try
        {
            status = await services.Auth!.GetStatusAsync();
        }
        catch (Exception ex)
        {
            CurrentPage = new SignInViewModel(services, CheckSignInAsync) { Error = $"Couldn't check your sign-in: {ex.Message}" };
            return;
        }

        if (!status.LoggedIn)
        {
            AccountText = null;
            if (CurrentPage is not SignInViewModel)
            {
                CurrentPage = new SignInViewModel(services, CheckSignInAsync);
            }
            return;
        }

        AccountText = string.Join(" · ", new[] { status.Email, PlanName(status.SubscriptionType) }.Where(s => !string.IsNullOrEmpty(s)));
        if (_workspace is null)
        {
            _workspace = new WorkspaceViewModel(services, ShowSignIn);
        }
        else
        {
            await _workspace.OnSignedInAgainAsync();
        }
        CurrentPage = _workspace;

        if (_initialFolder is { } folder)
        {
            _initialFolder = null;
            await _workspace.OpenFolderAsync(folder);
        }
    }

    /// <summary>A tab reported that Claude Code needs a sign-in.</summary>
    private void ShowSignIn()
    {
        if (CurrentPage is not SignInViewModel)
        {
            CurrentPage = new SignInViewModel(services, CheckSignInAsync);
        }
    }

    private static string? PlanName(string? subscriptionType) => subscriptionType switch
    {
        null or "" => null,
        "max" => "Max",
        "pro" => "Pro",
        _ => subscriptionType,
    };

    public async ValueTask DisposeAsync()
    {
        if (_workspace is not null)
        {
            await _workspace.DisposeAsync();
        }
    }
}
