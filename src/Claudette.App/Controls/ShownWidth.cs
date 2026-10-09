using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Claudette.App.Controls;

/// <summary>
/// Keeps its content within the width a sideways-scrolling <see cref="ScrollViewer"/> shows, from where the content
/// starts to <see cref="Gap"/> short of the viewer's right edge (DESIGN.md §3, "Side panel"). A tree's row wraps its
/// text there and lines its numbers up at that edge however deep it's nested, and only a row nested so deep that it
/// would get less than <see cref="Minimum"/> scrolls sideways.
/// </summary>
public sealed class ShownWidth : Decorator
{
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<ShownWidth, double>(nameof(Minimum), 120);

    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<ShownWidth, double>(nameof(Gap), 16);

    private double _limit = double.PositiveInfinity;

    public ShownWidth() => EffectiveViewportChanged += OnEffectiveViewportChanged;

    /// <summary>The least width the content gets, however far right it starts.</summary>
    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <summary>
    /// The room it leaves at the viewer's right edge, so the content stays clear of the vertical scroll bar, which the
    /// viewer draws over it.
    /// </summary>
    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        // The viewport in this control's own coordinates: its right edge is how far the shown area reaches from here,
        // plus however far it's scrolled sideways, which doesn't change what fits.
        var viewport = e.EffectiveViewport;
        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            return;
        }
        var scrolled = this.FindAncestorOfType<ScrollViewer>()?.Offset.X ?? 0;
        var limit = Math.Max(Minimum, viewport.Right - scrolled - Gap);
        if (Math.Abs(limit - _limit) > 0.5)
        {
            _limit = limit;
            InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child)
        {
            return default;
        }
        child.Measure(availableSize.WithWidth(Math.Min(availableSize.Width, _limit)));
        return child.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // A row stretched wider by a row nested deeper keeps this one within the shown width.
        Child?.Arrange(new Rect(0, 0, Math.Min(finalSize.Width, _limit), finalSize.Height));
        return finalSize;
    }
}
