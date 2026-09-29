using Claudette.Core.ProjectTools;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// The small dialog for one custom project action (DESIGN.md §18, "Custom actions"): its name, command, working folder
/// and mode. It hands back a new action, or a changed copy of the one it was given, and changes nothing itself.
/// </summary>
public sealed partial class ProjectActionEditorViewModel : ViewModelBase
{
    private readonly CustomProjectAction? _existing;
    private readonly Action<CustomProjectAction, ProjectFileScope> _save;
    private readonly Action _close;

    public ProjectActionEditorViewModel(string folder, CustomProjectAction? existing, Action<CustomProjectAction, ProjectFileScope> save, Action close)
    {
        Folder = folder;
        _existing = existing;
        _save = save;
        _close = close;
        Name = existing?.Name ?? "";
        Command = existing?.Command ?? "";
        WorkingFolder = existing?.WorkingFolder ?? "";
        LaunchAndForget = existing?.Mode == CustomActionMode.LaunchAndForget;
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

    /// <summary>"Launch and forget" rather than the default, "Run with output".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunWithOutput))]
    public partial bool LaunchAndForget { get; set; }

    public bool RunWithOutput
    {
        get => !LaunchAndForget;
        set => LaunchAndForget = !value;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var action = _existing?.Clone() ?? new CustomProjectAction();
        action.Name = Name.Trim();
        action.Command = Command.Trim();
        action.WorkingFolder = string.IsNullOrWhiteSpace(WorkingFolder) ? null : WorkingFolder.Trim();
        action.Mode = LaunchAndForget ? CustomActionMode.LaunchAndForget : CustomActionMode.RunWithOutput;
        _close();
        _save(action, SaveToShared ? ProjectFileScope.Shared : ProjectFileScope.Local);
    }

    private bool CanSave() => Name.Trim().Length > 0 && Command.Trim().Length > 0 && Command.IndexOfAny(['\r', '\n']) < 0;

    [RelayCommand]
    private void Cancel() => _close();
}
