using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Claudette.Core.Settings;
using Path = Avalonia.Controls.Shapes.Path;

namespace Claudette.App.Views;

/// <summary>
/// A tab's mark (DESIGN.md §4, "Marks"), drawn as its vector icon from App.axaml (<c>IconMarkCheck</c> and so on) in
/// its color from the <c>Path.mark</c> styles there, and hidden when there's none. The tab's row, the rail, the tab's
/// menu and History all draw marks with it, so a mark looks the same in each.
/// </summary>
public sealed class MarkIcon : Path
{
    public static readonly StyledProperty<TabMark?> MarkProperty = AvaloniaProperty.Register<MarkIcon, TabMark?>(nameof(Mark));

    static MarkIcon() => MarkProperty.Changed.AddClassHandler<MarkIcon>((icon, _) => icon.Show());

    public MarkIcon()
    {
        Classes.Add("mark");
        Show();
    }

    public TabMark? Mark
    {
        get => GetValue(MarkProperty);
        set => SetValue(MarkProperty, value);
    }

    /// <summary>Styled as a Path, so the styles read <c>Path.mark.check</c> and so on.</summary>
    protected override Type StyleKeyOverride => typeof(Path);

    private void Show()
    {
        foreach (var mark in TabMarks.All)
        {
            Classes.Set(TabMarks.Key(mark), mark == Mark);
        }
        Data = Mark is { } shown && Application.Current?.TryFindResource($"IconMark{shown}", out var icon) == true ? icon as Geometry : null;
        IsVisible = Mark is not null;
    }
}
