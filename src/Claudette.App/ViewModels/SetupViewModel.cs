using Claudette.App.Services;
using Claudette.Core.Installation;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>Shown when Claude Code is missing, too old or unusable (DESIGN.md §2, "Dependency").</summary>
public sealed partial class SetupViewModel(ClaudeLocateResult result, IPlatformServices platform, Func<string?, Task> checkAgain) : ViewModelBase
{
    public const string InstallDocsUrl = "https://code.claude.com/docs/en/setup";

    public string Title { get; } = result.Problem switch
    {
        ClaudeInstallProblem.TooOld => "Claude Code needs an update",
        ClaudeInstallProblem.UnsupportedLauncher => "This Claude Code install isn't supported yet",
        ClaudeInstallProblem.VersionUnreadable => "Claude Code didn't respond",
        _ => "Claude Code isn't installed",
    };

    public string Detail { get; } = result.Detail ?? "";

    public string? FoundPath { get; } = result.FoundPath;

    public bool HasFoundPath => FoundPath is not null;

    [RelayCommand]
    private Task CheckAgainAsync() => checkAgain(null);

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await platform.PickFileAsync("Choose the claude executable") is { } path)
        {
            await checkAgain(path);
        }
    }

    [RelayCommand]
    private Task OpenInstallDocsAsync() => platform.OpenUrlAsync(InstallDocsUrl);
}
