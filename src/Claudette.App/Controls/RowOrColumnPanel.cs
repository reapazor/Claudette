using Avalonia;
using Avalonia.Controls;

namespace Claudette.App.Controls;

/// <summary>
/// The header's weekly meters (DESIGN.md §6): side by side when they all fit on one line, or else stacked one above
/// the other, at the start. A narrow window keeps every meter whole rather than cutting off the last.
/// </summary>
public sealed class RowOrColumnPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<RowOrColumnPanel, double>(nameof(Spacing));

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<RowOrColumnPanel, double>(nameof(RowSpacing));

    /// <summary>The space between the children, side by side.</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>The space between the lines, when the children are stacked.</summary>
    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    static RowOrColumnPanel()
    {
        AffectsMeasure<RowOrColumnPanel>(SpacingProperty, RowSpacingProperty);
    }

    /// <summary>Whether the children were stacked at the last arrange.</summary>
    public bool IsStacked { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        foreach (var child in Children)
        {
            child.Measure(unbounded);
        }
        var shown = Shown();
        if (shown.Count == 0)
        {
            return default;
        }
        return Fits(shown, availableSize.Width)
            ? new Size(RowWidth(shown), shown.Max(c => c.DesiredSize.Height))
            : new Size(shown.Max(c => c.DesiredSize.Width), ColumnHeight(shown));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var shown = Shown();
        IsStacked = !Fits(shown, finalSize.Width);
        if (!IsStacked)
        {
            var (x, height) = (0.0, shown.Count == 0 ? 0 : shown.Max(c => c.DesiredSize.Height));
            foreach (var child in shown)
            {
                child.Arrange(new Rect(x, 0, child.DesiredSize.Width, height));
                x += child.DesiredSize.Width + Spacing;
            }
        }
        else
        {
            var y = 0.0;
            foreach (var child in shown)
            {
                child.Arrange(new Rect(0, y, child.DesiredSize.Width, child.DesiredSize.Height));
                y += child.DesiredSize.Height + RowSpacing;
            }
        }
        return finalSize;
    }

    private List<Control> Shown() => Children.Where(c => c.IsVisible).ToList();

    private bool Fits(List<Control> shown, double width) =>
        shown.Count < 2 || double.IsInfinity(width) || RowWidth(shown) <= width;

    private double RowWidth(List<Control> shown) => shown.Sum(c => c.DesiredSize.Width) + (Spacing * (shown.Count - 1));

    private double ColumnHeight(List<Control> shown) => shown.Sum(c => c.DesiredSize.Height) + (RowSpacing * (shown.Count - 1));
}
