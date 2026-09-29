using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Claudette.Core.ProjectTools;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// An entry of a project file's <c>actions</c> on Settings' Actions page (DESIGN.md §18, "Custom actions"). It keeps the
/// entry's JSON as written, so saving keeps fields Claudette doesn't edit, such as <c>os</c>.
/// </summary>
public sealed record CustomActionRow(JsonNode? Raw, CustomProjectAction? Action, string? Problem, bool ForThisOS)
{
    public string Name => Action?.Name ?? "(can't be read)";

    public bool CanEdit => Action is not null;

    public string Detail => Action is not { } action
        ? Problem ?? ""
        : (action.Mode == CustomActionMode.LaunchAndForget ? "Launch and forget: " : "")
            + action.Command + (string.IsNullOrWhiteSpace(action.WorkingFolder) ? "" : $"  (in {action.WorkingFolder})")
            + (ForThisOS ? "" : $"  · only on {string.Join(", ", action.Os ?? [])}");

    /// <summary>The entry to write back: its JSON with the edited fields over it.</summary>
    public JsonNode? ToJson() => Action is { } action ? ProjectFile.ToJson(action, Raw) : Raw?.DeepClone();
}

/// <summary>
/// Settings → the tab's project → <b>Actions</b> (DESIGN.md §14, §18): the actions editor that was in Tab settings…, a
/// choice of the two files and the chosen file's actions. Entries for other OSes are listed, marked "only on …", and
/// entries that can't be read are listed with their reason and can only be removed. Each change saves the file at once.
/// </summary>
public sealed partial class ProjectSettingsViewModel
{
    private readonly ObservableCollection<CustomActionRow> _sharedRows = [];
    private readonly ObservableCollection<CustomActionRow> _localRows = [];
    private readonly Dictionary<ProjectFileScope, string?> _actionFileErrors = [];

    private ObservableCollection<CustomActionRow> RowsFor(ProjectFileScope scope) => scope == ProjectFileScope.Shared ? _sharedRows : _localRows;

    private void LoadActionRows(ProjectFileScope scope)
    {
        var rows = RowsFor(scope);
        rows.Clear();
        try
        {
            foreach (var entry in ProjectFile.ReadEntries(Folder, scope, _services.ProjectTools.OS))
            {
                rows.Add(new CustomActionRow(entry.Raw, entry.Action, entry.Problem, entry.ForThisOS));
            }
            _actionFileErrors[scope] = null;
        }
        catch (InvalidOperationException ex)
        {
            _actionFileErrors[scope] = ex.Message;
        }
    }

    /// <summary>The two files: shared with the project, or just the user's.</summary>
    public IReadOnlyList<SettingChoice<ProjectFileScope>> ProjectActionFiles { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProjectActions), nameof(ProjectActionFileError), nameof(CanEditProjectActionFile), nameof(ProjectActionsNote))]
    [NotifyCanExecuteChangedFor(nameof(AddProjectActionCommand), nameof(OpenProjectActionFileCommand))]
    public partial SettingChoice<ProjectFileScope> SelectedProjectActionFile { get; set; }

    partial void OnSelectedProjectActionFileChanged(SettingChoice<ProjectFileScope> value) => SelectedProjectAction = null;

    private ProjectFileScope Scope => SelectedProjectActionFile.Value;

    /// <summary>The chosen file's actions, in the order the menus list them.</summary>
    public ObservableCollection<CustomActionRow> ProjectActions => RowsFor(Scope);

    /// <summary>The file exists but can't be read, so it can't be edited here.</summary>
    public string? ProjectActionFileError => _actionFileErrors.GetValueOrDefault(Scope);

    public bool CanEditProjectActionFile => ProjectActionFileError is null;

    public string ProjectActionsNote => Scope == ProjectFileScope.Shared
        ? $"{ProjectFile.SharedName} in {_tab.FolderName} is shared: commit it with the project. Saving rewrites it, so comments in it are dropped."
        : $"{ProjectFile.LocalName} in {_tab.FolderName} is just yours: add it to .gitignore. Saving rewrites it, so comments in it are dropped.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditProjectActionCommand), nameof(RemoveProjectActionCommand), nameof(MoveProjectActionUpCommand), nameof(MoveProjectActionDownCommand))]
    public partial CustomActionRow? SelectedProjectAction { get; set; }

    /// <summary>Why the last change couldn't be saved; the list is back to what the file holds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionsError))]
    public partial string? ActionsError { get; private set; }

    public bool HasActionsError => ActionsError is not null;

    /// <summary>
    /// <b>Add an action…</b> from the chip's or the tab's menu: the dialog asks which file the action goes in, as it did
    /// over the main window, and the page shows that file once it's saved.
    /// </summary>
    internal void StartNewAction() => Editor = new ProjectActionEditorViewModel(Folder, null, (action, scope) =>
    {
        SelectedProjectActionFile = ProjectActionFiles.Single(f => f.Value == scope);
        AddActionRow(action, scope);
    }, CloseEditor) { AsksForFile = true };

    /// <summary><b>Add…</b>: an action for the chosen file.</summary>
    [RelayCommand(CanExecute = nameof(CanEditProjectActionFile))]
    private void AddProjectAction()
    {
        var scope = Scope;
        Editor = new ProjectActionEditorViewModel(Folder, null, (action, _) => AddActionRow(action, scope), CloseEditor);
    }

    private void AddActionRow(CustomProjectAction action, ProjectFileScope scope)
    {
        var row = new CustomActionRow(null, action, null, true);
        RowsFor(scope).Add(row);
        if (SaveActions(scope) && Scope == scope)
        {
            SelectedProjectAction = row;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditSelectedProjectAction))]
    private void EditProjectAction()
    {
        if (SelectedProjectAction is not { Action: { } existing } row)
        {
            return;
        }
        var rows = ProjectActions;
        var scope = Scope;
        Editor = new ProjectActionEditorViewModel(Folder, existing, (action, _) =>
        {
            var index = rows.IndexOf(row);
            if (index >= 0)
            {
                var edited = row with { Action = action };
                rows[index] = edited;
                if (SaveActions(scope))
                {
                    SelectedProjectAction = edited;
                }
            }
        }, CloseEditor);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedProjectAction))]
    private void RemoveProjectAction()
    {
        if (SelectedProjectAction is { } row && ProjectActions.Remove(row))
        {
            SelectedProjectAction = null;
            SaveActions(Scope);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedProjectAction))]
    private void MoveProjectActionUp() => MoveProjectAction(-1);

    [RelayCommand(CanExecute = nameof(HasSelectedProjectAction))]
    private void MoveProjectActionDown() => MoveProjectAction(1);

    private void MoveProjectAction(int by)
    {
        if (SelectedProjectAction is not { } row)
        {
            return;
        }
        var rows = ProjectActions;
        var index = rows.IndexOf(row);
        var target = index + by;
        if (index < 0 || target < 0 || target >= rows.Count)
        {
            return;
        }
        rows.Move(index, target);
        SelectedProjectAction = row;
        SaveActions(Scope);
    }

    private bool HasSelectedProjectAction() => SelectedProjectAction is not null;

    private bool CanEditSelectedProjectAction() => SelectedProjectAction?.CanEdit == true;

    /// <summary>Opens the chosen file in the app the OS uses for JSON, for what the page doesn't cover.</summary>
    [RelayCommand(CanExecute = nameof(CanEditProjectActionFile))]
    private Task OpenProjectActionFileAsync()
    {
        var path = ProjectFile.PathFor(Folder, Scope);
        return File.Exists(path) ? _services.Platform.OpenFileAsync(path) : Task.CompletedTask;
    }

    /// <summary>
    /// Writes one file's actions as the list has them. When that fails, the list goes back to what the file holds and
    /// the page says why.
    /// </summary>
    private bool SaveActions(ProjectFileScope scope)
    {
        try
        {
            ProjectFile.WriteActions(Folder, scope, RowsFor(scope).Select(r => r.ToJson()));
        }
        catch (Exception ex) when (IsSaveFailure(ex))
        {
            LoadActionRows(scope);
            SelectedProjectAction = null;
            ActionsError = SaveFailure(scope, ex);
            ActionFileChanged();
            return false;
        }
        ActionsError = null;
        OnFileSaved();
        return true;
    }

    private void ActionFileChanged()
    {
        OnPropertyChanged(nameof(ProjectActionFileError));
        OnPropertyChanged(nameof(CanEditProjectActionFile));
        AddProjectActionCommand.NotifyCanExecuteChanged();
        OpenProjectActionFileCommand.NotifyCanExecuteChanged();
    }
}
