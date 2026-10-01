using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Claudette.App.Controls;

namespace Claudette.App.UiTests;

/// <summary>The conversation's virtualizing panel (DESIGN.md §5), with rows of set heights.</summary>
public class ConversationPanelTests
{
    private const double ViewHeight = 400;

    [AvaloniaFact]
    public void Only_the_rows_in_view_and_a_screen_either_side_have_controls()
    {
        var (window, scroll, items, rows) = Show(300);

        scroll.Offset = new Vector(0, 3000);
        UiText.Settle(window);

        // One run of rows around the view: at least half a screen either side, and no more than a screen and a row
        // (the tallest is 110). The rows above it are estimated, so where they end moves a little as rows are measured.
        var built = Built(items).ToList();
        var offset = scroll.Offset.Y;
        Assert.Equal(Enumerable.Range(built[0].Index, built.Count), built.Select(b => b.Index));
        Assert.InRange(built[0].Bounds.Top, offset - ViewHeight - 110, offset - ViewHeight / 2);
        Assert.InRange(built[^1].Bounds.Bottom, offset + ViewHeight * 1.5, offset + 2 * ViewHeight + 110);
        Assert.Equal(rows.Count, items.ItemCount);
        GC.KeepAlive(window);
    }

    [AvaloniaFact]
    public void Each_row_starts_where_the_one_above_it_ends()
    {
        var (window, scroll, items, rows) = Show(100);

        // Every row measured, from the top to the bottom.
        for (var offset = 0.0; offset < scroll.Extent.Height; offset += ViewHeight)
        {
            scroll.Offset = new Vector(0, offset);
            UiText.Settle(window);
        }

        var built = Built(items).ToList();
        for (var i = 1; i < built.Count; i++)
        {
            Assert.Equal(built[i - 1].Bounds.Bottom, built[i].Bounds.Top);
        }
        Assert.Equal(rows.Sum(r => r.Height), scroll.Extent.Height);
        GC.KeepAlive(window);
    }

    [AvaloniaFact]
    public void A_row_changing_height_moves_only_the_rows_after_it_and_builds_none_again()
    {
        var (window, _, items, rows) = Show(100);
        var before = Built(items).ToDictionary(b => b.Index, b => (b.Container, b.Bounds.Top));
        var prepared = 0;
        items.ContainerPrepared += (_, _) => prepared++;

        rows[3].Height += 50;
        UiText.Settle(window);
        rows[3].Height += 25;
        UiText.Settle(window);

        Assert.Equal(0, prepared);
        foreach (var (index, container, bounds) in Built(items))
        {
            if (before.TryGetValue(index, out var was))
            {
                Assert.Same(was.Container, container);
                Assert.Equal(index > 3 ? was.Top + 75 : was.Top, bounds.Top);
            }
        }
        GC.KeepAlive(window);
    }

    [AvaloniaFact]
    public void Adding_and_removing_rows_keeps_the_other_rows_controls_in_order()
    {
        var (window, _, items, rows) = Show(100);
        var before = Built(items).ToDictionary(b => rows[b.Index], b => b.Container);

        rows.Insert(2, new Row(1000, 33));
        rows.RemoveAt(6);
        rows.Insert(0, new Row(1001, 21));
        UiText.Settle(window);

        var built = Built(items).ToList();
        foreach (var (index, container, _) in built)
        {
            Assert.Same(rows[index], container.DataContext);
            Assert.Equal(index, items.IndexFromContainer(container));
            if (before.TryGetValue(rows[index], out var was))
            {
                Assert.Same(was, container);
            }
        }
        for (var i = 1; i < built.Count; i++)
        {
            Assert.Equal(built[i - 1].Bounds.Bottom, built[i].Bounds.Top);
        }
        Assert.Equal(0, built[0].Index);
        Assert.Equal(21, built[0].Bounds.Height);
        GC.KeepAlive(window);
    }

    [AvaloniaFact]
    public void Clearing_the_rows_and_adding_new_ones_builds_the_new_ones()
    {
        var (window, scroll, items, rows) = Show(100);

        rows.Clear();
        UiText.Settle(window);
        Assert.Empty(Built(items));
        Assert.Equal(scroll.Viewport.Height, scroll.Extent.Height);

        rows.Add(new Row(0, 50));
        rows.Add(new Row(1, 70));
        UiText.Settle(window);
        Assert.Equal(new[] { (0, 0.0, 50.0), (1, 50.0, 70.0) }, Built(items).Select(b => (b.Index, b.Bounds.Top, b.Bounds.Height)));
        GC.KeepAlive(window);
    }

    [AvaloniaFact]
    public void Scrolling_to_a_row_far_below_builds_it_in_view()
    {
        var (window, scroll, items, _) = Show(300);

        items.ScrollIntoView(250);
        UiText.Settle(window);

        var container = items.ContainerFromIndex(250);
        Assert.NotNull(container);
        var top = container.TranslatePoint(default, scroll)!.Value.Y;
        Assert.InRange(top, -0.5, ViewHeight - container.Bounds.Height + 0.5);
        GC.KeepAlive(window);
    }

    private static IEnumerable<(int Index, Control Container, Rect Bounds)> Built(ItemsControl items) =>
        items.GetRealizedContainers().Select(c => (Index: items.IndexFromContainer(c), Container: c, c.Bounds)).OrderBy(b => b.Index);

    /// <summary>Rows of different heights in a scroll viewer as high as <see cref="ViewHeight"/>.</summary>
    private static (Window Window, ScrollViewer Scroll, ItemsControl Items, ObservableCollection<Row> Rows) Show(int count)
    {
        var rows = new ObservableCollection<Row>(Enumerable.Range(0, count).Select(i => new Row(i, 20 + i % 7 * 15)));
        var items = new ItemsControl
        {
            ItemsSource = rows,
            ItemsPanel = new FuncTemplate<Panel?>(() => new ConversationPanel { CacheLength = 1 }),
            ItemTemplate = new FuncDataTemplate<Row>((_, _) => new RowView()),
        };
        var scroll = new ScrollViewer { Content = items };
        var window = UiText.Show(scroll, 300, ViewHeight);
        return (window, scroll, items, rows);
    }

    private sealed class Row(int number, double height) : INotifyPropertyChanged
    {
        public int Number { get; } = number;

        public double Height
        {
            get;
            set
            {
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Height)));
            }
        } = height;

        public event PropertyChangedEventHandler? PropertyChanged;

        public override string ToString() => $"row {Number}";
    }

    /// <summary>As high as its row says, following it as it changes.</summary>
    private sealed class RowView : Border
    {
        private Row? _row;

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_row is not null)
            {
                _row.PropertyChanged -= OnRowChanged;
            }
            _row = DataContext as Row;
            if (_row is not null)
            {
                _row.PropertyChanged += OnRowChanged;
            }
            Height = _row?.Height ?? 0;
        }

        private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => Height = _row!.Height;
    }
}
