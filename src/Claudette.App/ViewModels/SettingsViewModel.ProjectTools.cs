using Claudette.Core.ProjectTools;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>A choice in one of Settings' dropdowns, with its label.</summary>
public sealed record SettingChoice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Settings → Project tools (DESIGN.md §18, "Project tools"), and its search entries.</summary>
public sealed partial class SettingsViewModel
{
    public bool IsProjectTools => SelectedCategory == "Project tools";

    private static IEnumerable<SettingsSearchResult> ProjectToolsSearchEntries() =>
    [
        new("Project tools", "Unreal: default editor configuration"),
        new("Project tools", "Project files for"),
        new("Project tools", "Open solutions with"),
        new("Project tools", "Tell Claude about Unreal projects"),
    ];

    public IReadOnlyList<SettingChoice<UnrealConfiguration>> UnrealConfigurationChoices { get; } =
    [
        new(Core.Settings.UnrealConfiguration.Development, "Development"),
        new(Core.Settings.UnrealConfiguration.DebugGame, "DebugGame (loads the DebugGame modules)"),
    ];

    /// <summary>The configuration a project uses until it's given its own in the chip menu.</summary>
    public SettingChoice<UnrealConfiguration> SelectedUnrealConfiguration
    {
        get => UnrealConfigurationChoices.First(c => c.Value == _settings.ProjectTools.UnrealConfiguration);
        set
        {
            if (value is not null)
            {
                Set(value.Value, v => _settings.ProjectTools.UnrealConfiguration = v);
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
        get => ProjectFileFormatChoices.First(c => c.Value == _settings.ProjectTools.FormatFor(ToolOSExtensions.Current));
        set
        {
            if (value is not null)
            {
                Set(value.Value, v => _settings.ProjectTools.ProjectFileFormat = v == ProjectToolSettings.DefaultFormat(ToolOSExtensions.Current) ? null : v);
            }
        }
    }

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
        get => SolutionOpenerChoices.First(c => c.Value == _settings.ProjectTools.OpenSolutionsWith);
        set
        {
            if (value is not null)
            {
                Set(value.Value, v => _settings.ProjectTools.OpenSolutionsWith = v);
                OnPropertyChanged(nameof(IsCustomIde));
            }
        }
    }

    public bool IsCustomIde => _settings.ProjectTools.OpenSolutionsWith == SolutionOpener.Custom;

    /// <summary>The program <b>Open solution</b> starts with the solution's path, for "Another program…".</summary>
    public string CustomIdePath
    {
        get => _settings.ProjectTools.CustomIdePath ?? "";
        set => Set(value, v => _settings.ProjectTools.CustomIdePath = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
    }

    [RelayCommand]
    private async Task BrowseCustomIdeAsync()
    {
        if (await _services.Platform.PickFileAsync("Choose the program to open solutions with") is { } path)
        {
            CustomIdePath = path;
        }
    }

    public bool TellClaudeAboutUnreal
    {
        get => _settings.ProjectTools.TellClaudeAboutUnreal;
        set => Set(value, v => _settings.ProjectTools.TellClaudeAboutUnreal = v);
    }

    /// <summary>Notifications → A project action finishes.</summary>
    public bool NotifyProjectActions
    {
        get => _settings.Notifications.ProjectActions;
        set => Set(value, v => _settings.Notifications.ProjectActions = v);
    }

    [RelayCommand]
    private void ResetProjectTools()
    {
        _settings.ProjectTools = new ProjectToolSettings();
        Save();
        OnPropertyChanged(nameof(SelectedUnrealConfiguration));
        OnPropertyChanged(nameof(SelectedProjectFileFormat));
        OnPropertyChanged(nameof(OpenSolutionsWith));
        OnPropertyChanged(nameof(IsCustomIde));
        OnPropertyChanged(nameof(CustomIdePath));
        OnPropertyChanged(nameof(TellClaudeAboutUnreal));
    }
}
