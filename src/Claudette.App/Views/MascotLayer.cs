using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Claudette.App.Mascot;

namespace Claudette.App.Views;

/// <summary>
/// Claudette on the composer (DESIGN.md §5): the layer behind the composer box that she stands in. It sits in the same
/// place as the box, before it, so the box hides whatever of her is below its top edge, and she rises and falls with
/// the box as it grows. It tells her where on the edge she has room (the part nothing sits on, between the corners),
/// which way the pointer is, and shows what she says in a bubble. A click pokes her; dragging picks her up. The rest of
/// the layer lets clicks through to what's under it. A second layer after the box (<see cref="IsFront"/>) draws what
/// goes in front of it, her legs over it while she sits on the edge.
/// </summary>
public sealed class MascotLayer : Control
{
    public static readonly StyledProperty<MascotDirector?> DirectorProperty =
        AvaloniaProperty.Register<MascotLayer, MascotDirector?>(nameof(Director));

    /// <summary>The composer box she stands on, for its rounded corners.</summary>
    public static readonly StyledProperty<Border?> BoxProperty = AvaloniaProperty.Register<MascotLayer, Border?>(nameof(Box));

    /// <summary>Her menu's <b>Hide Claudette</b>.</summary>
    public static readonly StyledProperty<ICommand?> HideCommandProperty = AvaloniaProperty.Register<MascotLayer, ICommand?>(nameof(HideCommand));

    /// <summary>The colour of the z's, the "!" and her other marks: the theme's muted text.</summary>
    public static readonly StyledProperty<IBrush?> ThemeBrushProperty = AvaloniaProperty.Register<MascotLayer, IBrush?>(nameof(ThemeBrush));

    /// <summary>Her hair tie's colour: her tab's group's, or null for her own berry.</summary>
    public static readonly StyledProperty<Color?> HairTieProperty = AvaloniaProperty.Register<MascotLayer, Color?>(nameof(HairTie));

    /// <summary>Screen pixels in a cell of her sprite at 100%: Settings → Appearance → her size.</summary>
    public static readonly StyledProperty<double> PixelsPerCellProperty = AvaloniaProperty.Register<MascotLayer, double>(nameof(PixelsPerCell), 3);

    /// <summary>The layer after the box: it draws only what goes in front of it, takes no clicks and tells her nothing.</summary>
    public static readonly StyledProperty<bool> IsFrontProperty = AvaloniaProperty.Register<MascotLayer, bool>(nameof(IsFront));

    /// <summary>The pointer this far either side of her, in cells, she looks its way.</summary>
    private const double GazeCells = 6;

    /// <summary>A press that moves this far, in pixels, picks her up rather than poking her.</summary>
    private const double DragThreshold = 4;

    private readonly List<Control> _keepClearOf = [];
    private MascotFigure _figure = new(front: false);
    private readonly Border _bubble;
    private readonly TextBlock _words;
    private Control[] _home = [];
    private MascotDirector? _director;
    private TopLevel? _topLevel;
    private bool _attached;
    private bool _following;
    private bool _shown;
    private double _cell = 3;
    private Size _clipped;
    private Point? _pressedAt;
    private Point _grabbedAt;

    public MascotLayer()
    {
        _words = new TextBlock { Classes = { "small" }, TextWrapping = TextWrapping.Wrap };
        _bubble = new Border { Classes = { "mascotbubble" }, Child = _words, IsVisible = false, IsHitTestVisible = false };
        UseFigure(_figure);
        VisualChildren.Add(_bubble);
        LogicalChildren.Add(_bubble);
    }

    public MascotDirector? Director
    {
        get => GetValue(DirectorProperty);
        set => SetValue(DirectorProperty, value);
    }

    public Border? Box
    {
        get => GetValue(BoxProperty);
        set => SetValue(BoxProperty, value);
    }

    public ICommand? HideCommand
    {
        get => GetValue(HideCommandProperty);
        set => SetValue(HideCommandProperty, value);
    }

    public IBrush? ThemeBrush
    {
        get => GetValue(ThemeBrushProperty);
        set => SetValue(ThemeBrushProperty, value);
    }

    public Color? HairTie
    {
        get => GetValue(HairTieProperty);
        set => SetValue(HairTieProperty, value);
    }

    public double PixelsPerCell
    {
        get => GetValue(PixelsPerCellProperty);
        set => SetValue(PixelsPerCellProperty, value);
    }

    public bool IsFront
    {
        get => GetValue(IsFrontProperty);
        set => SetValue(IsFrontProperty, value);
    }

    /// <summary>Her figure, for tests.</summary>
    internal Control Figure => _figure;

    /// <summary>The bubble of what she says, for tests.</summary>
    internal Border Bubble => _bubble;

    /// <summary>
    /// Things outside the composer that can sit just above the box, such as <b>Jump to latest</b>: she keeps clear of
    /// them, and ducks behind the box while one spans it.
    /// </summary>
    public void KeepClearOf(IEnumerable<Control> controls) => _keepClearOf.AddRange(controls);

    /// <summary>Where she tends to go back to: over the first of <paramref name="controls"/> that shows (Send, or Stop in its place).</summary>
    public void SetHome(params Control[] controls) => _home = controls;

    private void UseFigure(MascotFigure figure)
    {
        VisualChildren.Remove(_figure);
        LogicalChildren.Remove(_figure);
        _figure = figure;
        _figure.IsVisible = false;
        _figure.Cell = _cell;
        _figure.ThemeBrush = ThemeBrush;
        _figure.HairTie = HairTie;
        VisualChildren.Insert(0, _figure);
        LogicalChildren.Insert(0, _figure);
        if (!figure.Front)
        {
            figure.PointerPressed += OnFigurePressed;
            figure.PointerMoved += OnFigureMoved;
            figure.PointerReleased += OnFigureReleased;
            figure.PointerCaptureLost += (_, _) => LetGo();
            var hide = new MenuItem { Header = "Hide Claudette" };
            hide.Bind(MenuItem.CommandProperty, this.GetObservable(HideCommandProperty));
            figure.ContextMenu = new ContextMenu { Items = { hide } };
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DirectorProperty)
        {
            Use(change.GetNewValue<MascotDirector?>());
        }
        else if (change.Property == ThemeBrushProperty)
        {
            _figure.ThemeBrush = change.GetNewValue<IBrush?>();
        }
        else if (change.Property == HairTieProperty)
        {
            _figure.HairTie = change.GetNewValue<Color?>();
        }
        else if (change.Property == PixelsPerCellProperty)
        {
            InvalidateArrange();
        }
        else if (change.Property == IsFrontProperty)
        {
            IsHitTestVisible = !change.GetNewValue<bool>();
            UseFigure(new MascotFigure(front: change.GetNewValue<bool>()));
            ShowFrame();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Follow();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        _shown = false;
        Follow();
        Report(director => director.SetShown(this, false));
    }

    private void Use(MascotDirector? director)
    {
        if (_director is { } old)
        {
            old.PropertyChanged -= OnDirectorChanged;
            Report(_ => old.SetShown(this, false));
        }
        _director = director;
        _shown = false;
        if (director is not null)
        {
            director.PropertyChanged += OnDirectorChanged;
        }
        ShowFrame();
        Follow();
    }

    /// <summary>Only the layer behind the box tells her things; the one in front just draws.</summary>
    private void Report(Action<MascotDirector> tell)
    {
        if (!IsFront && _director is { } director)
        {
            tell(director);
        }
    }

    /// <summary>
    /// Watches the layout while attached with her to show: it's how the layer sees the box and what's on it change.
    /// And, behind the box, the pointer, which she looks toward.
    /// </summary>
    private void Follow()
    {
        var follow = _attached && _director is not null;
        if (follow == _following)
        {
            return;
        }
        _following = follow;
        if (follow)
        {
            LayoutUpdated += OnLayoutUpdated;
            if (!IsFront && TopLevel.GetTopLevel(this) is { } top)
            {
                _topLevel = top;
                top.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
                top.AddHandler(PointerExitedEvent, OnPointerLeft, RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);
            }
            InvalidateArrange();
        }
        else
        {
            LayoutUpdated -= OnLayoutUpdated;
            if (_topLevel is { } top)
            {
                top.RemoveHandler(PointerMovedEvent, OnPointerMoved);
                top.RemoveHandler(PointerExitedEvent, OnPointerLeft);
                _topLevel = null;
            }
        }
    }

    /// <summary>Her new frame. Every tab's composer has a layer, so only the one on screen follows her; the others catch up when shown.</summary>
    private void OnDirectorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MascotDirector.Frame) && _shown)
        {
            ShowFrame();
        }
    }

    private void ShowFrame()
    {
        var frame = _director?.Frame;
        _figure.Frame = frame;
        _figure.IsVisible = _figure.HasSomething(frame);
        var say = IsFront ? null : frame?.Say;
        if (_words.Text != say)
        {
            _words.Text = say;
        }
        _bubble.IsVisible = say is not null && _figure.IsVisible;
        InvalidateArrange();
    }

    /// <summary>
    /// After each layout pass: whether she's on screen (her tab is the selected one), the size of a cell, and where on
    /// the edge she has room.
    /// </summary>
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_director is not { } director)
        {
            return;
        }
        var shown = IsEffectivelyVisible && Bounds.Width > 0;
        Report(_ => director.SetShown(this, shown));
        if (shown != _shown)
        {
            _shown = shown;
            if (shown)
            {
                ShowFrame();
            }
        }
        if (!shown)
        {
            return;
        }
        var cell = CellSize();
        if (cell != _cell)
        {
            _cell = cell;
            _figure.Cell = cell;
            InvalidateArrange();
        }
        Report(_ => director.SetRoom(Room(cell)));
    }

    /// <summary>
    /// A cell's size here: a whole number of the screen's pixels, near <see cref="PixelsPerCell"/> at the display's
    /// scaling and the window's zoom, so her edges stay crisp.
    /// </summary>
    private double CellSize()
    {
        var scale = Scale();
        return Math.Max(1, Math.Round(PixelsPerCell * scale, MidpointRounding.AwayFromZero)) / scale;
    }

    /// <summary>Screen pixels per pixel here: the display's scaling times the window's zoom.</summary>
    private double Scale()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return 1;
        }
        var zoom = this.TransformToVisual(top)?.M11 ?? 1;
        return top.RenderScaling * (zoom > 0 ? zoom : 1);
    }

    /// <summary>
    /// Where on the box's top edge she keeps to: its last third, in the rightmost stretch between its corners that nothing
    /// sits on just above (not the working line's words, the suffix chips, a card over the box, or what
    /// <see cref="KeepClearOf"/> named) and that she fits in, or that stretch's right end when its part of the last third
    /// is too small. Her home is over Send, and set down anywhere on that stretch, she lands there. Null when there's no
    /// room for her.
    /// </summary>
    private MascotRoom? Room(double cell)
    {
        var width = Bounds.Width;
        var inset = (Box?.CornerRadius.TopLeft ?? 0) + 2;
        var bandHeight = (MascotArt.Height + 1) * cell;
        var band = new Rect(0, -bandHeight, width, bandHeight);
        List<(double Left, double Right)> free = [(inset, width - inset)];
        foreach (var taken in Taken().Where(r => r.Intersects(band)))
        {
            free = [.. free.SelectMany(f => Without(f, (taken.Left - cell, taken.Right + cell)))];
        }
        // A cell over the least, so rounding to whole cells still leaves her enough.
        var least = (MascotDirector.LeastRoom + 1) * cell;
        var fits = free.Where(f => f.Right - f.Left >= least).ToList();
        if (fits.Count == 0)
        {
            return null;
        }
        var (edgeLeft, right) = fits.MaxBy(f => f.Right);
        var left = Math.Max(edgeLeft, Math.Min(width * 2 / 3, right - least));
        var home = _home.FirstOrDefault(c => c.IsEffectivelyVisible) is { } anchor && Here(anchor, new Rect(anchor.Bounds.Size)) is { } over
            ? (int)Math.Round(over.Center.X / cell - MascotArt.Width / 2.0)
            : int.MaxValue;
        var room = new MascotRoom((int)Math.Ceiling(left / cell), (int)Math.Floor(right / cell), home)
        {
            EdgeLeft = (int)Math.Ceiling(edgeLeft / cell),
            EdgeRight = (int)Math.Floor(right / cell),
        };
        return room.Width >= MascotDirector.LeastRoom ? room : null;
    }

    private static IEnumerable<(double Left, double Right)> Without((double Left, double Right) free, (double Left, double Right) taken)
    {
        if (taken.Right <= free.Left || taken.Left >= free.Right)
        {
            yield return free;
            yield break;
        }
        if (taken.Left > free.Left)
        {
            yield return (free.Left, taken.Left);
        }
        if (taken.Right < free.Right)
        {
            yield return (taken.Right, free.Right);
        }
    }

    /// <summary>
    /// What's drawn just above the box, here: the words and chips of what the composer stacks over it (each leaf, so
    /// the working line takes only as much of the edge as its words), and the controls to keep clear of.
    /// </summary>
    private IEnumerable<Rect> Taken()
    {
        if (Parent is Control stage && stage.Parent is Panel area)
        {
            foreach (var above in area.Children.TakeWhile(c => c != stage).Where(c => c.IsVisible))
            {
                foreach (var leaf in above.GetVisualDescendants().OfType<Visual>().Where(IsDrawnLeaf))
                {
                    if (Here(leaf, new Rect(leaf.Bounds.Size)) is { } rect)
                    {
                        yield return rect;
                    }
                }
            }
        }
        foreach (var control in _keepClearOf.Where(c => c.IsEffectivelyVisible))
        {
            if (Here(control, new Rect(control.Bounds.Size)) is { } rect)
            {
                yield return rect;
            }
        }
    }

    private static bool IsDrawnLeaf(Visual visual) =>
        visual.IsEffectivelyVisible && visual.Bounds is { Width: > 0, Height: > 0 } && !visual.GetVisualChildren().Any();

    private Rect? Here(Visual visual, Rect rect) => visual.TransformToVisual(this) is { } transform ? rect.TransformToAABB(transform) : null;

    protected override Size MeasureOverride(Size availableSize)
    {
        _figure.Measure(Size.Infinity);
        _bubble.Measure(new Size(280, double.PositiveInfinity));
        // Nothing of the layer's own: it takes the box's place without changing its size.
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (finalSize != _clipped)
        {
            // Whatever of her is below the box's bottom edge, behind it, stays hidden there too.
            _clipped = finalSize;
            Clip = new RectangleGeometry(new Rect(-finalSize.Width, -10_000, 3 * finalSize.Width, 10_000 + finalSize.Height));
        }
        if (_director?.Frame is not { } frame)
        {
            _figure.Arrange(default);
            _bubble.Arrange(default);
            return finalSize;
        }
        var height = MascotArt.HeightOf(frame.Pose);
        var (x, y) = PixelOffset();
        var scale = Scale();
        // Falling, she's part-way between cells: kept on whole screen pixels.
        var top = Math.Round(-(height - frame.Drop) * _cell * scale) / scale;
        var figure = new Rect(x + frame.X * _cell, y + top, MascotArt.Width * _cell, height * _cell);
        _figure.Arrange(figure);
        if (_bubble.IsVisible)
        {
            // Over her head, hat and all, leaning left of her since she lives at the box's right.
            var size = _bubble.DesiredSize;
            var left = Math.Clamp(figure.Right - size.Width + 2 * _cell, 0, Math.Max(0, finalSize.Width - size.Width));
            _bubble.Arrange(new Rect(left, figure.Top - 3 * _cell - size.Height, size.Width, size.Height));
        }
        return finalSize;
    }

    /// <summary>How far to move her so her corner falls on a whole screen pixel, which keeps every cell's edges crisp.</summary>
    private (double X, double Y) PixelOffset()
    {
        if (TopLevel.GetTopLevel(this) is not { } top || this.TranslatePoint(default, top) is not { } origin)
        {
            return (0, 0);
        }
        var scale = Scale();
        var x = origin.X * top.RenderScaling;
        var y = origin.Y * top.RenderScaling;
        return ((Math.Round(x) - x) / scale, (Math.Round(y) - y) / scale);
    }

    // ---- The pointer --------------------------------------------------------------------------------------------------

    /// <summary>She looks toward the pointer when it's well to one side of her.</summary>
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_shown || _director is not { Frame: var frame } director)
        {
            return;
        }
        var offset = (e.GetPosition(this).X - (frame.X + MascotArt.Width / 2.0) * _cell) / _cell;
        director.SetGaze(offset > GazeCells ? 1 : offset < -GazeCells ? -1 : 0);
    }

    private void OnPointerLeft(object? sender, PointerEventArgs e)
    {
        if (ReferenceEquals(e.Source, _topLevel))
        {
            _director?.SetGaze(0);
        }
    }

    /// <summary>A press on her: a poke when it's let go where it was, or the start of carrying her when it moves.</summary>
    private void OnFigurePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_figure).Properties.IsLeftButtonPressed)
        {
            return;
        }
        _pressedAt = e.GetPosition(this);
        _grabbedAt = e.GetPosition(_figure);
        e.Pointer.Capture(_figure);
        e.Handled = true;
    }

    private void OnFigureMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedAt is not { } pressedAt || _director is not { } director)
        {
            return;
        }
        var at = e.GetPosition(this);
        if (!director.IsCarried)
        {
            if (Math.Abs(at.X - pressedAt.X) < DragThreshold && Math.Abs(at.Y - pressedAt.Y) < DragThreshold)
            {
                return;
            }
            director.BeginCarry();
            if (!director.IsCarried)
            {
                return;
            }
        }
        var (offsetX, offsetY) = PixelOffset();
        var left = at.X - _grabbedAt.X - offsetX;
        var top = at.Y - _grabbedAt.Y - offsetY;
        director.Carry((int)Math.Round(left / _cell), top / _cell + MascotArt.HeightOf("carried"));
    }

    private void OnFigureReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressedAt is null)
        {
            return;
        }
        var carried = _director?.IsCarried == true;
        _pressedAt = null;
        e.Pointer.Capture(null);
        if (carried)
        {
            _director!.Release();
        }
        else
        {
            _director?.Poke();
        }
        e.Handled = true;
    }

    private void LetGo()
    {
        _pressedAt = null;
        if (_director is { IsCarried: true } director)
        {
            director.Release();
        }
    }
}
