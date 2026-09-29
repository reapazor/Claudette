using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Claudette.App.Controls;

/// <summary>One point on a usage chart: a time and a value (usually percent used).</summary>
public readonly record struct ChartPoint(DateTimeOffset Time, double Value);

/// <summary>
/// A small line chart for plan usage (DESIGN.md §6, "Burn trendline"): the usage line, an optional dotted projection,
/// and dashed warning and limit lines. Compact, with no axes, for the header sparkline; with axes in the Usage panel.
/// </summary>
public sealed class UsageChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<ChartPoint>?> PointsProperty =
        AvaloniaProperty.Register<UsageChart, IReadOnlyList<ChartPoint>?>(nameof(Points));

    public static readonly StyledProperty<IReadOnlyList<ChartPoint>?> ProjectionProperty =
        AvaloniaProperty.Register<UsageChart, IReadOnlyList<ChartPoint>?>(nameof(Projection));

    public static readonly StyledProperty<DateTimeOffset?> RangeStartProperty =
        AvaloniaProperty.Register<UsageChart, DateTimeOffset?>(nameof(RangeStart));

    public static readonly StyledProperty<DateTimeOffset?> RangeEndProperty =
        AvaloniaProperty.Register<UsageChart, DateTimeOffset?>(nameof(RangeEnd));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<UsageChart, double>(nameof(Maximum), 100);

    public static readonly StyledProperty<double?> WarnLevelProperty =
        AvaloniaProperty.Register<UsageChart, double?>(nameof(WarnLevel));

    public static readonly StyledProperty<double?> CriticalLevelProperty =
        AvaloniaProperty.Register<UsageChart, double?>(nameof(CriticalLevel));

    public static readonly StyledProperty<bool> ShowAxesProperty =
        AvaloniaProperty.Register<UsageChart, bool>(nameof(ShowAxes));

    /// <summary>Time labels on the x axis: "HH:mm" for a session, "ddd" for a week.</summary>
    public static readonly StyledProperty<string> TimeFormatProperty =
        AvaloniaProperty.Register<UsageChart, string>(nameof(TimeFormat), "HH:mm");

    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<UsageChart, IBrush?>(nameof(LineBrush), Brushes.SteelBlue);

    public static readonly StyledProperty<IBrush?> ProjectionBrushProperty =
        AvaloniaProperty.Register<UsageChart, IBrush?>(nameof(ProjectionBrush), Brushes.Gray);

    public static readonly StyledProperty<IBrush?> WarnBrushProperty =
        AvaloniaProperty.Register<UsageChart, IBrush?>(nameof(WarnBrush), Brushes.Goldenrod);

    public static readonly StyledProperty<IBrush?> CriticalBrushProperty =
        AvaloniaProperty.Register<UsageChart, IBrush?>(nameof(CriticalBrush), Brushes.IndianRed);

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<UsageChart, IBrush?>(nameof(GridBrush), Brushes.LightGray);

    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<UsageChart, IBrush?>(nameof(LabelBrush), Brushes.Gray);

    static UsageChart()
    {
        AffectsRender<UsageChart>(
            PointsProperty, ProjectionProperty, RangeStartProperty, RangeEndProperty, MaximumProperty, WarnLevelProperty,
            CriticalLevelProperty, ShowAxesProperty, TimeFormatProperty, LineBrushProperty, ProjectionBrushProperty,
            WarnBrushProperty, CriticalBrushProperty, GridBrushProperty, LabelBrushProperty);
    }

    public IReadOnlyList<ChartPoint>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }

    public IReadOnlyList<ChartPoint>? Projection { get => GetValue(ProjectionProperty); set => SetValue(ProjectionProperty, value); }

    public DateTimeOffset? RangeStart { get => GetValue(RangeStartProperty); set => SetValue(RangeStartProperty, value); }

    public DateTimeOffset? RangeEnd { get => GetValue(RangeEndProperty); set => SetValue(RangeEndProperty, value); }

    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public double? WarnLevel { get => GetValue(WarnLevelProperty); set => SetValue(WarnLevelProperty, value); }

    public double? CriticalLevel { get => GetValue(CriticalLevelProperty); set => SetValue(CriticalLevelProperty, value); }

    public bool ShowAxes { get => GetValue(ShowAxesProperty); set => SetValue(ShowAxesProperty, value); }

    public string TimeFormat { get => GetValue(TimeFormatProperty); set => SetValue(TimeFormatProperty, value); }

    public IBrush? LineBrush { get => GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }

    public IBrush? ProjectionBrush { get => GetValue(ProjectionBrushProperty); set => SetValue(ProjectionBrushProperty, value); }

    public IBrush? WarnBrush { get => GetValue(WarnBrushProperty); set => SetValue(WarnBrushProperty, value); }

    public IBrush? CriticalBrush { get => GetValue(CriticalBrushProperty); set => SetValue(CriticalBrushProperty, value); }

    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    public IBrush? LabelBrush { get => GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    public override void Render(DrawingContext context)
    {
        var points = Points ?? [];
        var projection = Projection ?? [];

        const double labelWidth = 34, labelHeight = 16;
        var plot = ShowAxes
            ? new Rect(labelWidth, 4, Math.Max(0, Bounds.Width - labelWidth - 4), Math.Max(0, Bounds.Height - labelHeight - 8))
            : new Rect(1, 1, Math.Max(0, Bounds.Width - 2), Math.Max(0, Bounds.Height - 2));
        if (plot.Width <= 0 || plot.Height <= 0)
        {
            return;
        }
        var geometry = ChartGeometry.Create(plot, points.Concat(projection), RangeStart, RangeEnd, Maximum);
        var (start, end, maximum) = (geometry.Start, geometry.End, geometry.Maximum);
        Point At(ChartPoint p) => geometry.At(p);
        double LevelY(double level) => geometry.Y(level);

        if (ShowAxes && GridBrush is { } grid)
        {
            var gridPen = new Pen(grid, 1);
            foreach (var level in new[] { 0.0, 0.5, 1.0 })
            {
                var y = plot.Bottom - plot.Height * level;
                context.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                DrawLabel(context, $"{maximum * level:0}%", new Point(0, y - 7), labelWidth - 4, TextAlignment.Right);
            }
            for (var i = 0; i <= 4; i++)
            {
                var time = start + (end - start) * (i / 4.0);
                var x = plot.Left + plot.Width * i / 4.0;
                DrawLabel(context, time.ToLocalTime().ToString(TimeFormat, CultureInfo.CurrentCulture), new Point(x - 30, plot.Bottom + 3), 60, TextAlignment.Center);
            }
        }

        DrawLevel(context, WarnLevel, WarnBrush);
        DrawLevel(context, CriticalLevel, CriticalBrush);

        if (points.Count > 0 && LineBrush is { } line)
        {
            var pen = new Pen(line, ShowAxes ? 2 : 1.5, lineJoin: PenLineJoin.Round);
            var ordered = points.OrderBy(p => p.Time).ToArray();
            if (ordered.Length == 1)
            {
                context.DrawEllipse(line, null, At(ordered[0]), 2, 2);
            }
            for (var i = 1; i < ordered.Length; i++)
            {
                context.DrawLine(pen, At(ordered[i - 1]), At(ordered[i]));
            }
        }

        if (projection.Count > 1 && ProjectionBrush is { } dotted)
        {
            var pen = new Pen(dotted, 1.5, new DashStyle([2, 3], 0));
            var ordered = projection.OrderBy(p => p.Time).ToArray();
            for (var i = 1; i < ordered.Length; i++)
            {
                context.DrawLine(pen, At(ordered[i - 1]), At(ordered[i]));
            }
        }

        void DrawLevel(DrawingContext dc, double? level, IBrush? brush)
        {
            if (level is { } value && brush is not null && value < maximum)
            {
                var y = LevelY(value);
                dc.DrawLine(new Pen(brush, 1, new DashStyle([4, 4], 0)) { }, new Point(plot.Left, y), new Point(plot.Right, y));
            }
        }
    }

    private void DrawLabel(DrawingContext context, string text, Point origin, double width, TextAlignment alignment)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, LabelBrush)
        {
            MaxTextWidth = width,
            TextAlignment = alignment,
        };
        context.DrawText(formatted, origin);
    }
}
