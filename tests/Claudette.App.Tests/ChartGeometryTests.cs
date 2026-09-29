using Avalonia;
using Claudette.App.Controls;
using Claudette.App.ViewModels;

namespace Claudette.App.Tests;

/// <summary>The math behind the usage charts, which are drawn by hand (DESIGN.md §6, "Detailed header").</summary>
public class ChartGeometryTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);
    private static readonly Rect Plot = new(10, 5, 200, 100);

    [Fact]
    public void Values_map_left_to_right_and_bottom_to_top()
    {
        var geometry = ChartGeometry.Create(Plot, [], Start, Start.AddHours(5));

        Assert.Equal(new Point(10, 105), geometry.At(new ChartPoint(Start, 0)));
        Assert.Equal(new Point(210, 5), geometry.At(new ChartPoint(Start.AddHours(5), 100)));
        Assert.Equal(new Point(60, 55), geometry.At(new ChartPoint(Start.AddHours(1.25), 50)));
        Assert.Equal(15, geometry.Y(90));
    }

    [Fact]
    public void Values_outside_the_chart_are_clamped_to_its_edges()
    {
        var geometry = ChartGeometry.Create(Plot, [], Start, Start.AddHours(5));

        Assert.Equal(new Point(10, 105), geometry.At(new ChartPoint(Start.AddHours(-2), -20)));
        Assert.Equal(new Point(210, 5), geometry.At(new ChartPoint(Start.AddHours(9), 180)));
        // A missing value sits on the baseline rather than off the chart.
        Assert.Equal(105, geometry.Y(double.NaN));
        Assert.False(geometry.Contains(Start.AddMinutes(-1)));
        Assert.True(geometry.Contains(Start.AddHours(5)));
    }

    [Fact]
    public void Without_a_range_the_points_set_it_and_a_maximum_of_zero_means_100()
    {
        var geometry = ChartGeometry.Create(Plot, [new ChartPoint(Start.AddHours(2), 10), new ChartPoint(Start, 5)], null, null, maximum: 0);

        Assert.Equal(Start, geometry.Start);
        Assert.Equal(Start.AddHours(2), geometry.End);
        Assert.Equal(100, geometry.Maximum);
        Assert.Equal([new Point(10, 100), new Point(210, 95)], geometry.Line([new ChartPoint(Start.AddHours(2), 10), new ChartPoint(Start, 5)]));
    }

    [Fact]
    public void An_empty_series_draws_nothing_and_still_has_a_range()
    {
        var empty = ChartGeometry.Create(Plot, [], null, null);
        var single = ChartGeometry.Create(Plot, [new ChartPoint(Start, 40)], null, null);

        Assert.Empty(empty.Line([]));
        Assert.True(empty.End > empty.Start);
        Assert.Equal(TimeSpan.FromMinutes(1), single.End - single.Start);
        Assert.Equal(new Point(10, 65), Assert.Single(single.Line([new ChartPoint(Start, 40)])));
        // A range that ends before it starts is widened rather than dividing by zero.
        Assert.Equal(Start.AddMinutes(1), ChartGeometry.Create(Plot, [], Start, Start.AddHours(-1)).End);
    }

    [Fact]
    public void Day_ticks_fall_on_midnight_in_the_given_time_zone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");
        // Monday 10:00 UTC (12:00 there) to Thursday 10:00 UTC.
        var geometry = ChartGeometry.Create(Plot, [], Start, Start.AddDays(3));

        var days = geometry.DayStarts(zone);

        Assert.Equal(
            [new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.FromHours(2)), new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.FromHours(2)), new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(2))],
            days);
        Assert.Empty(ChartGeometry.Create(Plot, [], Start, Start.AddHours(5)).DayStarts(zone));
    }

    [Fact]
    public void Labels_keep_to_the_axis_and_leave_out_those_that_would_overlap()
    {
        // Start, end and now: the ends read from the edges, and "now" goes when it's too close to one of them.
        var places = ChartGeometry.PlaceLabels([(0, 30), (200, 30), (100, 30)], 0, 200);
        var crowded = ChartGeometry.PlaceLabels([(0, 30), (200, 30), (20, 30)], 0, 200);
        var tooWide = ChartGeometry.PlaceLabels([(50, 300)], 0, 200);

        Assert.Equal([0, 170, 85], places);
        Assert.Equal([0, 170, null], crowded);
        Assert.Equal([null], tooWide);
    }

    [Fact]
    public void The_plot_leaves_room_for_the_labels_and_never_goes_negative()
    {
        var plot = UsageDetailChart.PlotArea(new Size(300, 130));

        Assert.Equal(new Rect(1, UsageDetailChart.TopPadding, 300 - UsageDetailChart.LevelGutter - 1, 130 - UsageDetailChart.TopPadding - UsageDetailChart.AxisHeight), plot);
        Assert.Equal(0, UsageDetailChart.PlotArea(new Size(10, 10)).Width);
        Assert.Equal(0, UsageDetailChart.PlotArea(new Size(10, 10)).Height);
    }

    [Fact]
    public void Long_series_keep_the_last_point_of_each_bucket()
    {
        var points = Enumerable.Range(0, 180).Select(i => new ChartPoint(Start.AddSeconds(i * 20), i)).Reverse();

        var kept = UsageViewModel.Downsample(points, TimeSpan.FromMinutes(1));

        Assert.Equal(60, kept.Count);
        Assert.Equal(new ChartPoint(Start.AddSeconds(40), 2), kept[0]);
        Assert.Equal(new ChartPoint(Start.AddSeconds(179 * 20), 179), kept[^1]);
        Assert.Empty(UsageViewModel.Downsample([], TimeSpan.FromMinutes(1)));
    }
}
