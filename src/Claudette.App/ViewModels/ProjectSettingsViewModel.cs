using System.ComponentModel;
using Claudette.App.Services;
using Claudette.Core.ProjectTools;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Claudette.App.ViewModels;

/// <summary>
/// The selected tab's project in Settings (DESIGN.md §14, "The project's pages"): its <b>Links</b> and <b>Actions</b>,
/// from the folder's <c>claudette.json</c> and <c>claudette.local.json</c>, and its <b>Tools</b>, this machine's choices
/// for the project in <c>state.json</c>. Like the rest of Settings, each change is saved as it's made: a file is
/// rewritten after each add, edit, removal or move, and every tab in the folder reads it again, so the project's menu
/// follows. Dispose it with the window.
/// </summary>
public sealed partial class ProjectSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly AppServices _services;
    private readonly TabViewModel _tab;
    private readonly ShellViewModel _shell;

    public ProjectSettingsViewModel(AppServices services, TabViewModel tab, ShellViewModel shell)
    {
        _services = services;
        _tab = tab;
        _shell = shell;
        LoadLinks();
        LoadActionRows(ProjectFileScope.Shared);
        LoadActionRows(ProjectFileScope.Local);
        ProjectActionFiles =
        [
            new SettingChoice<ProjectFileScope>(ProjectFileScope.Shared, $"Shared with the project ({ProjectFile.SharedName})"),
            new SettingChoice<ProjectFileScope>(ProjectFileScope.Local, $"Just me ({ProjectFile.LocalName})"),
        ];
        // The shared file first when it has actions, since that's where a project's are.
        SelectedProjectActionFile = ProjectActionFiles[_sharedRows.Count > 0 || _localRows.Count == 0 && _actionFileErrors.GetValueOrDefault(ProjectFileScope.Shared) is null ? 0 : 1];
        BuildToolChoices();
        _tab.PropertyChanged += OnTabPropertyChanged;
    }

    public void Dispose() => _tab.PropertyChanged -= OnTabPropertyChanged;

    /// <summary>The tab the pages are for: the one selected when the window opened.</summary>
    internal TabViewModel Tab => _tab;

    /// <summary>The tab's folder, where the project files are.</summary>
    public string Folder => _tab.Folder;

    /// <summary>The group's heading in the sidebar: the project's name when a provider found one, else the folder's.</summary>
    public string Heading => GroupName(_tab);

    /// <summary>"NightOwl" for a detected project, else the folder's name, as Settings' sidebar heads the tab's group.</summary>
    public static string GroupName(TabViewModel tab) => tab.Project?.Name ?? tab.FolderName;

    /// <summary>The small line at the top of each page: which tab the window follows.</summary>
    public string ForTabText => $"For the tab in {_tab.Folder}";

    /// <summary>"Shared (claudette.json)" or "Just me (claudette.local.json)": where a link or action is.</summary>
    public static string FileLabel(ProjectFileScope scope) =>
        scope == ProjectFileScope.Shared ? $"Shared ({ProjectFile.SharedName})" : $"Just me ({ProjectFile.LocalName})";

    /// <summary>The small dialog over the window: a link or an action being added or edited.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditor))]
    public partial ViewModelBase? Editor { get; private set; }

    public bool HasEditor => Editor is not null;

    private void CloseEditor() => Editor = null;

    /// <summary>A file was saved: every tab in the folder reads its project files again, as after the old editor's Apply.</summary>
    private void OnFileSaved() => _shell.OnProjectActionsChanged(_tab.Folder);

    /// <summary>A write that failed says why: the file's own reason when it isn't JSON, else what the OS said.</summary>
    private static string SaveFailure(ProjectFileScope scope, Exception ex) =>
        ex is InvalidOperationException ? ex.Message : $"Couldn't save {ProjectFile.FileName(scope)}: {ex.Message}";

    private static bool IsSaveFailure(Exception ex) => ex is InvalidOperationException or IOException or UnauthorizedAccessException;

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.Project) or null or "")
        {
            OnPropertyChanged(nameof(Heading));
            ToolsChanged();
        }
    }
}
