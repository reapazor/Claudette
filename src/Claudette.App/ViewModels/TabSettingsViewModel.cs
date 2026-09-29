using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Claudette.App.Services;
using Claudette.Core.ProjectTools;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>A choice in a dropdown. A null value means "use the default".</summary>
public sealed record Choice(string? Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// An entry of a project file's <c>actions</c> in Tab settings… (DESIGN.md §18, "Custom actions"). It keeps the entry's
/// JSON as written, so saving keeps fields Claudette doesn't edit, such as <c>os</c>.
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
/// "Tab settings…": settings for one tab that replace the defaults (DESIGN.md §14, "Per-tab overrides"), and the
/// folder's custom project actions (DESIGN.md §18).
/// </summary>
public sealed partial class TabSettingsViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly TabViewModel _tab;
    private readonly Action _close;

    private readonly ShellViewModel? _shell;

    public TabSettingsViewModel(AppServices services, TabViewModel tab, Action close, ShellViewModel? shell = null)
    {
        _shell = shell;
        _services = services;
        _tab = tab;
        _close = close;
        var overrides = tab.State.Overrides;
        var defaults = services.Settings;

        // What the tab's Claude Code offers, else what Claude Code last offered on this machine (DESIGN.md §14).
        IReadOnlyList<ModelInfo> models = tab.Models.Count > 0 ? tab.Models : services.State.KnownModels;
        ModelChoices = [new Choice(null, $"Default ({defaults.NewTabs.DefaultModel ?? "Claude Code's default"})"), .. models.Select(m => new Choice(m.Value, m.DisplayName))];
        if (overrides.Model is { } model && ModelChoices.All(c => c.Value != model))
        {
            ModelChoices = [.. ModelChoices, new Choice(model, model)];
        }
        var levels = models.SelectMany(m => m.SupportedEffortLevels).Distinct(StringComparer.Ordinal).ToList();
        if (levels.Count == 0)
        {
            levels = ["low", "medium", "high", "xhigh", "max"];
        }
        if (overrides.Effort is { } effort && !levels.Contains(effort))
        {
            levels.Add(effort);
        }
        EffortChoices = [new Choice(null, $"Default ({defaults.NewTabs.DefaultEffort ?? "model default"})"), .. levels.Select(e => new Choice(e, e))];
        ModeChoices = [new Choice(null, $"Default ({PermissionModeInfo.Label(defaults.NewTabs.DefaultPermissionMode)})"), .. PermissionModeInfo.Choices.Select(m => new Choice(m.Value, m.Label))];
        MonitorChoices = [new Choice(null, $"Default ({(defaults.Processes.ShowMonitor ? "on" : "off")})"), new Choice(On, "On"), new Choice(Off, "Off")];

        SelectedModel = ModelChoices.First(c => c.Value == overrides.Model);
        SelectedEffort = EffortChoices.FirstOrDefault(c => c.Value == overrides.Effort) ?? EffortChoices[0];
        SelectedMode = ModeChoices.FirstOrDefault(c => c.Value == overrides.PermissionMode) ?? ModeChoices[0];
        SelectedMonitor = MonitorChoices.First(c => c.Value == overrides.ShowProcessMonitor switch { true => On, false => Off, null => null });

        var checkIns = overrides.CheckIns ?? defaults.CheckIns;
        UseCustomCheckIns = overrides.CheckIns is not null;
        CheckInsEnabled = checkIns.Enabled;
        RunTimeMinutes = checkIns.RunTimeMinutes;
        QuietTimeMinutes = checkIns.QuietTimeMinutes;
        CheckInMessage = checkIns.Message;
        NotifyOnCheckIn = checkIns.Notify;
        SyncToLibrary = tab.State.SyncToLibrary;
        _sharedRows = LoadRows(ProjectFileScope.Shared, out var sharedError);
        _localRows = LoadRows(ProjectFileScope.Local, out var localError);
        _fileErrors[ProjectFileScope.Shared] = sharedError;
        _fileErrors[ProjectFileScope.Local] = localError;
        ProjectActionFiles =
        [
            new SettingChoice<ProjectFileScope>(ProjectFileScope.Shared, $"Shared with the project ({ProjectFile.SharedName})"),
            new SettingChoice<ProjectFileScope>(ProjectFileScope.Local, $"Just me ({ProjectFile.LocalName})"),
        ];
        // The shared file first when it has actions, since that's where a project's are.
        SelectedProjectActionFile = ProjectActionFiles[_sharedRows.Count > 0 || _localRows.Count == 0 && sharedError is null ? 0 : 1];
    }

    public string Title => $"Settings for \"{_tab.DisplayName}\"";

    public IReadOnlyList<Choice> ModelChoices { get; }

    public IReadOnlyList<Choice> EffortChoices { get; }

    public IReadOnlyList<Choice> ModeChoices { get; }

    private const string On = "on";
    private const string Off = "off";

    /// <summary>The process monitor for this tab (DESIGN.md §4, "Process monitor").</summary>
    public IReadOnlyList<Choice> MonitorChoices { get; }

    [ObservableProperty]
    public partial Choice SelectedMonitor { get; set; }

    [ObservableProperty]
    public partial Choice SelectedModel { get; set; }

    [ObservableProperty]
    public partial Choice SelectedEffort { get; set; }

    [ObservableProperty]
    public partial Choice SelectedMode { get; set; }

    [ObservableProperty]
    public partial bool UseCustomCheckIns { get; set; }

    [ObservableProperty]
    public partial bool CheckInsEnabled { get; set; }

    [ObservableProperty]
    public partial decimal? RunTimeMinutes { get; set; }

    [ObservableProperty]
    public partial decimal? QuietTimeMinutes { get; set; }

    [ObservableProperty]
    public partial string CheckInMessage { get; set; }

    [ObservableProperty]
    public partial bool NotifyOnCheckIn { get; set; }

    /// <summary>
    /// <b>Sync to other machines</b> (DESIGN.md §9, "Session library"). The tab's own state rather than an override, so
    /// <b>Use defaults</b> leaves it as it is.
    /// </summary>
    [ObservableProperty]
    public partial bool SyncToLibrary { get; set; }

    // ---- Project actions (DESIGN.md §18, "Custom actions"): the folder's claudette.json files, saved with Apply ------

    /// <summary>Opens the action dialog; set by the shell.</summary>
    internal Action<string, CustomProjectAction?, Action<CustomProjectAction, ProjectFileScope>, bool>? EditAction { get; init; }

    private readonly ObservableCollection<CustomActionRow> _sharedRows;
    private readonly ObservableCollection<CustomActionRow> _localRows;
    private readonly Dictionary<ProjectFileScope, string?> _fileErrors = [];
    private readonly HashSet<ProjectFileScope> _changedFiles = [];

    private ObservableCollection<CustomActionRow> LoadRows(ProjectFileScope scope, out string? error)
    {
        error = null;
        try
        {
            return [.. ProjectFile.ReadEntries(_tab.Folder, scope, _services.ProjectTools.OS).Select(e => new CustomActionRow(e.Raw, e.Action, e.Problem, e.ForThisOS))];
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return [];
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
    public ObservableCollection<CustomActionRow> ProjectActions => Scope == ProjectFileScope.Shared ? _sharedRows : _localRows;

    /// <summary>The file exists but can't be read, so it can't be edited here.</summary>
    public string? ProjectActionFileError => _fileErrors.GetValueOrDefault(Scope);

    public bool CanEditProjectActionFile => ProjectActionFileError is null;

    public string ProjectActionsNote => Scope == ProjectFileScope.Shared
        ? $"{ProjectFile.SharedName} in {_tab.FolderName} is shared: commit it with the project. Saving rewrites it, so comments in it are dropped."
        : $"{ProjectFile.LocalName} in {_tab.FolderName} is just yours: add it to .gitignore. Saving rewrites it, so comments in it are dropped.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditProjectActionCommand), nameof(RemoveProjectActionCommand), nameof(MoveProjectActionUpCommand), nameof(MoveProjectActionDownCommand))]
    public partial CustomActionRow? SelectedProjectAction { get; set; }

    [RelayCommand(CanExecute = nameof(CanEditProjectActionFile))]
    private void AddProjectAction()
    {
        var rows = ProjectActions;
        var scope = Scope;
        EditAction?.Invoke(_tab.Folder, null, (action, _) =>
        {
            var row = new CustomActionRow(null, action, null, true);
            rows.Add(row);
            SelectedProjectAction = row;
            _changedFiles.Add(scope);
        }, false);
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
        EditAction?.Invoke(_tab.Folder, existing, (action, _) =>
        {
            var index = rows.IndexOf(row);
            if (index >= 0)
            {
                var edited = row with { Action = action };
                rows[index] = edited;
                SelectedProjectAction = edited;
                _changedFiles.Add(scope);
            }
        }, false);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedProjectAction))]
    private void RemoveProjectAction()
    {
        if (SelectedProjectAction is { } row && ProjectActions.Remove(row))
        {
            SelectedProjectAction = null;
            _changedFiles.Add(Scope);
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
        _changedFiles.Add(Scope);
    }

    private bool HasSelectedProjectAction() => SelectedProjectAction is not null;

    private bool CanEditSelectedProjectAction() => SelectedProjectAction?.CanEdit == true;

    /// <summary>Opens the chosen file in the app the OS uses for JSON, for what the dialog doesn't cover.</summary>
    [RelayCommand(CanExecute = nameof(CanEditProjectActionFile))]
    private Task OpenProjectActionFileAsync()
    {
        var path = ProjectFile.PathFor(_tab.Folder, Scope);
        return File.Exists(path) ? _services.Platform.OpenFileAsync(path) : Task.CompletedTask;
    }

    /// <summary>Writes each file whose actions changed.</summary>
    private void SaveProjectActions()
    {
        foreach (var scope in _changedFiles)
        {
            var rows = scope == ProjectFileScope.Shared ? _sharedRows : _localRows;
            try
            {
                ProjectFile.WriteActions(_tab.Folder, scope, rows.Select(r => r.ToJson()));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                _tab.AddProjectNote($"Couldn't save {ProjectFile.FileName(scope)}: {ex.Message}");
            }
        }
        if (_changedFiles.Count > 0)
        {
            _shell?.OnProjectActionsChanged(_tab.Folder);
        }
        _changedFiles.Clear();
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        SaveProjectActions();
        var previous = _tab.State.Overrides;
        _tab.State.Overrides = new TabOverrides
        {
            Model = SelectedModel.Value,
            Effort = SelectedEffort.Value,
            PermissionMode = SelectedMode.Value,
            ShowProcessMonitor = SelectedMonitor.Value switch { On => true, Off => false, _ => null },
            CheckIns = UseCustomCheckIns
                ? new CheckInSettings
                {
                    Enabled = CheckInsEnabled,
                    RunTimeMinutes = (int)(RunTimeMinutes ?? 0),
                    QuietTimeMinutes = (int)(QuietTimeMinutes ?? 0),
                    Message = string.IsNullOrWhiteSpace(CheckInMessage) ? CheckInSettings.DefaultMessage : CheckInMessage,
                    Notify = NotifyOnCheckIn,
                }
                : null,
        };
        _close();
        _tab.SetSyncToLibrary(SyncToLibrary);
        await _tab.ApplyOverridesAsync(previous);
    }

    [RelayCommand]
    private void UseDefaults()
    {
        SelectedModel = ModelChoices[0];
        SelectedEffort = EffortChoices[0];
        SelectedMode = ModeChoices[0];
        SelectedMonitor = MonitorChoices[0];
        UseCustomCheckIns = false;
    }

    [RelayCommand]
    private void Cancel() => _close();
}
