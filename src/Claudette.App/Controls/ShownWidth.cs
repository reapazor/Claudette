using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Claudette.App.Controls;

/// <summary>
/// Keeps its content within the width a sideways-scrolling <see cref="ScrollViewer"/> shows, from where the content
/// starts to the viewer's right edge (DESIGN.md §3, "Side panel"). A row's name, numbers and status stay in view and
/// line up at that edge, and text in it wraps or trims there, while the long text beside it (a path, a command line)
/// is shown whole and scrolls. Content far enough to the right, deep in a tree, still gets <see cref="Minimum"/>.
/// </summary>
public sealed class ShownWidth : Decorator
{
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<ShownWidth, double>(nameof(Minimum), 120);

    private double _limit = double.PositiveInfinity;

    public ShownWidth() => EffectiveViewportChanged += OnEffectiveViewportChanged;

    /// <summary>The least width the content gets, however far right it starts.</summary>
    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
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
        var limit = Math.Max(Minimum, viewport.Right - scrolled);
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
        // A row stretched wider by the long text beside it keeps this part within the shown width.
        Child?.Arrange(new Rect(0, 0, Math.Min(finalSize.Width, _limit), finalSize.Height));
        return finalSize;
    }
}
