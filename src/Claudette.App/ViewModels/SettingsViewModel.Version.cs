using System.Runtime.InteropServices;
using Claudette.Core.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// The foot of the Settings sidebar (DESIGN.md §14, "Version"): which Claudette this is, copied with a click, and
/// <b>Report an issue</b>, which opens a new GitHub issue with the versions filled in.
/// </summary>
public sealed partial class SettingsViewModel
{
    /// <summary>How long the version says "Copied" after a click.</summary>
    public static readonly TimeSpan CopiedFor = TimeSpan.FromSeconds(2);

    private ITimer? _copiedTimer;

    /// <summary>The version details were just copied.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionLabel))]
    public partial bool VersionCopied { get; private set; }

    /// <summary>"Claudette 0.1.0", or "Claudette 0.1.0 · 842169b" for a source build.</summary>
    public string VersionLabel => VersionCopied ? "Copied" : _services.Build.Label;

    public string VersionTip => _services.Build.Kind == AppInstallKind.SourceBuild
        ? $"Claudette {_services.Build.Description}, built from this checkout. Click to copy the versions for a bug report."
        : $"Claudette {_services.Build.Description}. Click to copy the versions for a bug report.";

    /// <summary>Claudette's version, Claude Code's, the OS and the runtime: only versions, since it's meant to be posted.</summary>
    internal IReadOnlyList<string> VersionDetails() => _services.Build.Details(
        _services.InstalledClaudeVersion?.ToString(),
        RuntimeInformation.OSDescription,
        RuntimeInformation.RuntimeIdentifier,
        RuntimeInformation.FrameworkDescription);

    [RelayCommand]
    private async Task CopyVersionAsync()
    {
        await _services.Platform.SetClipboardTextAsync(string.Join(Environment.NewLine, VersionDetails()));
        _copiedTimer?.Dispose();
        VersionCopied = true;
        _copiedTimer = _services.Time.CreateTimer(_ => _services.Dispatcher.Post(() => VersionCopied = false), null, CopiedFor, Timeout.InfiniteTimeSpan);
    }

    /// <summary>A new issue on Claudette's GitHub with the versions filled in. Nothing is sent until the user submits it.</summary>
    [RelayCommand]
    private Task ReportIssueAsync() =>
        _services.Platform.OpenUrlAsync(AppBuild.NewIssueUrl(ReleaseFeed.DefaultRepository, VersionDetails()));
}
