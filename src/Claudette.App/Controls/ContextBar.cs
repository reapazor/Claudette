using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Claudette.App.ViewModels;

namespace Claudette.App.Controls;

/// <summary>
/// The context window as a bar (DESIGN.md §6, "Per-tab context"): each part as wide as its share, with a gap of the
/// surface between parts and rounded ends. A part too small to see keeps a sliver. Each part's color is a resource
/// key, looked up as the bar draws, so it follows the theme and the style; hovering a part names it.
/// </summary>
public sealed class ContextBar : Control
{
    /// <summary>The surface showing between two parts.</summary>
    public const double Gap = 2;

    /// <summary>The narrowest a part is drawn, so a part with a few hundred tokens is still there to hover.</summary>
    public const double MinSegmentWidth = 3;

    public static readonly StyledProperty<IReadOnlyList<ContextSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<ContextBar, IReadOnlyList<ContextSegment>?>(nameof(Segments));

    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<ContextBar, double>(nameof(CornerRadius), 4);

    static ContextBar()
    {
        AffectsRender<ContextBar>(SegmentsProperty, CornerRadiusProperty);
    }

    public ContextBar()
    {
        // The colors are looked up as the bar draws: draw again when they change.
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        ResourcesChanged += (_, _) => InvalidateVisual();
    }

    public IReadOnlyList<ContextSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>Where each part with tokens goes across <paramref name="width"/>: its index, left edge and width.</summary>
    public static IReadOnlyList<(int Index, double X, double Width)> Layout(IReadOnlyList<long> tokens, double width)
    {
        var shown = Enumerable.Range(0, tokens.Count).Where(i => tokens[i] > 0).ToList();
        var available = width - Gap * Math.Max(0, shown.Count - 1);
        if (shown.Count == 0 || available <= 0)
        {
            return [];
        }
        double total = shown.Sum(i => tokens[i]);
        var widths = shown.ToDictionary(i => i, i => tokens[i] / total * available);
        // Slivers take their minimum from the others, in proportion to them.
        var small = shown.Where(i => widths[i] < MinSegmentWidth).ToList();
        var large = shown.Except(small).ToList();
        if (small.Count > 0 && large.Count > 0 && available > MinSegmentWidth * shown.Count)
        {
            var rest = available - MinSegmentWidth * small.Count;
            var largeTotal = large.Sum(i => widths[i]);
            foreach (var i in small)
            {
                widths[i] = MinSegmentWidth;
            }
            foreach (var i in large)
            {
                widths[i] = widths[i] / largeTotal * rest;
            }
        }
        var layout = new List<(int, double, double)>();
        var x = 0.0;
        foreach (var i in shown)
        {
            layout.Add((i, x, widths[i]));
            x += widths[i] + Gap;
        }
        return layout;
    }

    public override void Render(DrawingContext context)
    {
        if (Segments is not { Count: > 0 } segments || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }
        var bounds = new Rect(Bounds.Size);
        using (context.PushClip(new RoundedRect(bounds, Math.Min(CornerRadius, bounds.Height / 2))))
        {
            foreach (var (index, x, width) in Layout([.. segments.Select(s => s.Tokens)], bounds.Width))
            {
                if (this.TryFindResource(segments[index].Brush, ActualThemeVariant, out var value) && value is IBrush brush)
                {
                    context.FillRectangle(brush, new Rect(x, 0, width, bounds.Height));
                }
            }
        }
    }

    /// <summary>Hovering a part names it, with its tokens and share.</summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        ToolTip.SetTip(this, SegmentAt(e.GetPosition(this).X)?.Tip);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ToolTip.SetTip(this, null);
    }

    /// <summary>The part at <paramref name="x"/>, or the nearest one when it's in a gap.</summary>
    public ContextSegment? SegmentAt(double x)
    {
        if (Segments is not { Count: > 0 } segments)
        {
            return null;
        }
        var layout = Layout([.. segments.Select(s => s.Tokens)], Bounds.Width);
        foreach (var (index, left, width) in layout)
        {
            if (x < left + width + Gap / 2)
            {
                return segments[index];
            }
        }
        return layout.Count > 0 ? segments[layout[^1].Index] : null;
    }
}
