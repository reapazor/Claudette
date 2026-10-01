using System.Collections.ObjectModel;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels.Settings;

/// <summary>One quick suffix being edited in Settings.</summary>
public sealed partial class QuickSuffixEditor(QuickSuffix suffix, Action changed) : ShortcutEditor
{
    public QuickSuffix Suffix { get; } = suffix;

    /// <summary>Adds the suffix straight from the keyboard (DESIGN.md §5, "Own shortcut").</summary>
    public override KeyChord? Shortcut => KeyChord.TryParse(Suffix.Shortcut, out var chord) ? chord : null;

    public bool HasShortcut => Shortcut is not null;

    protected override void OnRefresh() => OnPropertyChanged(nameof(HasShortcut));

    public string Label
    {
        get => Suffix.Label;
        set
        {
            if (Suffix.Label != value)
            {
                Suffix.Label = value;
                OnPropertyChanged();
                changed();
            }
        }
    }

    public string Text
    {
        get => Suffix.Text;
        set
        {
            if (Suffix.Text != value)
            {
                Suffix.Text = value;
                OnPropertyChanged();
                changed();
            }
        }
    }
}

/// <summary>
/// Settings → Quick suffixes (DESIGN.md §5, "Quick suffixes"): each one's label, text and own shortcut, added, moved
/// and deleted here.
/// </summary>
public sealed partial class QuickSuffixesPage : SettingsPage
{
    public QuickSuffixesPage(SettingsContext context) : base(context, SettingsCategory.QuickSuffixes)
    {
        LoadSuffixes();
        context.Shortcuts.Saved += OnShortcutsSaved;
    }

    public override IEnumerable<SettingsSearchResult> SearchEntries =>
    [
        Entry("Add suffix"),
        Entry("Suffix shortcuts", pageText: "Shortcut"),
    ];

    public ObservableCollection<QuickSuffixEditor> Suffixes { get; } = [];

    /// <summary>The Suffixes menu's own shortcut, which the page's introduction names.</summary>
    public string SuffixesShortcutText => Services.Tips.Text(KeyboardShortcuts.Suffixes) ?? "no shortcut";

    private void LoadSuffixes()
    {
        Suffixes.Clear();
        foreach (var suffix in Settings.QuickSuffixes)
        {
            Suffixes.Add(new QuickSuffixEditor(suffix, Save));
        }
    }

    [RelayCommand]
    private void AddSuffix()
    {
        var suffix = new QuickSuffix { Label = "New suffix", Text = "" };
        Settings.QuickSuffixes.Add(suffix);
        Suffixes.Add(new QuickSuffixEditor(suffix, Save));
        Save();
    }

    [RelayCommand]
    private void RemoveSuffix(QuickSuffixEditor? editor)
    {
        if (editor is not null)
        {
            Settings.QuickSuffixes.Remove(editor.Suffix);
            Suffixes.Remove(editor);
            Save();
        }
    }

    [RelayCommand]
    private void MoveSuffixUp(QuickSuffixEditor? editor) => MoveSuffix(editor, -1);

    [RelayCommand]
    private void MoveSuffixDown(QuickSuffixEditor? editor) => MoveSuffix(editor, 1);

    private void MoveSuffix(QuickSuffixEditor? editor, int offset)
    {
        if (editor is null)
        {
            return;
        }
        var from = Suffixes.IndexOf(editor);
        var to = from + offset;
        if (to < 0 || to >= Suffixes.Count)
        {
            return;
        }
        Suffixes.Move(from, to);
        Settings.QuickSuffixes = Suffixes.Select(e => e.Suffix).ToList();
        Save();
    }

    // ---- Each suffix's own shortcut, checked against the commands' (DESIGN.md §14, "Keyboard shortcuts") ----------

    [RelayCommand]
    private void StartRecording(ShortcutEditor? editor) => Context.Shortcuts.Start(editor);

    [RelayCommand]
    private void ClearShortcut(ShortcutEditor? editor) => Context.Shortcuts.Clear(editor);

    private void OnShortcutsSaved()
    {
        foreach (var suffix in Suffixes)
        {
            suffix.Refresh();
        }
        OnPropertyChanged(nameof(SuffixesShortcutText));
    }

    protected override void ResetSettings()
    {
        Settings.QuickSuffixes = QuickSuffix.Defaults();
        LoadSuffixes();
        Save();
    }
}
