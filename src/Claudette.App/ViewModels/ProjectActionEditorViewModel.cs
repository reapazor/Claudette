using Claudette.App.Services;
using Claudette.Core.ProjectTools;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// The small dialog for one custom project action (DESIGN.md §18, "Custom actions"): its name, command, working folder,
/// what must exist for it to be shown, and mode. It hands back a new action, or a changed copy of the one it was given,
/// and changes nothing itself.
/// </summary>
public sealed partial class ProjectActionEditorViewModel : ViewModelBase
{
    private readonly IPlatformServices _platform;
    private readonly CustomProjectAction? _existing;
    private readonly Action<CustomProjectAction, ProjectFileScope> _save;
    private readonly Action _close;

    public ProjectActionEditorViewModel(string folder, IPlatformServices platform, CustomProjectAction? existing, Action<CustomProjectAction, ProjectFileScope> save, Action close)
    {
        Folder = folder;
        _platform = platform;
        _existing = existing;
        _save = save;
        _close = close;
        Name = existing?.Name ?? "";
        Command = existing?.Command ?? "";
        WorkingFolder = existing?.WorkingFolder ?? "";
        IfExists = string.Join("; ", existing?.IfExists ?? []);
        LaunchAndForget = existing?.Mode == CustomActionMode.LaunchAndForget;
        RunOnNewWorktree = existing?.RunOnNewWorktree ?? false;
        SaveToShared = existing?.Scope == ProjectFileScope.Shared;
    }

    /// <summary>Asks which file the action goes in; from Tab settings… the file is already chosen.</summary>
    public bool AsksForFile { get; init; }

    /// <summary>In <c>claudette.json</c>, shared with the project, rather than <c>claudette.local.json</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveToLocal), nameof(FileNote))]
    public partial bool SaveToShared { get; set; }

    public bool SaveToLocal
    {
        get => !SaveToShared;
        set => SaveToShared = !value;
    }

    public string SharedText => $"Shared with the project ({ProjectFile.SharedName})";

    public string LocalText => $"Just me ({ProjectFile.LocalName})";

    /// <summary>Saving rewrites the file, which drops its comments.</summary>
    public string FileNote => AsksForFile
        ? $"Saving rewrites {ProjectFile.FileName(SaveToShared ? ProjectFileScope.Shared : ProjectFileScope.Local)} in {Folder}, so comments in it are dropped."
        : "";

    /// <summary>The tab's folder, which the working folder is relative to.</summary>
    public string Folder { get; }

    public string Title => _existing is null ? "Add an action" : "Edit action";

    public string ShellText => OperatingSystem.IsWindows()
        ? "Runs with cmd.exe, as in a Command Prompt."
        : "Runs with your shell ($SHELL -c), as in a terminal.";

    public string CommandPlaceholder => OperatingSystem.IsWindows() ? "for example: dotnet test Game.sln" : "for example: ./Scripts/run-tests.sh";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Command { get; set; }

    /// <summary>Relative to the tab's folder; empty for the tab's folder itself.</summary>
    [ObservableProperty]
    public partial string WorkingFolder { get; set; }

    /// <summary>
    /// The file or folder that must exist for the action to be shown (<c>ifExists</c>), relative to the tab's folder;
    /// empty to show it always. Several are separated by semicolons.
    /// </summary>
    [ObservableProperty]
    public partial string IfExists { get; set; }

    /// <summary>
    /// Picks the file for <see cref="IfExists"/>. One under the tab's folder is written relative to it, with forward
    /// slashes, so a shared file works on every OS.
    /// </summary>
    [RelayCommand]
    private async Task BrowseIfExistsAsync()
    {
        if (await _platform.PickFileAsync("Show the action only when this file exists") is not { } path)
        {
            return;
        }
        var relative = Path.GetRelativePath(Folder, path);
        IfExists = Path.IsPathRooted(relative) ? relative : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>"Launch and forget" rather than the default, "Run with output".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunWithOutput))]
    public partial bool LaunchAndForget { get; set; }

    public bool RunWithOutput
    {
        get => !LaunchAndForget;
        set => LaunchAndForget = !value;
    }

    /// <summary>Runs on its own in each new worktree a worktree tab makes (DESIGN.md §18, "Setting up a new worktree").</summary>
    [ObservableProperty]
    public partial bool RunOnNewWorktree { get; set; }

    public string RunOnNewWorktreeText =>
        $"Run there, once Claude Code has made it, such as to install dependencies. {CustomProjectAction.WorktreeVariable} and {CustomProjectAction.MainCheckoutVariable} "
        + $"name the two folders, to copy a local .env from, say. A shared action runs only in a folder you trust.";

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var action = _existing?.Clone() ?? new CustomProjectAction();
        action.Name = Name.Trim();
        action.Command = Command.Trim();
        action.WorkingFolder = string.IsNullOrWhiteSpace(WorkingFolder) ? null : WorkingFolder.Trim();
        var paths = IfExists.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        action.IfExists = paths.Length > 0 ? paths : null;
        action.Mode = LaunchAndForget ? CustomActionMode.LaunchAndForget : CustomActionMode.RunWithOutput;
        action.RunOnNewWorktree = RunOnNewWorktree;
        _close();
        _save(action, SaveToShared ? ProjectFileScope.Shared : ProjectFileScope.Local);
    }

    private bool CanSave() => Name.Trim().Length > 0 && Command.Trim().Length > 0 && Command.IndexOfAny(['\r', '\n']) < 0;

    [RelayCommand]
    private void Cancel() => _close();
}
