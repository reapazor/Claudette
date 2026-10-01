using Claudette.Core;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Claudette.App.ViewModels;

/// <summary>Tabs that share a working folder, shown together like browser tab groups (DESIGN.md §4, "Grouped by folder").</summary>
public sealed partial class TabGroupViewModel : ObservableObject
{
    /// <summary>The colors a new group takes in turn, and the swatches of the color picker.</summary>
    public static readonly IReadOnlyList<NamedColor> Palette =
    [
        new("Blue", Color.Parse("#4C8DFF")), new("Green", Color.Parse("#3DBE72")), new("Orange", Color.Parse("#F29B38")),
        new("Purple", Color.Parse("#A66BFF")), new("Pink", Color.Parse("#F25CA2")), new("Teal", Color.Parse("#25B8B8")),
        new("Yellow", Color.Parse("#D9B51A")), new("Red", Color.Parse("#F2555A")),
    ];

    public TabGroupViewModel(string folder, Color color, bool isCollapsed)
    {
        Folder = folder;
        Color = color;
        IsCollapsed = isCollapsed;
        Label = FolderName;
        Tabs.CollectionChanged += OnTabsChanged;
    }

    public string Folder { get; }

    public string FolderName => Formats.FolderName(Folder);

    /// <summary>The folder is in a git repository, so a tab can work in a worktree of it (DESIGN.md §4, "Worktree tabs").</summary>
    public bool CanMakeWorktrees => _canMakeWorktrees ??= Core.Git.GitInfo.TryGetBranch(Folder) is not null;

    private bool? _canMakeWorktrees;

    /// <summary>The folder name, or <c>parent/name</c> when two groups would otherwise look the same.</summary>
    [ObservableProperty]
    public partial string Label { get; set; }

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Brush), nameof(SoftBrush))]
    public partial Color Color { get; set; }

    public IBrush Brush => new SolidColorBrush(Color);

    public IBrush SoftBrush => new SolidColorBrush(Color, 0.18);

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

    /// <summary>A color as it's saved and typed: <c>#RRGGBB</c>. Group colors are opaque.</summary>
    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// Reads a color typed or saved as <c>#RRGGBB</c> or <c>#RGB</c>, with or without the <c>#</c>. Any transparency is
    /// dropped.
    /// </summary>
    public static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        var hex = text?.Trim().TrimStart('#') ?? "";
        if (hex.Length is not (3 or 6) || !hex.All(char.IsAsciiHexDigit) || !Color.TryParse("#" + hex, out var parsed))
        {
            return false;
        }
        color = Color.FromRgb(parsed.R, parsed.G, parsed.B);
        return true;
    }
}

/// <summary>One of the group colors, with the name its swatch has for screen readers and in its tip.</summary>
public sealed record NamedColor(string Name, Color Color);
