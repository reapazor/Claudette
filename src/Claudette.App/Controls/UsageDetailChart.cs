using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Claudette.App.Controls;

/// <summary>What the time axis of a <see cref="UsageDetailChart"/> is labeled with.</summary>
public enum ChartAxis
{
    /// <summary>The start, now and the end, as clock times: the session chart.</summary>
    Times,

    /// <summary>Ticks at midnight and each day's name: the weekly chart.</summary>
    Days,
}

/// <summary>Another line on a usage chart, such as a model-specific weekly limit.</summary>
public sealed record ChartSeries(string Label, IReadOnlyList<ChartPoint> Points);

/// <summary>
/// A chart for the detailed usage header (DESIGN.md §6, "Detailed header"): usage over a window with faint bands above
/// the warning and critical thresholds, dashed lines at them, the dotted projection, a "now" marker, a marked point
/// where the projection crosses a level, thinner extra lines (model-specific limits) and a labeled time axis.
/// Drawn like the sparkline (<see cref="UsageChart"/>), with the math in <see cref="ChartGeometry"/>. Colors come from
/// the app's tokens, bound in XAML, so it follows the theme.
/// </summary>
public sealed class UsageDetailChart : Control
{
    /// <summary>Room above the plot for the current point's marker.</summary>
    public const double TopPadding = 6;

    /// <summary>Room below the plot for the time labels.</summary>
    public const double AxisHeight = 15;

    /// <summary>Room right of the plot for the threshold labels.</summary>
    public const double LevelGutter = 30;

    private const double LabelSize = 10;
    private const double BandOpacity = 0.09;

    public static readonly StyledProperty<IReadOnlyList<ChartPoint>?> PointsProperty =
        AvaloniaProperty.Register<UsageDetailChart, IReadOnlyList<ChartPoint>?>(nameof(Points));

    public static readonly StyledProperty<IReadOnlyList<ChartPoint>?> ProjectionProperty =
        AvaloniaProperty.Register<UsageDetailChart, IReadOnlyList<ChartPoint>?>(nameof(Projection));

    public static readonly StyledProperty<IReadOnlyList<ChartSeries>?> SeriesProperty =
        AvaloniaProperty.Register<UsageDetailChart, IReadOnlyList<ChartSeries>?>(nameof(Series));

    public static readonly StyledProperty<DateTimeOffset?> RangeStartProperty =
        AvaloniaProperty.Register<UsageDetailChart, DateTimeOffset?>(nameof(RangeStart));

    public static readonly StyledProperty<DateTimeOffset?> RangeEndProperty =
        AvaloniaProperty.Register<UsageDetailChart, DateTimeOffset?>(nameof(RangeEnd));

    public static readonly StyledProperty<DateTimeOffset?> NowProperty =
        AvaloniaProperty.Register<UsageDetailChart, DateTimeOffset?>(nameof(Now));

    public static readonly StyledProperty<ChartPoint?> MarkerProperty =
        AvaloniaProperty.Register<UsageDetailChart, ChartPoint?>(nameof(Marker));

    public static readonly StyledProperty<double?> WarnLevelProperty =
        AvaloniaProperty.Register<UsageDetailChart, double?>(nameof(WarnLevel));

    public static readonly StyledProperty<double?> CriticalLevelProperty =
        AvaloniaProperty.Register<UsageDetailChart, double?>(nameof(CriticalLevel));

    public static readonly StyledProperty<ChartAxis> AxisProperty =
        AvaloniaProperty.Register<UsageDetailChart, ChartAxis>(nameof(Axis));

    /// <summary>Clock times on a <see cref="ChartAxis.Times"/> axis.</summary>
    public static readonly StyledProperty<string> TimeFormatProperty =
        AvaloniaProperty.Register<UsageDetailChart, string>(nameof(TimeFormat), "t");

    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<UsageDetailChart, IBrush?>(nameof(LineBrush), Brushes.SteelBlue);

    public static readonly StyledProperty<IBrush?> ProjectionBrushProperty =
        AvaloniaProperty.Register<UsageDetailChart, IBrush?>(nameof(ProjectionBrush), Brushes.Gray);

    public static readonly StyledProperty<IBrush?> WarnBrushProperty =
        AvaloniaProperty.Register<UsageDetailChart, IBrush?>(nameof(WarnBrush), Brushes.Goldenrod);

    public static readonly StyledProperty<IBrush?> CriticalBrushProperty =
        AvaloniaProperty.Register<UsageDetailChart, IBrush?>(nameof(CriticalBrush), Brushes.IndianRed);

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<UsageDetailChart, IBrush?>(nameof(GridBrush), Brushes.LightGray);

    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<UsageDetailChart, IBrush?>(nameof(LabelBrush), Brushes.Gray);

    /// <summary>The chart's background, for the ring that keeps a marker clear of the lines under it.</summary>
    public static readonly StyledProperty<IBrush?> SurfaceBrushProperty =
        AvaloniaProperty.Register<UsageDetailChart, IBrush?>(nameof(SurfaceBrush), Brushes.White);

    /// <summary>The first of <see cref="Series"/>.</summary>
    public static readonly StyledProperty<IBrush?> SeriesBrushProperty =
        AvaloniaProperty.Register<UsageDetailChart, IBrush?>(nameof(SeriesBrush), Brushes.SeaGreen);

    /// <summary>The second of <see cref="Series"/>. Further series aren't drawn: two hues are all that stay apart.</summary>
    public static readonly StyledProperty<IBrush?> Series2BrushProperty =
        AvaloniaProperty.Register<UsageDetailChart, IBrush?>(nameof(Series2Brush), Brushes.SlateBlue);

    static UsageDetailChart()
    {
        AffectsRender<UsageDetailChart>(
            PointsProperty, ProjectionProperty, SeriesProperty, RangeStartProperty, RangeEndProperty, NowProperty, MarkerProperty,
            WarnLevelProperty, CriticalLevelProperty, AxisProperty, TimeFormatProperty, LineBrushProperty, ProjectionBrushProperty,
            WarnBrushProperty, CriticalBrushProperty, GridBrushProperty, LabelBrushProperty, SurfaceBrushProperty, SeriesBrushProperty,
            Series2BrushProperty);
    }

    public IReadOnlyList<ChartPoint>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }

    public IReadOnlyList<ChartPoint>? Projection { get => GetValue(ProjectionProperty); set => SetValue(ProjectionProperty, value); }

    public IReadOnlyList<ChartSeries>? Series { get => GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }

    public DateTimeOffset? RangeStart { get => GetValue(RangeStartProperty); set => SetValue(RangeStartProperty, value); }

    public DateTimeOffset? RangeEnd { get => GetValue(RangeEndProperty); set => SetValue(RangeEndProperty, value); }

    public DateTimeOffset? Now { get => GetValue(NowProperty); set => SetValue(NowProperty, value); }

    public ChartPoint? Marker { get => GetValue(MarkerProperty); set => SetValue(MarkerProperty, value); }

    public double? WarnLevel { get => GetValue(WarnLevelProperty); set => SetValue(WarnLevelProperty, value); }

    public double? CriticalLevel { get => GetValue(CriticalLevelProperty); set => SetValue(CriticalLevelProperty, value); }

    public ChartAxis Axis { get => GetValue(AxisProperty); set => SetValue(AxisProperty, value); }

    public string TimeFormat { get => GetValue(TimeFormatProperty); set => SetValue(TimeFormatProperty, value); }

    public IBrush? LineBrush { get => GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }

    public IBrush? ProjectionBrush { get => GetValue(ProjectionBrushProperty); set => SetValue(ProjectionBrushProperty, value); }

    public IBrush? WarnBrush { get => GetValue(WarnBrushProperty); set => SetValue(WarnBrushProperty, value); }

    public IBrush? CriticalBrush { get => GetValue(CriticalBrushProperty); set => SetValue(CriticalBrushProperty, value); }

    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    public IBrush? LabelBrush { get => GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    public IBrush? SurfaceBrush { get => GetValue(SurfaceBrushProperty); set => SetValue(SurfaceBrushProperty, value); }

    public IBrush? SeriesBrush { get => GetValue(SeriesBrushProperty); set => SetValue(SeriesBrushProperty, value); }

    public IBrush? Series2Brush { get => GetValue(Series2BrushProperty); set => SetValue(Series2BrushProperty, value); }

    /// <summary>The plot inside a chart of <paramref name="size"/>, leaving room for the markers and labels around it.</summary>
    public static Rect PlotArea(Size size) =>
        new(1, TopPadding, Math.Max(0, size.Width - LevelGutter - 1), Math.Max(0, size.Height - TopPadding - AxisHeight));

    /// <summary>The geometry the chart draws with at its current size and values.</summary>
    public ChartGeometry Geometry()
    {
        var series = Series ?? [];
        var all = (Points ?? []).Concat(Projection ?? []).Concat(series.SelectMany(s => s.Points));
        return ChartGeometry.Create(PlotArea(Bounds.Size), all, RangeStart, RangeEnd);
    }

    public override void Render(DrawingContext context)
    {
        var geometry = Geometry();
        var plot = geometry.Plot;
        if (plot.Width < 4 || plot.Height < 4)
        {
            return;
        }

        DrawBands(context, geometry);
        if (GridBrush is { } grid)
        {
            // Recessive hairlines: the baseline and the limit.
            var pen = new Pen(grid, 1);
            context.DrawLine(pen, new Point(plot.Left, Snap(plot.Bottom)), new Point(plot.Right, Snap(plot.Bottom)));
            context.DrawLine(pen, new Point(plot.Left, Snap(plot.Top)), new Point(plot.Right, Snap(plot.Top)));
            if (Axis == ChartAxis.Days)
            {
                foreach (var midnight in geometry.DayStarts(TimeZoneInfo.Local))
                {
                    var x = Snap(geometry.X(midnight));
                    context.DrawLine(pen, new Point(x, plot.Top), new Point(x, plot.Bottom + 3));
                }
            }
        }
        DrawLevels(context, geometry);

        if (Now is { } now && geometry.Contains(now) && LabelBrush is { } nowBrush)
        {
            var x = Snap(geometry.X(now));
            using (context.PushOpacity(0.5))
            {
                context.DrawLine(new Pen(nowBrush, 1), new Point(x, plot.Top), new Point(x, plot.Bottom));
            }
        }

        var series = Series ?? [];
        for (var i = 0; i < series.Count && i < 2; i++)
        {
            if ((i == 0 ? SeriesBrush : Series2Brush) is { } brush)
            {
                DrawPolyline(context, geometry.Line(series[i].Points), new Pen(brush, 1.25, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round));
            }
        }

        var points = Points ?? [];
        if (LineBrush is { } line)
        {
            DrawPolyline(context, geometry.Line(points), new Pen(line, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round));
        }
        if (Projection is { Count: > 1 } projection && ProjectionBrush is { } dotted)
        {
            DrawPolyline(context, geometry.Line(projection), new Pen(dotted, 1.5, new DashStyle([2, 2.5], 0)));
        }

        // The current value, and where the projection crosses a level: markers with a ring of the surface around them.
        if (points.Count > 0 && LineBrush is { } dot)
        {
            var current = geometry.At(points.MaxBy(p => p.Time));
            context.DrawEllipse(SurfaceBrush, null, current, 5.5, 5.5);
            context.DrawEllipse(dot, null, current, 3.5, 3.5);
        }
        if (Marker is { } marker && geometry.Contains(marker.Time) && CriticalBrush is { } critical)
        {
            var at = geometry.At(marker);
            context.DrawEllipse(SurfaceBrush, new Pen(critical, 2), at, 4, 4);
        }

        DrawAxis(context, geometry);
    }

    /// <summary>Faint bands from the warning threshold to the critical one, and from there to the limit.</summary>
    private void DrawBands(DrawingContext context, ChartGeometry geometry)
    {
        var plot = geometry.Plot;
        void Band(double from, double to, IBrush? brush)
        {
            if (brush is null || to <= from)
            {
                return;
            }
            var top = geometry.Y(to);
            var bottom = geometry.Y(from);
            using (context.PushOpacity(BandOpacity))
            {
                context.FillRectangle(brush, new Rect(plot.Left, top, plot.Width, bottom - top));
            }
        }
        var critical = CriticalLevel is { } c && c < geometry.Maximum ? c : (double?)null;
        if (WarnLevel is { } warn && warn < geometry.Maximum)
        {
            Band(warn, critical ?? geometry.Maximum, WarnBrush);
        }
        if (critical is { } level)
        {
            Band(level, geometry.Maximum, CriticalBrush);
        }
    }

    /// <summary>Dashed lines at the thresholds, labeled to the right of the plot, as is the limit.</summary>
    private void DrawLevels(DrawingContext context, ChartGeometry geometry)
    {
        var plot = geometry.Plot;
        var labels = new List<(double Value, IBrush? Brush)>();
        if (CriticalLevel is { } critical && critical < geometry.Maximum)
        {
            labels.Add((critical, CriticalBrush));
        }
        if (WarnLevel is { } warn && warn < geometry.Maximum)
        {
            labels.Add((warn, WarnBrush));
        }
        foreach (var (value, brush) in labels)
        {
            if (brush is not null)
            {
                var y = Snap(geometry.Y(value));
                context.DrawLine(new Pen(brush, 1, new DashStyle([4, 3], 0)), new Point(plot.Left, y), new Point(plot.Right, y));
            }
        }

        labels.Add((geometry.Maximum, null));
        var texts = labels.Select(l => Text($"{l.Value:0}%")).ToArray();
        var places = ChartGeometry.PlaceLabels([.. labels.Select((l, i) => (geometry.Y(l.Value), texts[i].Height))], 0, Bounds.Height, gap: 0);
        for (var i = 0; i < texts.Length; i++)
        {
            if (places[i] is { } top)
            {
                context.DrawText(texts[i], new Point(plot.Right + 4, top));
            }
        }
    }

    /// <summary>The time labels under the plot: the start, the end and now, or each day's name.</summary>
    private void DrawAxis(DrawingContext context, ChartGeometry geometry)
    {
        var plot = geometry.Plot;
        var labels = new List<(FormattedText Text, double Center)>();
        if (Axis == ChartAxis.Times)
        {
            labels.Add((Text(Format(geometry.Start)), plot.Left));
            labels.Add((Text(Format(geometry.End)), plot.Right));
            if (Now is { } now && geometry.Contains(now))
            {
                labels.Add((Text(Format(now), FontWeight.SemiBold), geometry.X(now)));
            }
        }
        else
        {
            var bounds = new List<DateTimeOffset> { geometry.Start };
            bounds.AddRange(geometry.DayStarts(TimeZoneInfo.Local));
            bounds.Add(geometry.End);
            for (var i = 0; i + 1 < bounds.Count; i++)
            {
                var (left, right) = (geometry.X(bounds[i]), geometry.X(bounds[i + 1]));
                var text = Text(bounds[i].ToLocalTime().ToString("ddd", CultureInfo.CurrentCulture));
                // A day with too little of it on the chart goes without its name.
                if (right - left >= text.Width + 2)
                {
                    labels.Add((text, (left + right) / 2));
                }
            }
        }
        var places = ChartGeometry.PlaceLabels([.. labels.Select(l => (l.Center, l.Text.Width))], 0, plot.Right);
        for (var i = 0; i < labels.Count; i++)
        {
            if (places[i] is { } left)
            {
                context.DrawText(labels[i].Text, new Point(left, plot.Bottom + 2));
            }
        }
    }

    private string Format(DateTimeOffset time) => time.ToLocalTime().ToString(TimeFormat, CultureInfo.CurrentCulture);

    private FormattedText Text(string text, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, weight), LabelSize, LabelBrush);

    private static void DrawPolyline(DrawingContext context, IReadOnlyList<Point> points, Pen pen)
    {
        if (points.Count < 2)
        {
            return;
        }
        var geometry = new StreamGeometry();
        using (var figure = geometry.Open())
        {
            figure.BeginFigure(points[0], isFilled: false);
            for (var i = 1; i < points.Count; i++)
            {
                figure.LineTo(points[i]);
            }
            figure.EndFigure(isClosed: false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    /// <summary>A hairline on a pixel's center, so it stays one pixel wide.</summary>
    private static double Snap(double value) => Math.Floor(value) + 0.5;
}
