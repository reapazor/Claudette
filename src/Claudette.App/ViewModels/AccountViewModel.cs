using Claudette.App.Services;
using Claudette.Core.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// The Claude Code account (DESIGN.md §11): the header's account menu and Settings → Claude Code, from
/// <c>claude auth status</c>; the banner across all tabs when a tab finds Claude Code signed out, and the sign-in dialog
/// it opens; and <b>Sign out</b>.
/// </summary>
public sealed partial class AccountViewModel(AppServices services) : ViewModelBase
{
    /// <summary>Runs <c>claude auth status</c> and acts on it once a sign-in finishes. Set by the main window.</summary>
    public Func<Task>? CheckSignIn { get; set; }

    /// <summary>
    /// Raised once <c>claude auth logout</c> has signed Claude Code out and <c>claude auth status</c> agrees. The tabs
    /// then wait for the next sign-in.
    /// </summary>
    public event Action? SignedOut;

    /// <summary>The latest <c>claude auth status</c>, or null before the first one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedIn), nameof(IsSignedOut), nameof(Email), nameof(PlanText), nameof(Organization), nameof(Summary), nameof(SettingsText), nameof(CanBeginSignIn))]
    [NotifyPropertyChangedFor(nameof(BillingUrl), nameof(HasBilling), nameof(BillingText), nameof(BillingTip), nameof(HeaderName), nameof(HeaderPlan))]
    [NotifyPropertyChangedFor(nameof(HeaderPlanUrl), nameof(HeaderPlanTip), nameof(HeaderPlanName))]
    [NotifyCanExecuteChangedFor(nameof(SignOutCommand))]
    public partial AuthStatus? Status { get; set; }

    /// <summary>Whether this account can use Remote Control follows what <c>claude auth status</c> says (DESIGN.md §18).</summary>
    partial void OnStatusChanged(AuthStatus? value) => services.RemoteControl.UseAccount(value);

    public bool IsSignedIn => Status is { LoggedIn: true };

    public bool IsSignedOut => !IsSignedIn;

    public string? Email => IsSignedIn ? Status?.Email : null;

    public string? Organization => IsSignedIn ? Status?.OrganizationName : null;

    /// <summary>The plan, such as "Max plan", or how Claude Code is signed in when there's no plan (an API key, say).</summary>
    public string? PlanText => IsSignedIn ? PlanName(Status?.SubscriptionType) ?? MethodName(Status?.AuthMethod) : null;

    /// <summary>The header's account button: the email and plan.</summary>
    public string Summary => IsSignedIn
        ? string.Join(" · ", new[] { Email, PlanText }.Where(s => !string.IsNullOrEmpty(s))) is { Length: > 0 } text ? text : "Signed in"
        : "Not signed in";

    // ---- Plan and billing (DESIGN.md §11, "Detecting") ------------------------------------------------------------

    public const string ClaudeBillingUrl = "https://claude.ai/settings/billing";

    public const string ConsoleBillingUrl = "https://platform.claude.com/settings/billing";

    /// <summary>The plan's usage on claude.ai, where the header's plan goes for a Claude account.</summary>
    public const string ClaudeUsageUrl = "https://claude.ai/new#settings/usage";

    /// <summary>
    /// Where this account's plan and billing are managed: claude.ai for a Claude plan, the Claude Console for an API
    /// key or Console account. Null for a cloud provider (Bedrock, Vertex, Foundry), which bills through that provider.
    /// </summary>
    public string? BillingUrl => !IsSignedIn || Status!.AuthMethod == "third_party" || Status.ApiProvider is { Length: > 0 } provider && provider != "firstParty"
        ? null
        : !string.IsNullOrEmpty(Status.SubscriptionType) || Status.AuthMethod is "claude.ai" or "oauth_token"
            ? ClaudeBillingUrl
            : ConsoleBillingUrl;

    public bool HasBilling => BillingUrl is not null;

    /// <summary>The menu's link.</summary>
    public string BillingText => BillingUrl == ConsoleBillingUrl ? "Console billing" : "Plan and billing";

    public string BillingTip => BillingUrl == ConsoleBillingUrl
        ? "Billing in the Claude Console, in your browser"
        : "Your plan and billing on claude.ai, in your browser";

    /// <summary>The header's account button: the email, which opens the menu. The plan beside it opens its usage.</summary>
    public string HeaderName => IsSignedIn && HasBilling && PlanText is not null ? Email ?? "Signed in" : Summary;

    /// <summary>The plan in the header, as a link; null when there's no plan or nowhere to manage it.</summary>
    public string? HeaderPlan => IsSignedIn && HasBilling ? PlanText : null;

    /// <summary>
    /// Where the header's plan goes: the plan's usage on claude.ai for a Claude account, the Console's billing
    /// otherwise. The account menu's link still opens billing.
    /// </summary>
    public string? HeaderPlanUrl => BillingUrl == ClaudeBillingUrl ? ClaudeUsageUrl : BillingUrl;

    public string HeaderPlanTip => HeaderPlanUrl == ClaudeUsageUrl ? "Your plan's usage on claude.ai, in your browser" : BillingTip;

    public string HeaderPlanName => HeaderPlanUrl == ClaudeUsageUrl ? "Plan usage" : BillingText;

    [RelayCommand]
    private Task OpenBillingAsync() => BillingUrl is { } url ? services.Platform.OpenUrlAsync(url) : Task.CompletedTask;

    [RelayCommand]
    private Task OpenHeaderPlanAsync() => HeaderPlanUrl is { } url ? services.Platform.OpenUrlAsync(url) : Task.CompletedTask;

    /// <summary>Settings → Claude Code's account line.</summary>
    public string SettingsText => IsSignedIn
        ? $"Signed in{(Email is { } email ? $" as {email}" : "")}{(PlanText is { } plan ? $" ({plan})" : "")}{(Organization is { } org ? $", {org}" : "")}."
        : "Not signed in.";

    // ---- Signing in mid-session (DESIGN.md §11, "Signing in") -------------------------------------------------------

    /// <summary>The banner across all tabs: <i>"Claude Code needs you to sign in."</i></summary>
    [ObservableProperty]
    public partial bool NeedsSignIn { get; set; }

    /// <summary>The sign-in dialog, while it's open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSignIn), nameof(CanBeginSignIn))]
    public partial SignInViewModel? SignIn { get; set; }

    public bool HasSignIn => SignIn is not null;

    /// <summary>The account menu's and Settings' <b>Sign in</b>: signed out, and not already signing in.</summary>
    public bool CanBeginSignIn => IsSignedOut && !HasSignIn;

    /// <summary>
    /// A tab found Claude Code signed out. Shows the banner, and sends an OS notification unless Claudette is in front
    /// (the notification service skips it then). Once per sign-in: more tabs failing don't notify again.
    /// </summary>
    public void RequireSignIn(bool notify = true)
    {
        if (NeedsSignIn)
        {
            return;
        }
        NeedsSignIn = true;
        if (notify)
        {
            services.Notifications.Notify(NotificationKind.SignIn, "Claude Code needs you to sign in", "Your tabs are waiting until you sign in again.");
        }
    }

    /// <summary>The banner's and the account menu's <b>Sign in</b>: opens the dialog and starts signing in.</summary>
    [RelayCommand]
    private void BeginSignIn()
    {
        ShowSignIn();
        if (SignIn is { IsIdle: true } signIn)
        {
            signIn.SignInCommand.Execute(null);
        }
    }

    /// <summary>Opens the sign-in dialog without starting, for a clicked notification.</summary>
    public void ShowSignIn() => SignIn ??= new SignInViewModel(services, () => CheckSignIn?.Invoke() ?? Task.CompletedTask, () => SignIn = null);

    /// <summary>Runs <c>claude auth status</c> again, so the header shows what Claude Code now reports.</summary>
    public async Task RefreshAsync()
    {
        if (services.Auth is not { } auth)
        {
            return;
        }
        try
        {
            Status = await auth.GetStatusAsync();
        }
        catch (Exception)
        {
            // The header keeps what it had; the banner says what matters.
        }
    }

    /// <summary><c>claude auth status</c> reports a sign-in: the banner and dialog go away.</summary>
    public void OnSignedIn(AuthStatus status)
    {
        Status = status;
        NeedsSignIn = false;
        SignIn = null;
        SignOutError = null;
        services.Notifications.Clear(NotificationKind.SignIn);
    }

    // ---- Signing out (DESIGN.md §11, "Account menu") ----------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSignOutConfirmation))]
    public partial ConfirmationViewModel? SignOutConfirmation { get; set; }

    public bool HasSignOutConfirmation => SignOutConfirmation is not null;

    [ObservableProperty]
    public partial bool IsSigningOut { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSignOutError))]
    public partial string? SignOutError { get; set; }

    public bool HasSignOutError => SignOutError is not null;

    /// <summary><b>Sign out</b> asks first, because every tab stops working until the next sign-in.</summary>
    [RelayCommand(CanExecute = nameof(IsSignedIn))]
    private void SignOut() => SignOutConfirmation = new ConfirmationViewModel(
        "Sign out of Claude Code?",
        "Every tab stops working until you sign in again. This signs Claude Code out on this computer, in the terminal too.",
        "Sign out",
        SignOutNowAsync,
        () => SignOutConfirmation = null);

    /// <summary>
    /// Runs <c>claude auth logout</c>, then <c>claude auth status</c>. Unless Claude Code still reports a sign-in (an API
    /// key in the environment, say, which logging out doesn't remove), the banner shows and the tabs wait for the next
    /// sign-in. No notification: the user just did this.
    /// </summary>
    private async Task SignOutNowAsync()
    {
        if (services.Auth is not { } auth)
        {
            return;
        }
        IsSigningOut = true;
        SignOutError = null;
        AuthStatus status;
        try
        {
            await auth.SignOutAsync();
            try
            {
                status = await auth.GetStatusAsync();
            }
            catch (Exception)
            {
                status = new AuthStatus(false, null, null, null, null, null, null, null);
            }
        }
        catch (Exception ex)
        {
            SignOutError = $"Couldn't sign out: {ex.Message}";
            return;
        }
        finally
        {
            IsSigningOut = false;
        }
        Status = status;
        if (status.LoggedIn)
        {
            SignOutError = $"Claude Code is still signed in{(MethodName(status.AuthMethod) is { } method ? $" ({method})" : "")}. Signing out doesn't remove an API key set in the environment or in Claude Code's settings.";
            return;
        }
        SignedOut?.Invoke();
        RequireSignIn(notify: false);
    }

    internal static string? PlanName(string? subscriptionType) => subscriptionType switch
    {
        null or "" => null,
        "max" => "Max plan",
        "pro" => "Pro plan",
        "team" => "Team plan",
        "enterprise" => "Enterprise plan",
        _ => subscriptionType,
    };

    /// <summary><c>authMethod</c> as people say it. Unknown values are shown as they are.</summary>
    private static string? MethodName(string? authMethod) => authMethod switch
    {
        null or "" or "none" => null,
        "claude.ai" => "Claude account",
        "api_key" => "API key",
        "api_key_helper" => "API key helper",
        "oauth_token" => "OAuth token",
        "third_party" => "Cloud provider",
        _ => authMethod,
    };
}
