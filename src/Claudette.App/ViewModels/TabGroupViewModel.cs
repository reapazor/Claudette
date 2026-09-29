using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Claudette.App.ViewModels;

/// <summary>Tabs that share a working folder, shown together like browser tab groups (DESIGN.md §4, "Grouped by folder").</summary>
public sealed partial class TabGroupViewModel : ObservableObject
{
    public static readonly IReadOnlyList<Color> Palette =
    [
        Color.Parse("#4C8DFF"), Color.Parse("#3DBE72"), Color.Parse("#F29B38"), Color.Parse("#A66BFF"),
        Color.Parse("#F25CA2"), Color.Parse("#25B8B8"), Color.Parse("#D9B51A"), Color.Parse("#F2555A"),
    ];

    public TabGroupViewModel(string folder, int colorIndex, bool isCollapsed)
    {
        Folder = folder;
        ColorIndex = colorIndex;
        IsCollapsed = isCollapsed;
        Label = FolderName;
        Tabs.CollectionChanged += OnTabsChanged;
    }

    public string Folder { get; }

    public string FolderName => Path.GetFileName(Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name ? name : Folder;

    /// <summary>The folder name, or <c>parent/name</c> when two groups would otherwise look the same.</summary>
    [ObservableProperty]
    public partial string Label { get; set; }

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Brush), nameof(SoftBrush))]
    public partial int ColorIndex { get; set; }

    public IBrush Brush => new SolidColorBrush(Palette[ColorIndex % Palette.Count]);

    public IBrush SoftBrush => new SolidColorBrush(Palette[ColorIndex % Palette.Count], 0.18);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExpanded), nameof(CollapseMenuText))]
    public partial bool IsCollapsed { get; set; }

    public bool IsExpanded => !IsCollapsed;

    public string CollapseMenuText => IsCollapsed ? "Expand group" : "Collapse group";

    /// <summary>The most urgent status among the group's tabs, shown when it's collapsed.</summary>
    public string UrgentGlyph =>
        Tabs.Any(t => t.Status == TabStatus.NeedsInput) ? "!"
        : Tabs.Any(t => t.Status == TabStatus.Error) ? "✕"
        : Tabs.Any(t => t.IsBusyStatus) ? "●"
        : Tabs.Any(t => t.Status == TabStatus.Unread) ? "•"
        : "";

    public string Summary => $"{Tabs.Count} tab{(Tabs.Count == 1 ? "" : "s")} in {Folder}";

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var tab in e.NewItems?.OfType<TabViewModel>() ?? [])
        {
            tab.PropertyChanged += OnTabPropertyChanged;
        }
        foreach (var tab in e.OldItems?.OfType<TabViewModel>() ?? [])
        {
            tab.PropertyChanged -= OnTabPropertyChanged;
        }
        OnPropertyChanged(nameof(UrgentGlyph));
        OnPropertyChanged(nameof(Summary));
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.Status))
        {
            OnPropertyChanged(nameof(UrgentGlyph));
        }
    }
}
