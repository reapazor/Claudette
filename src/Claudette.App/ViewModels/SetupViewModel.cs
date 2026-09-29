using System.Text;
using Claudette.App.Services;
using Claudette.Core.Installation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>Shown when Claude Code is missing, too old or unusable (DESIGN.md §2, "Dependency").</summary>
/// <param name="update">
/// <b>Update now</b> for a version below the minimum (DESIGN.md §12): runs the install method's update command,
/// passing each line of output. Null when there's nothing to update.
/// </param>
/// <param name="recheck">Checks the same <c>claude</c> again after a successful update.</param>
public sealed partial class SetupViewModel(
    ClaudeLocateResult result,
    IPlatformServices platform,
    Func<string?, Task> checkAgain,
    Func<Action<string>, Task<ClaudeUpdateResult>>? update = null,
    Func<Task>? recheck = null) : ViewModelBase
{
    public const string InstallDocsUrl = "https://code.claude.com/docs/en/setup";

    private readonly StringBuilder _output = new();

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

    public bool CanUpdate => update is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateNowCommand))]
    public partial bool IsUpdating { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput))]
    public partial string Output { get; private set; } = "";

    public bool HasOutput => Output.Length > 0;

    [ObservableProperty]
    public partial string? UpdateResult { get; private set; }

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

    [RelayCommand(CanExecute = nameof(CanRunUpdate))]
    private async Task UpdateNowAsync()
    {
        if (update is null)
        {
            return;
        }
        IsUpdating = true;
        _output.Clear();
        Output = "";
        UpdateResult = null;
        var outcome = await update(line =>
        {
            _output.AppendLine(line);
            Output = _output.ToString();
        });
        IsUpdating = false;
        UpdateResult = outcome.Message;
        if (outcome.Succeeded)
        {
            await (recheck?.Invoke() ?? checkAgain(null));
        }
    }

    private bool CanRunUpdate() => !IsUpdating;
}
