using Claudette.App.Services;
using Claudette.Core.Auth;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Claudette.App.ViewModels;

/// <summary>
/// Runs the startup checks (Claude Code installed, then signed in) and shows the matching page (DESIGN.md §2, §11).
/// </summary>
public sealed partial class MainWindowViewModel(AppServices services, string? initialFolder = null) : ViewModelBase, IAsyncDisposable
{
    private ShellViewModel? _shell;

    [ObservableProperty]
    public partial ViewModelBase CurrentPage { get; set; } = new BusyViewModel("Starting…");

    [ObservableProperty]
    public partial string? AccountText { get; set; }

    /// <summary>The main UI, once Claude Code is installed and signed in.</summary>
    public ShellViewModel? Shell => _shell;

    public AppServices Services => services;

    public Task StartAsync() => CheckInstallAsync(services.Settings.ClaudeCode.ClaudePath);

    private async Task CheckInstallAsync(string? path)
    {
        CurrentPage = new BusyViewModel("Looking for Claude Code…");
        var result = await services.Locator.LocateAsync(path);
        if (!result.IsUsable)
        {
            CurrentPage = new SetupViewModel(result, services.Platform, CheckInstallAsync);
            return;
        }
        if (path is not null && path != services.Settings.ClaudeCode.ClaudePath)
        {
            // The user pointed Claudette at claude from the setup screen: remember it.
            services.Settings.ClaudeCode.ClaudePath = path;
            services.SaveSettings();
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

        services.ProjectsDirectory = status.ProjectsDirectory
            ?? (status.ConfigDirectory is { } config ? Path.Combine(config, "projects") : null);
        AccountText = string.Join(" · ", new[] { status.Email, PlanName(status.SubscriptionType) }.Where(s => !string.IsNullOrEmpty(s)));
        if (_shell is null)
        {
            _shell = new ShellViewModel(services, ShowSignIn);
            OnPropertyChanged(nameof(Shell));
            CurrentPage = _shell;
            _shell.Restore(initialFolder);
        }
        else
        {
            CurrentPage = _shell;
            await _shell.OnSignedInAgainAsync();
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
        if (_shell is not null)
        {
            await _shell.DisposeAsync();
        }
    }
}
