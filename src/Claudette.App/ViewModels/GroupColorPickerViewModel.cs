using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// A folder group's color picker, from <b>Change color…</b> in the group's menu (DESIGN.md §4, "Grouped by folder"): the
/// group colors as swatches, and any other color from a spectrum and hue slider or as a hex code. Whatever it picks
/// applies to the group at once, and the shell remembers it for the folder.
/// </summary>
public sealed partial class GroupColorPickerViewModel : ObservableObject
{
    private readonly Action<Color> _apply;

    /// <summary>Set while one of the color's forms follows another, so the change isn't applied again.</summary>
    private bool _syncing;

    public GroupColorPickerViewModel(TabGroupViewModel group, Action<Color> apply)
    {
        Group = group;
        _apply = apply;
        Swatches = TabGroupViewModel.Palette.Select(p => new ColorSwatch(p.Name, p.Color)).ToList();
        _syncing = true;
        HsvColor = group.Color.ToHsv();
        Hex = TabGroupViewModel.ToHex(group.Color);
        _syncing = false;
        MarkSwatch(group.Color);
    }

    public TabGroupViewModel Group { get; }

    public IReadOnlyList<ColorSwatch> Swatches { get; }

    /// <summary>The color as the spectrum and hue slider show it. Kept as HSV so the hue survives a grey or black.</summary>
    [ObservableProperty]
    public partial HsvColor HsvColor { get; set; }

    /// <summary>The color as <c>#RRGGBB</c>. Text that isn't a color yet (while it's typed) changes nothing.</summary>
    [ObservableProperty]
    public partial string Hex { get; set; }

    /// <summary>Raised when a swatch is picked or Enter is pressed in the hex box: the picker closes.</summary>
    public event Action? Done;

    partial void OnHsvColorChanged(HsvColor value)
    {
        if (!_syncing)
        {
            Apply(value.ToRgb(), fromHsv: true);
        }
    }

    partial void OnHexChanged(string value)
    {
        if (!_syncing && TabGroupViewModel.TryParseHex(value, out var color))
        {
            Apply(color, fromHex: true);
        }
    }

    [RelayCommand]
    private void Pick(ColorSwatch? swatch)
    {
        if (swatch is null)
        {
            return;
        }
        Apply(swatch.Color);
        Done?.Invoke();
    }

    [RelayCommand]
    private void Finish() => Done?.Invoke();

    private void Apply(Color color, bool fromHsv = false, bool fromHex = false)
    {
        color = Color.FromRgb(color.R, color.G, color.B);
        _syncing = true;
        if (!fromHsv)
        {
            HsvColor = color.ToHsv();
        }
        if (!fromHex)
        {
            Hex = TabGroupViewModel.ToHex(color);
        }
        _syncing = false;
        MarkSwatch(color);
        _apply(color);
    }

    private void MarkSwatch(Color color)
    {
        foreach (var swatch in Swatches)
        {
            swatch.IsSelected = swatch.Color == color;
        }
    }
}

/// <summary>A group color in the picker, ringed when it's the group's color.</summary>
public sealed partial class ColorSwatch(string name, Color color) : ObservableObject
{
    public string Name { get; } = name;

    public Color Color { get; } = color;

    public IBrush Brush { get; } = new SolidColorBrush(color);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
