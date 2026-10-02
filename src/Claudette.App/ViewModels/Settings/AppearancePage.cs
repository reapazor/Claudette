using Claudette.Core.Settings;

namespace Claudette.App.ViewModels.Settings;

/// <summary>A Style choice in Settings → Appearance (DESIGN.md §3, "Visual style").</summary>
public sealed record StyleOption(AppStyle Style, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Settings → Appearance → Motion's choices (DESIGN.md §3, "Accessibility").</summary>
public sealed record MotionChoice(MotionSetting Setting)
{
    public override string ToString() => Setting switch
    {
        MotionSetting.Reduce => "Reduce motion",
        MotionSetting.Full => "Don't reduce",
        _ => "Follow the system",
    };
}

/// <summary>Settings → Appearance → Zoom's steps (DESIGN.md §3, "Accessibility").</summary>
public sealed record ZoomChoice(int Percent)
{
    public override string ToString() => $"{Percent}%";
}

/// <summary>
/// Settings → Appearance (DESIGN.md §14): theme, style and density, the fonts, zoom and motion, and what the
/// conversation, the working line, the usage header and the tab rows show.
/// </summary>
public sealed class AppearancePage(SettingsContext context) : SettingsPage(context, SettingsCategory.Appearance)
{
    public override IEnumerable<SettingsSearchResult> SearchEntries =>
    [
        Entry("Theme"),
        Entry("Style"),
        Entry("Conversation font"),
        Entry("Conversation font size"),
        Entry("Code font"),
        Entry("Code font size"),
        Entry("Show thinking expanded"),
        Entry("Show fun words while Claude works"),
        Entry("Show what Claude is doing while it works"),
        Entry("Detailed usage header"),
        Entry("Show context on tab rows"),
        Entry("Show git branch on tab rows"),
        Entry("Density"),
        Entry("Zoom"),
        Entry("Motion"),
        Entry("Reduce motion", pageText: "Motion"),
    ];

    public IReadOnlyList<ThemeChoice> Themes { get; } = [ThemeChoice.System, ThemeChoice.Light, ThemeChoice.Dark];

    public ThemeChoice Theme
    {
        get => Settings.Appearance.Theme;
        set => Set(value, v => Settings.Appearance.Theme = v);
    }

    /// <summary>Settings → Appearance → Style (DESIGN.md §3, "Visual style"), named as the list shows them.</summary>
    public IReadOnlyList<StyleOption> StyleOptions { get; } =
        [new(AppStyle.Standard, "Standard"), new(AppStyle.Claude, "Claude")];

    public StyleOption Style
    {
        get => StyleOptions.FirstOrDefault(o => o.Style == Settings.Appearance.Style) ?? StyleOptions[0];
        set => Set(value, v => Settings.Appearance.Style = v?.Style ?? AppStyle.Standard);
    }

    public IReadOnlyList<Density> Densities { get; } = [Density.Comfortable, Density.Compact];

    /// <summary>Compact tightens the conversation, the sidebar's rows and the composer (DESIGN.md §14).</summary>
    public Density Density
    {
        get => Settings.Appearance.Density;
        set => Set(value, v => Settings.Appearance.Density = v);
    }

    // ---- Fonts (DESIGN.md §14, "Appearance") ----------------------------------------------------------------------

    /// <summary>The fonts installed here, to pick from; any other name can be typed.</summary>
    public IReadOnlyList<string> InstalledFonts => field ??= Services.Platform.InstalledFonts();

    /// <summary>Empty for Claudette's own font.</summary>
    public string ConversationFont
    {
        get => Settings.Appearance.ConversationFont ?? "";
        set => Set(value, v => Settings.Appearance.ConversationFont = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
    }

    public decimal? ConversationFontSize
    {
        get => (decimal)Settings.Appearance.ConversationFontSize;
        set => Set(value, v => Settings.Appearance.ConversationFontSize = Math.Clamp((double)(v ?? 14), 9, 28));
    }

    /// <summary>Empty for the default monospace fonts.</summary>
    public string CodeFont
    {
        get => Settings.Appearance.CodeFont ?? "";
        set => Set(value, v => Settings.Appearance.CodeFont = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
    }

    public decimal? CodeFontSize
    {
        get => (decimal)Settings.Appearance.CodeFontSize;
        set => Set(value, v => Settings.Appearance.CodeFontSize = Math.Clamp((double)(v ?? 13), 8, 28));
    }

    // ---- Accessibility (DESIGN.md §3): the main window's scale, and whether anything moves -----------------------

    public IReadOnlyList<ZoomChoice> ZoomChoices { get; } = [.. Core.Accessibility.Zoom.Steps.Select(percent => new ZoomChoice(percent))];

    /// <summary>The main window's content, scaled (DESIGN.md §3, "Accessibility"); Ctrl/Cmd +, − and 0 change it too.</summary>
    public ZoomChoice Zoom
    {
        get => ZoomChoices.FirstOrDefault(c => c.Percent == Settings.Appearance.Zoom) ?? new ZoomChoice(Settings.Appearance.Zoom);
        set => Set(value?.Percent ?? Core.Accessibility.Zoom.Default, v => Settings.Appearance.Zoom = v);
    }

    public IReadOnlyList<MotionChoice> MotionChoices { get; } =
        [new(MotionSetting.System), new(MotionSetting.Reduce), new(MotionSetting.Full)];

    /// <summary>Whether the busy dots pulse, the working glyph twinkles and the taskbar or Dock icon moves.</summary>
    public MotionChoice Motion
    {
        get => MotionChoices.FirstOrDefault(c => c.Setting == Settings.Appearance.Motion) ?? MotionChoices[0];
        set => Set(value?.Setting ?? MotionSetting.System, v => Settings.Appearance.Motion = v);
    }

    // ---- What shows ------------------------------------------------------------------------------------------------

    public bool ExpandThinking
    {
        get => Settings.Appearance.ExpandThinking;
        set => Set(value, v => Settings.Appearance.ExpandThinking = v);
    }

    /// <summary>The working line's twinkling glyph and fun verbs (DESIGN.md §5, "Working line").</summary>
    public bool FunWorkingWords
    {
        get => Settings.Appearance.FunWorkingWords;
        set => Set(value, v => Settings.Appearance.FunWorkingWords = v);
    }

    /// <summary>The working line says what the running tool is doing (DESIGN.md §5, "Working line").</summary>
    public bool ShowToolInWorkingLine
    {
        get => Settings.Appearance.ShowToolInWorkingLine;
        set => Set(value, v => Settings.Appearance.ShowToolInWorkingLine = v);
    }

    /// <summary>
    /// The usage header drawn taller with charts (DESIGN.md §6, "Detailed header"), the same switch as its chevron. It's
    /// this machine's state rather than a setting, like the sidebar's collapsed state, so it doesn't sync.
    /// </summary>
    public bool DetailedUsageHeader
    {
        get => Services.State.DetailedUsageHeader;
        set
        {
            if (Services.State.DetailedUsageHeader != value)
            {
                Services.State.DetailedUsageHeader = value;
                Services.SaveState();
                OnPropertyChanged();
            }
        }
    }

    /// <summary>The context ring on each tab's row (DESIGN.md §4, "Sidebar").</summary>
    public bool ShowContextOnTabs
    {
        get => Settings.Appearance.ShowContextOnTabs;
        set => Set(value, v => Settings.Appearance.ShowContextOnTabs = v);
    }

    /// <summary>The git branch, or worktree, on each tab's row (DESIGN.md §4, "Sidebar").</summary>
    public bool ShowBranchOnTabs
    {
        get => Settings.Appearance.ShowBranchOnTabs;
        set => Set(value, v => Settings.Appearance.ShowBranchOnTabs = v);
    }

    /// <summary>The detailed usage header goes back to its default too, though it's kept with the machine's state.</summary>
    protected override void ResetSettings()
    {
        Settings.Appearance = new AppearanceSettings();
        Save();
        DetailedUsageHeader = false;
    }
}
