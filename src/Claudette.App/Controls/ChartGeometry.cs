using Avalonia;

namespace Claudette.App.Controls;

/// <summary>
/// Maps usage values to points on a chart (DESIGN.md §6): time runs left to right from <see cref="Start"/> to
/// <see cref="End"/>, and 0 to <see cref="Maximum"/> runs bottom to top. Anything outside is clamped to the plot's edge.
/// Kept apart from drawing so the math can be tested.
/// </summary>
public readonly record struct ChartGeometry(Rect Plot, DateTimeOffset Start, DateTimeOffset End, double Maximum)
{
    /// <summary>
    /// The geometry for <paramref name="points"/>: the given range, or else the points' own, and at least a minute
    /// long. A maximum of zero or less means 100.
    /// </summary>
    public static ChartGeometry Create(Rect plot, IEnumerable<ChartPoint> points, DateTimeOffset? start, DateTimeOffset? end, double maximum = 100)
    {
        DateTimeOffset? first = null, last = null;
        if (start is null || end is null)
        {
            foreach (var point in points)
            {
                first = first is { } f && f <= point.Time ? f : point.Time;
                last = last is { } l && l >= point.Time ? l : point.Time;
            }
        }
        var from = start ?? first ?? DateTimeOffset.MinValue;
        var to = end ?? last ?? DateTimeOffset.MinValue;
        if (to <= from)
        {
            to = from + TimeSpan.FromMinutes(1);
        }
        return new ChartGeometry(plot, from, to, maximum <= 0 || !double.IsFinite(maximum) ? 100 : maximum);
    }

    /// <summary>Where <paramref name="time"/> falls across the plot, clamped to its left and right edges.</summary>
    public double X(DateTimeOffset time) =>
        Math.Clamp(Plot.Left + (Plot.Width * ((time - Start).TotalSeconds / (End - Start).TotalSeconds)), Plot.Left, Plot.Right);

    /// <summary>Where <paramref name="value"/> falls up the plot, clamped to 0 and the maximum. A missing value sits on the baseline.</summary>
    public double Y(double value) =>
        double.IsFinite(value) ? Plot.Bottom - (Plot.Height * Math.Clamp(value / Maximum, 0, 1)) : Plot.Bottom;

    public Point At(ChartPoint point) => new(X(point.Time), Y(point.Value));

    public bool Contains(DateTimeOffset time) => time >= Start && time <= End;

    /// <summary>The points in time order, mapped onto the plot.</summary>
    public IReadOnlyList<Point> Line(IEnumerable<ChartPoint> points) => [.. points.OrderBy(p => p.Time).Select(At)];

    /// <summary>
    /// The midnights in <paramref name="zone"/> strictly between <see cref="Start"/> and <see cref="End"/>, for the
    /// weekly chart's day ticks.
    /// </summary>
    public IReadOnlyList<DateTimeOffset> DayStarts(TimeZoneInfo zone)
    {
        var days = new List<DateTimeOffset>();
        var local = TimeZoneInfo.ConvertTime(Start, zone);
        var day = new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Unspecified).AddDays(1);
        // A chart spans days, not years: stop well before a mistaken range could take long.
        for (var i = 0; i < 400; i++, day = day.AddDays(1))
        {
            var midnight = new DateTimeOffset(day, zone.GetUtcOffset(day));
            if (midnight >= End)
            {
                break;
            }
            if (midnight > Start)
            {
                days.Add(midnight);
            }
        }
        return days;
    }

    /// <summary>
    /// Where each label along an axis goes: the left edge of its box, or null when it's left out. Each label is centered
    /// on its position but kept between <paramref name="min"/> and <paramref name="max"/>, so the first and last read
    /// from the ends. Labels are placed in the order given, and one that would overlap a label already placed (with
    /// <paramref name="gap"/> between them) is left out, so put the ones that matter most first.
    /// </summary>
    public static IReadOnlyList<double?> PlaceLabels(IReadOnlyList<(double Center, double Width)> labels, double min, double max, double gap = 6)
    {
        var placed = new List<(double Left, double Right)>();
        var result = new double?[labels.Count];
        for (var i = 0; i < labels.Count; i++)
        {
            var (center, width) = labels[i];
            if (width > max - min || !double.IsFinite(center))
            {
                continue;
            }
            var left = Math.Clamp(center - (width / 2), min, max - width);
            var right = left + width;
            if (placed.Any(p => left < p.Right + gap && right > p.Left - gap))
            {
                continue;
            }
            placed.Add((left, right));
            result[i] = left;
        }
        return result;
    }
}
