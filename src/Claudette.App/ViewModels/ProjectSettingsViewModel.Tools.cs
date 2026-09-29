using Claudette.Core.ProjectTools;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// Settings → the tab's project → <b>Tools</b> (DESIGN.md §14, §18): the choices this machine remembers for the tab's
/// project, the same <c>ProjectToolState</c> values the project menus change. Only those its provider has: the project
/// when the folder has several, the per-project choice (Unreal's editor configuration, Unity's code optimization) and
/// what's picked with <b>Choose…</b> (Unreal's engine folder, the Unity editor, the Godot executable), with
/// <b>Clear</b> to forget the pick and find it again.
/// </summary>
public sealed partial class ProjectSettingsViewModel
{
    private IReadOnlyList<SettingChoice<string>> _projectChoices = [];
    private IReadOnlyList<SettingChoice<string>> _choiceOptions = [];

    private ProjectInfo? ToolsProject => _tab.Project;

    /// <summary>A provider found a project for the tab's folder.</summary>
    public bool HasToolsProject => ToolsProject is not null;

    /// <summary>"NightOwl · Unreal Engine".</summary>
    public string ToolsTitle => _tab.ProjectHeaderTitle;

    /// <summary>What the choices are kept by: the project's file (a <c>.uproject</c>, <c>project.godot</c>) or folder.</summary>
    public string ToolsKeyText => ToolsProject is { } project ? $"Remembered on this machine for {project.ProjectPath}." : "";

    // ---- The project, when the folder has several --------------------------------------------------------------------

    public bool HasSeveralProjects => _projectChoices.Count > 1;

    /// <summary>Each project found for the folder, nearest first, by name and where it is.</summary>
    public IReadOnlyList<SettingChoice<string>> ProjectChoices => _projectChoices;

    /// <summary>The project the folder uses. Remembered per folder, as the menus' <b>Projects in this folder</b>.</summary>
    public SettingChoice<string>? SelectedProjectChoice
    {
        get => ToolsProject is { } project ? _projectChoices.FirstOrDefault(c => SamePath(c.Value, project.ProjectPath)) : null;
        set
        {
            if (value is null || ToolsProject is { } project && SamePath(value.Value, project.ProjectPath))
            {
                return;
            }
            _services.ProjectTools.State.ChooseProject(Folder, value.Value);
            _services.SaveState();
            _ = _tab.RefreshProjectAsync();
        }
    }

    // ---- The per-project choice ------------------------------------------------------------------------------------------

    public bool HasToolsChoice => ToolsProject?.Choice is not null;

    /// <summary>"Editor configuration" for Unreal, "Code optimization" for Unity.</summary>
    public string ToolsChoiceLabel => ToolsProject?.Choice?.Label ?? "";

    public IReadOnlyList<SettingChoice<string>> ToolsChoiceOptions => _choiceOptions;

    /// <summary>The project's own choice, or Settings → Project tools' default until it has one.</summary>
    public SettingChoice<string>? SelectedToolsChoice
    {
        get => ToolsProject?.Choice is { } choice ? _choiceOptions.FirstOrDefault(o => o.Value == choice.Selected) : null;
        set
        {
            if (value is null || ToolsProject is not { Choice: { } choice } project || value.Value == choice.Selected)
            {
                return;
            }
            _services.ProjectTools.Remember(project.ProjectPath, choice.Key, value.Value);
            _ = _tab.RefreshProjectAsync();
        }
    }

    // ---- What's chosen with Choose… ----------------------------------------------------------------------------------

    private ProjectFix? ToolsFix => ToolsProject?.Fix;

    public bool HasToolsFix => ToolsFix is not null;

    /// <summary>"Engine folder", "Unity editor", "Godot executable".</summary>
    public string ToolsFixName => ToolsFix?.Name ?? "";

    /// <summary>The picker's title, for the button's tooltip: "Choose the Unreal Engine folder for NightOwl".</summary>
    public string ToolsFixTitle => ToolsFix?.Title ?? "";

    /// <summary>The folder or program in use, or why there's none: "The engine for EngineAssociation "5.9" wasn't found…".</summary>
    public string ToolsFixCurrent => ToolsFix?.Current ?? ToolsProject?.Problem ?? "Not found on this machine.";

    /// <summary>What's remembered for the project; null when Claudette finds it by itself.</summary>
    private string? ChosenFix => ToolsProject is { Fix: { } fix } project ? _services.ProjectTools.State.Get(project.ProjectPath, fix.Key) : null;

    /// <summary>Whether it was chosen or found: "Chosen by you.", "Found automatically."</summary>
    public string ToolsFixSource => ChosenFix switch
    {
        null when ToolsFix?.Current is null => "Not chosen: choose it here, or install it where Claudette looks.",
        null => "Found automatically.",
        { } chosen when ToolsFix?.Current is { } current && SamePath(chosen, current) => "Chosen by you. Clear forgets it, and Claudette looks for it again.",
        { } chosen => $"You chose {chosen}, which can't be used now. Clear forgets it.",
    };

    /// <summary>Why the last pick can't be used.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToolsFixError))]
    public partial string? ToolsFixError { get; private set; }

    public bool HasToolsFixError => ToolsFixError is not null;

    /// <summary><b>Choose…</b>: pick, check, remember for the project, look again, as <b>Choose engine folder…</b> does.</summary>
    [RelayCommand]
    private async Task ChooseToolsFixAsync()
    {
        if (ToolsProject is not { Fix: { } fix } project)
        {
            return;
        }
        var picked = fix.PickFolder ? await _services.Platform.PickFolderAsync(fix.Title) : await _services.Platform.PickFileAsync(fix.Title);
        if (picked is null)
        {
            return;
        }
        var (path, error) = fix.Validate(picked);
        if (path is null)
        {
            ToolsFixError = error ?? $"{picked} can't be used.";
            return;
        }
        ToolsFixError = null;
        _services.ProjectTools.Remember(project.ProjectPath, fix.Key, path);
        ToolsChanged();
        await _tab.RefreshProjectAsync();
    }

    /// <summary><b>Clear</b>: forgets the pick, so Claudette finds the engine, editor or Godot by itself again.</summary>
    [RelayCommand(CanExecute = nameof(CanClearToolsFix))]
    private async Task ClearToolsFixAsync()
    {
        if (ToolsProject is not { Fix: { } fix } project)
        {
            return;
        }
        ToolsFixError = null;
        _services.ProjectTools.Remember(project.ProjectPath, fix.Key, null);
        ToolsChanged();
        await _tab.RefreshProjectAsync();
    }

    private bool CanClearToolsFix() => ChosenFix is not null;

    /// <summary>For the search box: what this project's Tools page has, such as "Engine folder".</summary>
    public IReadOnlyList<string> ToolsSearchLabels =>
        [.. new[] { ToolsProject?.Choice?.Label, ToolsFix?.Name }.OfType<string>().Where(l => l.Length > 0)];

    private void BuildToolChoices()
    {
        _projectChoices = [.. _tab.ProjectCandidates.Select(c => new SettingChoice<string>(c.Path, $"{c.Name} ({RelativeToFolder(c.Path)})"))];
        _choiceOptions = ToolsProject?.Choice is { } choice ? [.. choice.Options.Select(o => new SettingChoice<string>(o.Value, o.Label))] : [];
    }

    /// <summary>The tab's project was found again, or a choice changed: the page shows it as it is now.</summary>
    private void ToolsChanged()
    {
        BuildToolChoices();
        OnPropertyChanged(nameof(HasToolsProject));
        OnPropertyChanged(nameof(ToolsTitle));
        OnPropertyChanged(nameof(ToolsKeyText));
        OnPropertyChanged(nameof(HasSeveralProjects));
        OnPropertyChanged(nameof(ProjectChoices));
        OnPropertyChanged(nameof(SelectedProjectChoice));
        OnPropertyChanged(nameof(HasToolsChoice));
        OnPropertyChanged(nameof(ToolsChoiceLabel));
        OnPropertyChanged(nameof(ToolsChoiceOptions));
        OnPropertyChanged(nameof(SelectedToolsChoice));
        OnPropertyChanged(nameof(HasToolsFix));
        OnPropertyChanged(nameof(ToolsFixName));
        OnPropertyChanged(nameof(ToolsFixTitle));
        OnPropertyChanged(nameof(ToolsFixCurrent));
        OnPropertyChanged(nameof(ToolsFixSource));
        OnPropertyChanged(nameof(ToolsSearchLabels));
        ClearToolsFixCommand.NotifyCanExecuteChanged();
    }

    private string RelativeToFolder(string path)
    {
        try
        {
            return Path.GetRelativePath(Folder, path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private static bool SamePath(string a, string b) => Core.Settings.FolderHistory.SamePath(a, b);
}
