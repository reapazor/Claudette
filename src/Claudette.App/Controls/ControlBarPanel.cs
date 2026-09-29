using Avalonia;
using Avalonia.Controls;

namespace Claudette.App.Controls;

/// <summary>
/// The composer's control bar (DESIGN.md §5): its first child at the start and its last at the end, on one line when
/// both fit, or else the last on a line of its own under the first, still at the end. The chips on the left can outgrow
/// a narrow window, and Send, on the right, must never be pushed out of view.
/// </summary>
public sealed class ControlBarPanel : Panel
{
    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<ControlBarPanel, double>(nameof(RowSpacing), 4);

    /// <summary>The space between the lines, when the bar takes two.</summary>
    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    static ControlBarPanel()
    {
        AffectsMeasure<ControlBarPanel>(RowSpacingProperty);
    }

    /// <summary>Whether the bar took two lines at its last arrange.</summary>
    public bool IsWrapped { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0)
        {
            return default;
        }
        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
        foreach (var child in Children)
        {
            child.Measure(unbounded);
        }
        var (start, end) = (Children[0].DesiredSize, Children[^1].DesiredSize);
        return Fits(availableSize.Width)
            ? new Size(start.Width + end.Width, Math.Max(start.Height, end.Height))
            : new Size(Math.Max(start.Width, end.Width), start.Height + RowSpacing + end.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0)
        {
            return finalSize;
        }
        var (first, last) = (Children[0], Children[^1]);
        var (start, end) = (first.DesiredSize, last.DesiredSize);
        IsWrapped = !Fits(finalSize.Width);
        if (!IsWrapped)
        {
            var height = Math.Max(start.Height, end.Height);
            first.Arrange(new Rect(0, 0, start.Width, height));
            if (!ReferenceEquals(first, last))
            {
                last.Arrange(new Rect(finalSize.Width - end.Width, 0, end.Width, height));
            }
        }
        else
        {
            first.Arrange(new Rect(0, 0, start.Width, start.Height));
            last.Arrange(new Rect(Math.Max(0, finalSize.Width - end.Width), start.Height + RowSpacing, end.Width, end.Height));
        }
        return finalSize;
    }

    private bool Fits(double width) =>
        Children.Count < 2 || double.IsInfinity(width) || Children[0].DesiredSize.Width + Children[^1].DesiredSize.Width <= width;
}
