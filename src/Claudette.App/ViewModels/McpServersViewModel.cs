using System.Collections.ObjectModel;
using Claudette.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>One MCP server on the side panel's MCP page. Its name and error are the server's own: untrusted text.</summary>
public sealed partial class McpServerRow(McpServerStatus status) : ObservableObject
{
    public McpServerStatus Status { get; } = status;

    public string Name => Status.Name;

    public string StateText => Status.State switch
    {
        McpServerState.Connected => Status.ToolCount == 1 ? "Connected · 1 tool" : $"Connected · {Status.ToolCount} tools",
        McpServerState.Pending => "Connecting…",
        McpServerState.NeedsAuth => "Needs signing in",
        McpServerState.Failed => "Failed",
        McpServerState.Disabled => "Off",
        _ => "Unknown",
    };

    public bool IsConnected => Status.State == McpServerState.Connected;

    public bool IsFailed => Status.State is McpServerState.Failed or McpServerState.NeedsAuth;

    public bool IsDisabled => Status.State == McpServerState.Disabled;

    /// <summary>Reconnect helps a server that failed or needs signing in; a connected one doesn't need it.</summary>
    public bool CanReconnect => Status.State is McpServerState.Failed or McpServerState.NeedsAuth or McpServerState.Pending;

    public string? Error => Status.Error;

    public bool HasError => !string.IsNullOrWhiteSpace(Status.Error);

    /// <summary>"project · 1.2.0": where it's configured and its version, when Claude Code says.</summary>
    public string? Detail => string.Join(" · ", new[] { Status.Scope, Status.Version is { } v ? $"v{v}" : null }.OfType<string>()) is { Length: > 0 } d ? d : null;

    public bool HasDetail => Detail is not null;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>It needs signing in: <b>Sign in</b> starts that.</summary>
    public bool CanSignIn => Status.State == McpServerState.NeedsAuth;

    /// <summary>A connected remote server can be signed out of, so the next connect asks again.</summary>
    public bool CanSignOut => IsConnected && Status.IsRemote;

    /// <summary>The address it's being signed in at, while the user finishes in the browser; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSigningIn))]
    public partial string? SignInUrl { get; set; }

    public bool IsSigningIn => SignInUrl is not null;

    /// <summary>What to do in the browser, while signing in.</summary>
    [ObservableProperty]
    public partial string? SignInText { get; set; }

    /// <summary>Claude Code waits for the browser to come back to it, or else takes the address it ended on.</summary>
    [ObservableProperty]
    public partial bool CanPasteAddress { get; set; }

    /// <summary>The address the browser ended on, pasted when it couldn't come back to Claude Code by itself.</summary>
    [ObservableProperty]
    public partial string PastedAddress { get; set; } = "";
}

/// <summary>
/// The side panel's MCP page (DESIGN.md §4, "MCP servers"): the session's MCP servers and how each is connected, with
/// <b>Reconnect</b> and a switch to turn one off for the session. Read from Claude Code when the page opens and after a
/// change; a tab without a session shows nothing.
/// </summary>
/// <param name="openUrl">Opens a sign-in page in the browser.</param>
public sealed partial class McpServersViewModel(Func<ClaudeSession?> session, Func<string, Task>? openUrl = null) : ViewModelBase
{
    public ObservableCollection<McpServerRow> Servers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    public bool IsEmpty => !IsLoading && Servers.Count == 0 && Error is null;

    /// <summary>"1 failed" for the panel's button when something needs attention; null otherwise.</summary>
    public string? Attention => Servers.Count(s => s.IsFailed) is var failed and > 0 ? $"{failed} need{(failed == 1 ? "s" : "")} attention" : null;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (session() is not { } current)
        {
            Servers.Clear();
            Changed();
            return;
        }
        IsLoading = true;
        Error = null;
        try
        {
            var servers = await current.GetMcpStatusAsync();
            Servers.Clear();
            foreach (var server in servers.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                Servers.Add(new McpServerRow(server));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = $"Couldn't read the MCP servers: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            Changed();
        }
    }

    [RelayCommand]
    private Task ReconnectAsync(McpServerRow? row) =>
        row is null ? Task.CompletedTask : ChangeAsync(row, s => s.ReconnectMcpServerAsync(row.Name), "reconnect");

    /// <summary>
    /// <b>Sign in</b> (DESIGN.md §4, "MCP servers"): Claude Code gives the address to sign in at, which opens in the
    /// browser; it connects the server once the browser comes back to it.
    /// </summary>
    [RelayCommand]
    private async Task SignInAsync(McpServerRow? row)
    {
        if (row is null || session() is not { } current)
        {
            return;
        }
        row.IsBusy = true;
        McpSignIn signIn;
        try
        {
            signIn = await current.SignInToMcpServerAsync(row.Name);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = $"Couldn't sign in to {row.Name}: {ex.Message.TrimEnd('.')}. You can sign in with /mcp in Claude Code in a terminal.";
            row.IsBusy = false;
            return;
        }
        row.IsBusy = false;
        if (signIn.AuthUrl is not { } url)
        {
            // Signed in already: it only needed connecting.
            await RefreshAsync();
            return;
        }
        row.SignInUrl = url;
        row.CanPasteAddress = signIn.CallbackExpected;
        row.SignInText = signIn.CallbackExpected
            ? "Finish signing in in your browser; the server connects when you're done. Refresh shows it."
            : "Finish signing in in your browser, then Reconnect.";
        await OpenSignInPageAsync(row);
    }

    [RelayCommand]
    private Task OpenSignInPageAsync(McpServerRow? row) =>
        row?.SignInUrl is { } url && openUrl is not null ? openUrl(url) : Task.CompletedTask;

    /// <summary>
    /// The address the browser ended on, for a sign-in whose page couldn't come back to Claude Code by itself (on another
    /// machine, say).
    /// </summary>
    [RelayCommand]
    private Task FinishSignInAsync(McpServerRow? row) =>
        row is { PastedAddress: var address } && address.Trim().Length > 0
            ? ChangeAsync(row, s => s.FinishMcpSignInAsync(row.Name, address.Trim()), "finish signing in to")
            : Task.CompletedTask;

    /// <summary><b>Sign out</b>: Claude Code forgets the server's sign-in, so the next connect asks again.</summary>
    [RelayCommand]
    private Task SignOutAsync(McpServerRow? row) =>
        row is null ? Task.CompletedTask : ChangeAsync(row, s => s.SignOutOfMcpServerAsync(row.Name), "sign out of");

    /// <summary>Turns a server off for this session, or back on.</summary>
    [RelayCommand]
    private Task ToggleAsync(McpServerRow? row) =>
        row is null ? Task.CompletedTask : ChangeAsync(row, s => s.SetMcpServerEnabledAsync(row.Name, row.IsDisabled), row.IsDisabled ? "turn on" : "turn off");

    private async Task ChangeAsync(McpServerRow row, Func<ClaudeSession, Task> change, string what)
    {
        if (session() is not { } current)
        {
            return;
        }
        row.IsBusy = true;
        try
        {
            await change(current);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = $"Couldn't {what} {row.Name}: {ex.Message}";
            row.IsBusy = false;
            return;
        }
        await RefreshAsync();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Attention));
    }
}
