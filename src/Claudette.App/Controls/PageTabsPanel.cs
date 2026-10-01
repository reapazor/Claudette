using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Claudette.App.Controls;

/// <summary>
/// The side panel's page tabs (DESIGN.md §3, "Side panel"): in a row while they all fit. When they don't, as many as fit
/// stay in the row, always with the page showing among them (the tab with the <c>selected</c> class), and the panel's
/// last child, the button that lists the rest, follows them; it's hidden the rest of the time. The tabs left out are
/// arranged past the end, where the panel clips them, and are taken out of the Tab order.
/// </summary>
public sealed class PageTabsPanel : Panel
{
    private readonly List<Control> _overflow = [];

    public PageTabsPanel()
    {
        ClipToBounds = true;
    }

    /// <summary>The tabs left out of the row at the last arrange, in order.</summary>
    public IReadOnlyList<Control> Overflow => _overflow;

    private IEnumerable<Control> Tabs => Children.Take(Children.Count - 1).Where(c => c.IsVisible);

    protected override void ChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        base.ChildrenChanged(sender, e);
        foreach (var child in e.OldItems?.OfType<Control>() ?? [])
        {
            child.Classes.CollectionChanged -= OnChildClassesChanged;
        }
        foreach (var child in e.NewItems?.OfType<Control>() ?? [])
        {
            child.Classes.CollectionChanged += OnChildClassesChanged;
        }
    }

    // Another page showing may need a place in the row.
    private void OnChildClassesChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateArrange();

    protected override Size MeasureOverride(Size availableSize)
    {
        var unbounded = availableSize.WithWidth(double.PositiveInfinity);
        foreach (var child in Children)
        {
            child.Measure(unbounded);
        }
        var width = Tabs.Sum(t => t.DesiredSize.Width);
        var height = Children.Select(c => c.DesiredSize.Height).DefaultIfEmpty().Max();
        return new Size(Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _overflow.Clear();
        if (Children.Count == 0)
        {
            return finalSize;
        }
        var more = Children[^1];
        var tabs = Tabs.ToList();
        var inRow = InRow(tabs, finalSize.Width, more.DesiredSize.Width);
        var x = 0.0;
        foreach (var tab in tabs)
        {
            var shown = inRow.Contains(tab);
            if (shown)
            {
                tab.Arrange(new Rect(x, 0, tab.DesiredSize.Width, finalSize.Height));
                x += tab.DesiredSize.Width;
            }
            else
            {
                _overflow.Add(tab);
            }
            KeyboardNavigation.SetIsTabStop(tab, shown);
        }
        foreach (var tab in _overflow)
        {
            tab.Arrange(new Rect(finalSize.Width, 0, tab.DesiredSize.Width, finalSize.Height));
        }
        // Showing or hiding the button changes its width, so the next pass places the tabs again beside it.
        more.IsVisible = _overflow.Count > 0;
        more.Arrange(new Rect(x, 0, more.DesiredSize.Width, finalSize.Height));
        return finalSize;
    }

    /// <summary>
    /// The tabs that stay in the row: all of them when they fit, or else as many as fit beside the button in order, with
    /// the page showing taking the last place if it wasn't among them.
    /// </summary>
    private static List<Control> InRow(List<Control> tabs, double width, double moreWidth)
    {
        if (Fits(tabs.Sum(t => t.DesiredSize.Width), width))
        {
            return tabs;
        }
        var room = width - moreWidth;
        var inRow = new List<Control>();
        var used = 0.0;
        foreach (var tab in tabs)
        {
            if (!Fits(used + tab.DesiredSize.Width, room))
            {
                break;
            }
            inRow.Add(tab);
            used += tab.DesiredSize.Width;
        }
        if (tabs.FirstOrDefault(t => t.Classes.Contains("selected")) is { } showing && !inRow.Contains(showing))
        {
            while (inRow.Count > 0 && !Fits(used + showing.DesiredSize.Width, room))
            {
                used -= inRow[^1].DesiredSize.Width;
                inRow.RemoveAt(inRow.Count - 1);
            }
            inRow.Add(showing);
        }
        return inRow;
    }

    // Layout rounding can leave the sum of the widths a hair over the width they came from.
    private static bool Fits(double width, double room) => width <= room + 0.01;
}
