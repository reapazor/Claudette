using Claudette.Core.ProjectTools;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels.Settings;

/// <summary>
/// Settings → Project tools (DESIGN.md §18, "Project tools"): the defaults every Unreal, Unity and Godot project starts
/// with, and how solutions open. Kept on each machine, like the diff tool.
/// </summary>
public sealed partial class ProjectToolsPage(SettingsContext context) : SettingsPage(context, SettingsCategory.ProjectTools)
{
    public override IEnumerable<SettingsSearchResult> SearchEntries =>
    [
        Entry("Unreal: default editor configuration", pageText: "Default editor configuration"),
        Entry("Project files for"),
        Entry("Open solutions with"),
        Entry("Tell Claude about Unreal projects"),
        Entry("Unity: default code optimization", pageText: "Default code optimization"),
        Entry("Tell Claude about Unity projects"),
        Entry("Godot executable"),
        Entry("Tell Claude about Godot projects"),
    ];

    // ---- Unreal Engine ---------------------------------------------------------------------------------------------

    public IReadOnlyList<SettingChoice<UnrealConfiguration>> UnrealConfigurationChoices { get; } =
    [
        new(UnrealConfiguration.Development, "Development"),
        new(UnrealConfiguration.DebugGame, "DebugGame (loads the DebugGame modules)"),
    ];

    /// <summary>The configuration a project uses until it's given its own in the project's menu.</summary>
    public SettingChoice<UnrealConfiguration> SelectedUnrealConfiguration
    {
        get => UnrealConfigurationChoices.First(c => c.Value == Settings.ProjectTools.UnrealConfiguration);
        set
        {
            if (value is not null)
            {
                Set(value.Value, v => Settings.ProjectTools.UnrealConfiguration = v);
            }
        }
    }

    public IReadOnlyList<SettingChoice<ProjectFileFormat>> ProjectFileFormatChoices { get; } =
    [
        .. Enum.GetValues<ProjectFileFormat>().Select(f => new SettingChoice<ProjectFileFormat>(f,
            Core.ProjectTools.Unreal.UnrealProvider.FormatName(f) + (f == ProjectToolSettings.DefaultFormat(ToolOSExtensions.Current) ? " (the default here)" : ""))),
    ];

    public SettingChoice<ProjectFileFormat> SelectedProjectFileFormat
    {
        get => ProjectFileFormatChoices.First(c => c.Value == Settings.ProjectTools.FormatFor(ToolOSExtensions.Current));
        set
        {
            if (value is not null)
            {
                Set(value.Value, v => Settings.ProjectTools.ProjectFileFormat = v == ProjectToolSettings.DefaultFormat(ToolOSExtensions.Current) ? null : v);
            }
        }
    }

    public bool TellClaudeAboutUnreal
    {
        get => Settings.ProjectTools.TellClaudeAboutUnreal;
        set => Set(value, v => Settings.ProjectTools.TellClaudeAboutUnreal = v);
    }

    // ---- Unity -------------------------------------------------------------------------------------------------------

    public IReadOnlyList<SettingChoice<UnityCodeOptimization>> UnityOptimizationChoices { get; } =
    [
        new(UnityCodeOptimization.Release, "Release"),
        new(UnityCodeOptimization.Debug, "Debug (-debugCodeOptimization, for stepping through scripts)"),
    ];

    /// <summary>The code optimization a Unity project opens with until it's given its own in the project's menu.</summary>
    public SettingChoice<UnityCodeOptimization> SelectedUnityOptimization
    {
        get => UnityOptimizationChoices.First(c => c.Value == Settings.ProjectTools.UnityCodeOptimization);
        set
        {
            if (value is not null)
            {
                Set(value.Value, v => Settings.ProjectTools.UnityCodeOptimization = v);
            }
        }
    }

    public bool TellClaudeAboutUnity
    {
        get => Settings.ProjectTools.TellClaudeAboutUnity;
        set => Set(value, v => Settings.ProjectTools.TellClaudeAboutUnity = v);
    }

    // ---- Godot (DESIGN.md §18, "Godot") ------------------------------------------------------------------------------

    /// <summary>The Godot executable; empty finds it.</summary>
    public string GodotPath
    {
        get => Settings.ProjectTools.GodotPath ?? "";
        set => Set(value, v => Settings.ProjectTools.GodotPath = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
    }

    [RelayCommand]
    private async Task BrowseGodotAsync()
    {
        if (await Services.Platform.PickFileAsync("Choose the Godot executable") is { } path)
        {
            GodotPath = path;
            OnPropertyChanged(nameof(GodotPath));
        }
    }

    /// <summary>What <b>Detect</b> found: where Godot usually is, ignoring the path set here.</summary>
    [ObservableProperty]
    public partial string? GodotDetectResult { get; set; }

    [RelayCommand]
    private async Task DetectGodotAsync()
    {
        var context = Services.ProjectTools.Context();
        var found = await Task.Run(() => Core.ProjectTools.Godot.GodotExecutables.Detect(context));
        if (found is null)
        {
            GodotDetectResult = "Godot wasn't found on the PATH or where its installers put it. Browse to it instead.";
            return;
        }
        GodotPath = found;
        OnPropertyChanged(nameof(GodotPath));
        GodotDetectResult = $"Found {found}.";
    }

    public bool TellClaudeAboutGodot
    {
        get => Settings.ProjectTools.TellClaudeAboutGodot;
        set => Set(value, v => Settings.ProjectTools.TellClaudeAboutGodot = v);
    }

    // ---- Solutions ---------------------------------------------------------------------------------------------------

    public IReadOnlyList<SettingChoice<SolutionOpener>> SolutionOpenerChoices { get; } =
    [
        new(SolutionOpener.System, "The OS's default app"),
        new(SolutionOpener.Rider, "Rider"),
        new(SolutionOpener.VisualStudio, "Visual Studio"),
        new(SolutionOpener.VSCode, "VS Code"),
        new(SolutionOpener.Custom, "Another program…"),
    ];

    public SettingChoice<SolutionOpener> OpenSolutionsWith
    {
        get => SolutionOpenerChoices.First(c => c.Value == Settings.ProjectTools.OpenSolutionsWith);
        set
        {
            if (value is not null)
            {
                Set(value.Value, v => Settings.ProjectTools.OpenSolutionsWith = v);
                OnPropertyChanged(nameof(IsCustomIde));
            }
        }
    }

    public bool IsCustomIde => Settings.ProjectTools.OpenSolutionsWith == SolutionOpener.Custom;

    /// <summary>The program <b>Open solution</b> starts with the solution's path, for "Another program…".</summary>
    public string CustomIdePath
    {
        get => Settings.ProjectTools.CustomIdePath ?? "";
        set => Set(value, v => Settings.ProjectTools.CustomIdePath = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
    }

    [RelayCommand]
    private async Task BrowseCustomIdeAsync()
    {
        if (await Services.Platform.PickFileAsync("Choose the program to open solutions with") is { } path)
        {
            CustomIdePath = path;
        }
    }

    /// <summary>A project's own choices, made in its menu or on its Tools page, stay as they are.</summary>
    protected override void ResetSettings()
    {
        Settings.ProjectTools = new ProjectToolSettings();
        Save();
        GodotDetectResult = null;
    }
}
