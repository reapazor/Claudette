using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Claudette.App.Controls;

/// <summary>
/// A line of code in a <see cref="LineScroller"/>: laid out at its full width, however narrow the space it has, and moved
/// sideways as the scroller scrolls. What doesn't fit is cut off.
/// </summary>
public sealed class ScrollingLine : Decorator
{
    private LineScroller? _scroller;

    static ScrollingLine()
    {
        ClipToBoundsProperty.OverrideDefaultValue<ScrollingLine>(true);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scroller = this.FindAncestorOfType<LineScroller>();
        _scroller?.Add(this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _scroller?.Remove(this);
        _scroller = null;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is null)
        {
            return default;
        }
        Child.Measure(availableSize.WithWidth(double.PositiveInfinity));
        return new Size(Math.Min(Child.DesiredSize.Width, availableSize.Width), Child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is not null)
        {
            var width = Child.DesiredSize.Width;
            var offset = _scroller?.Arranged(width, finalSize.Width) ?? 0;
            Child.Arrange(new Rect(-offset, 0, Math.Max(width, finalSize.Width), finalSize.Height));
        }
        return finalSize;
    }
}
