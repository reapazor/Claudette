using Claudette.Core.Diffs;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels.Settings;

/// <summary>A diff tool choice in Settings: built-in, an installed preset, or a custom command.</summary>
public sealed record DiffToolOption(string Kind, string? PresetId, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Settings → Diff tool (DESIGN.md §8, "External diff tool"): how changed files open, with <b>Test</b>.</summary>
public sealed partial class DiffToolPage(SettingsContext context) : SettingsPage(context, SettingsCategory.DiffTool)
{
    public override IEnumerable<SettingsSearchResult> SearchEntries =>
    [
        Entry("Diff tool"),
        Entry("Custom diff command", pageText: "Command"),
        Entry("Test the diff tool", pageText: "Test"),
    ];

    /// <summary>Built-in, each preset whose tool is installed here, and a custom command.</summary>
    public IReadOnlyList<DiffToolOption> DiffToolOptions => field ??=
    [
        new DiffToolOption("builtIn", null, "Built-in diff view"),
        .. DiffToolDetector.Detect(Services.UserEnvironment.Probe).Select(d => new DiffToolOption("preset", d.Preset.Id, $"{d.Preset.Name}  ({d.ExecutablePath})")),
        new DiffToolOption("custom", null, "Custom command…"),
    ];

    public DiffToolOption SelectedDiffTool
    {
        get
        {
            var settings = Settings.DiffTool;
            return DiffToolOptions.FirstOrDefault(o => o.Kind == settings.Kind && (o.Kind != "preset" || o.PresetId == settings.PresetId))
                ?? DiffToolOptions[0];
        }
        set
        {
            if (value is null)
            {
                return;
            }
            Settings.DiffTool.Kind = value.Kind;
            Settings.DiffTool.PresetId = value.PresetId;
            Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustomDiffTool));
            OnPropertyChanged(nameof(CanTestDiffTool));
            DiffToolTestResult = null;
        }
    }

    public bool IsCustomDiffTool => Settings.DiffTool.Kind == "custom";

    public bool CanTestDiffTool => Settings.DiffTool.Kind != "builtIn";

    public string CustomDiffCommand
    {
        get => Settings.DiffTool.CustomCommand ?? "";
        set
        {
            Settings.DiffTool.CustomCommand = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(CustomDiffCommandError));
        }
    }

    public string? CustomDiffCommandError =>
        Settings.DiffTool.CustomCommand is { } command && !DiffToolCommand.TryParse(command, out _, out var error) ? error : null;

    [ObservableProperty]
    public partial string? DiffToolTestResult { get; set; }

    /// <summary>Opens a sample diff so the user can check the tool works.</summary>
    [RelayCommand]
    private async Task TestDiffToolAsync()
    {
        var settings = Settings.DiffTool;
        var choice = settings.Kind == "custom"
            ? new DiffToolChoice(DiffToolKind.Custom, CustomCommand: settings.CustomCommand)
            : new DiffToolChoice(DiffToolKind.Preset, settings.PresetId);
        try
        {
            await new DiffToolLauncher(Services.Launcher, Services.Time, environment: Services.UserEnvironment).TestAsync(choice, Path.Combine(Services.Paths.DiffTempDirectory, "test"));
            DiffToolTestResult = "Opened a sample diff. If nothing appeared, check the command.";
        }
        catch (Exception ex)
        {
            DiffToolTestResult = $"Couldn't open it: {ex.Message}";
        }
    }

    protected override void ResetSettings()
    {
        Settings.DiffTool = new DiffToolSettings();
        Save();
    }
}
