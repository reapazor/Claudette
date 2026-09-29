using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace Claudette.App.Controls;

/// <summary>
/// Scrolls the lines of code inside it sideways, all together, with a scroll bar along the bottom (DESIGN.md §8). The
/// rows stay the width of the view, so their colors reach across it, and only the code in each <see cref="ScrollingLine"/>
/// moves: line numbers stay where they are.
/// </summary>
/// <remarks>
/// It scrolls as far as the widest line laid out since <see cref="Lines"/> last changed. In a virtualized list that's the
/// widest line shown so far, so the scroll bar grows when a wider line comes into view, as it does in VS Code, and never
/// shrinks under the pointer.
/// </remarks>
public sealed class LineScroller : DockPanel
{
    public static readonly StyledProperty<IEnumerable?> LinesProperty =
        AvaloniaProperty.Register<LineScroller, IEnumerable?>(nameof(Lines));

    /// <summary>How far a notch of the mouse wheel scrolls, as it does in a ScrollViewer.</summary>
    private const double WheelStep = 50;

    private readonly ScrollBar _bar = new() { Orientation = Orientation.Horizontal, SmallChange = WheelStep, IsVisible = false };
    private readonly HashSet<ScrollingLine> _lines = [];
    private double _widest;
    private double _viewport;

    public LineScroller()
    {
        SetDock(_bar, Dock.Bottom);
        Children.Add(_bar);
        _bar.ValueChanged += (_, _) => ArrangeLines();
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
    }

    /// <summary>The lines shown. A new list scrolls back to the start, and its lines are measured afresh.</summary>
    public IEnumerable? Lines
    {
        get => GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    /// <summary>How far the code is scrolled from the start of the lines.</summary>
    public double Offset
    {
        get => _bar.Value;
        set => _bar.Value = value;
    }

    /// <summary>How far the code can scroll: the widest line less the width there is to show it.</summary>
    public double ScrollableWidth => _bar.Maximum;

    internal void Add(ScrollingLine line) => _lines.Add(line);

    internal void Remove(ScrollingLine line) => _lines.Remove(line);

    /// <summary>
    /// A line was laid out <paramref name="width"/> wide, with <paramref name="viewport"/> to show it in. Returns how far
    /// to move it.
    /// </summary>
    internal double Arranged(double width, double viewport)
    {
        if (width > _widest || viewport != _viewport)
        {
            _widest = Math.Max(_widest, width);
            _viewport = viewport;
            Update();
        }
        return _bar.Value;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LinesProperty)
        {
            _widest = 0;
            _bar.Value = 0;
            Update();
            ArrangeLines();
        }
    }

    private void Update()
    {
        // Less than a pixel over is rounding, not a line that doesn't fit.
        var scrollable = _widest - _viewport < 1 ? 0 : _widest - _viewport;
        _bar.Maximum = scrollable;
        _bar.ViewportSize = _viewport;
        _bar.LargeChange = _viewport;
        _bar.IsVisible = scrollable > 0;
    }

    private void ArrangeLines()
    {
        foreach (var line in _lines)
        {
            line.InvalidateArrange();
        }
    }

    /// <summary>
    /// Shift turns the wheel sideways, as it does in a ScrollViewer, and a touchpad can scroll sideways by itself. The
    /// list scrolls itself up and down, so a scroll that also goes up or down carries on to it.
    /// </summary>
    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        var shifted = e.KeyModifiers == KeyModifiers.Shift && e.Delta.X == 0;
        var sideways = shifted ? e.Delta.Y : e.Delta.X;
        if (!_bar.IsVisible || sideways == 0)
        {
            return;
        }
        Offset -= sideways * WheelStep;
        e.Handled = shifted || e.Delta.Y == 0;
    }
}
