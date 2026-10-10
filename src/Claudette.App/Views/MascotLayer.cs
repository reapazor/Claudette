using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering;
using Avalonia.VisualTree;
using Claudette.App.Mascot;

namespace Claudette.App.Views;

/// <summary>
/// Claudette on the composer (DESIGN.md §5): the layer behind the composer box that she stands in. It sits in the same
/// place as the box, before it, so the box hides whatever of her is below its top edge, and she rises and falls with
/// the box as it grows. It tells her where on the edge she has room: the part nothing sits on, between the corners.
/// The rest of the layer lets clicks through to what's under it.
/// </summary>
public sealed class MascotLayer : Control
{
    /// <summary>Screen pixels in a cell of her sprite at 100%: she's 36 pixels tall.</summary>
    public const double PixelsPerCell = 3;

    public static readonly StyledProperty<MascotDirector?> DirectorProperty =
        AvaloniaProperty.Register<MascotLayer, MascotDirector?>(nameof(Director));

    /// <summary>The composer box she stands on, for its rounded corners.</summary>
    public static readonly StyledProperty<Border?> BoxProperty = AvaloniaProperty.Register<MascotLayer, Border?>(nameof(Box));

    /// <summary>Her menu's <b>Hide Claudette</b>.</summary>
    public static readonly StyledProperty<ICommand?> HideCommandProperty = AvaloniaProperty.Register<MascotLayer, ICommand?>(nameof(HideCommand));

    /// <summary>The colour of the z's over her while she sleeps, and the "!" when she's startled: the theme's muted text.</summary>
    public static readonly StyledProperty<IBrush?> ThemeBrushProperty = AvaloniaProperty.Register<MascotLayer, IBrush?>(nameof(ThemeBrush));

    private readonly MascotFigure _figure = new();
    private readonly List<Control> _keepClearOf = [];
    private MascotDirector? _director;
    private bool _attached;
    private bool _following;
    private bool _shown;
    private double _cell = PixelsPerCell;
    private Size _clipped;

    public MascotLayer()
    {
        _figure.IsVisible = false;
        VisualChildren.Add(_figure);
        LogicalChildren.Add(_figure);
        _figure.PointerPressed += OnFigurePressed;
        var hide = new MenuItem { Header = "Hide Claudette" };
        hide.Bind(MenuItem.CommandProperty, this.GetObservable(HideCommandProperty));
        _figure.ContextMenu = new ContextMenu { Items = { hide } };
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

    /// <summary>Her figure, for tests.</summary>
    internal Control Figure => _figure;

    /// <summary>
    /// Things outside the composer that can sit just above the box, such as <b>Jump to latest</b>: she keeps clear of
    /// them, and ducks behind the box while one spans it.
    /// </summary>
    public void KeepClearOf(IEnumerable<Control> controls) => _keepClearOf.AddRange(controls);

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
        _director?.SetShown(this, false);
    }

    private void Use(MascotDirector? director)
    {
        if (_director is { } old)
        {
            old.PropertyChanged -= OnDirectorChanged;
            old.SetShown(this, false);
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

    /// <summary>Watches the layout while attached with her to show: it's how the layer sees the box and what's on it change.</summary>
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
            InvalidateArrange();
        }
        else
        {
            LayoutUpdated -= OnLayoutUpdated;
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
        _figure.IsVisible = frame is { IsHidden: false };
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
        director.SetShown(this, shown);
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
        director.SetRoom(Room(cell));
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
    /// The widest stretch of the box's top edge, between its corners, that nothing sits on just above: not the working
    /// line's words, the suffix chips, a card over the box, or what <see cref="KeepClearOf"/> named. Null when there's
    /// too little for her.
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
        if (free.Count == 0)
        {
            return null;
        }
        var (left, right) = free.MaxBy(f => f.Right - f.Left);
        var room = new MascotRoom((int)Math.Ceiling(left / cell), (int)Math.Floor(right / cell));
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
            return finalSize;
        }
        var height = MascotArt.HeightOf(frame.Pose);
        var (x, y) = PixelOffset();
        _figure.Arrange(new Rect(x + frame.X * _cell, y - (height - frame.Drop) * _cell, MascotArt.Width * _cell, height * _cell));
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

    /// <summary>A click pokes her; a right-click opens her menu.</summary>
    private void OnFigurePressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(_figure).Properties.IsLeftButtonPressed)
        {
            _director?.Poke();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Draws her pose and props cell by cell, in the palette <c>build-icons.mjs</c> gives them; the z's and "!" in the
    /// theme's muted text colour. Her whole figure takes clicks, gaps and all.
    /// </summary>
    private sealed class MascotFigure : Control, ICustomHitTest
    {
        private static readonly Dictionary<char, IBrush> Palette =
            MascotArt.Palette.ToDictionary(p => p.Key, p => (IBrush)new ImmutableSolidColorBrush(Color.Parse(p.Value)));

        public MascotFigure()
        {
            // The layer puts her on whole screen pixels itself, which layout rounding would undo at a zoom.
            UseLayoutRounding = false;
            Cursor = new Cursor(StandardCursorType.Hand);
            // She's decoration: screen readers pass her by.
            AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        }

        public MascotFrame? Frame
        {
            get;
            set
            {
                if (field != value)
                {
                    field = value;
                    InvalidateVisual();
                }
            }
        }

        public double Cell
        {
            get;
            set
            {
                field = value;
                InvalidateVisual();
            }
        } = PixelsPerCell;

        public IBrush? ThemeBrush
        {
            get;
            set
            {
                field = value;
                InvalidateVisual();
            }
        }

        public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

        protected override AutomationPeer OnCreateAutomationPeer() => new NoneAutomationPeer(this);

        public override void Render(DrawingContext context)
        {
            if (Frame is not { } frame || !MascotArt.Poses.TryGetValue(frame.Pose, out var pose))
            {
                return;
            }
            Draw(context, pose, 0, 0);
            // Props stand on the edge, which is this many rows below her top.
            var edge = pose.Height - frame.Drop;
            foreach (var prop in frame.Props)
            {
                if (MascotArt.Props.TryGetValue(prop.Name, out var sprite))
                {
                    Draw(context, sprite, prop.X, edge - prop.Bottom - sprite.Height);
                }
            }
        }

        /// <summary>Each row's runs of one colour as one rectangle.</summary>
        private void Draw(DrawingContext context, MascotSprite sprite, int left, int top)
        {
            for (var y = 0; y < sprite.Height; y++)
            {
                var row = sprite.Rows[y];
                for (var x = 0; x < row.Length;)
                {
                    var end = x + 1;
                    while (end < row.Length && row[end] == row[x])
                    {
                        end++;
                    }
                    if (row[x] != '.' && Brush(row[x]) is { } brush)
                    {
                        context.FillRectangle(brush, new Rect((left + x) * Cell, (top + y) * Cell, (end - x) * Cell, Cell));
                    }
                    x = end;
                }
            }
        }

        private IBrush? Brush(char key) => key == MascotArt.ThemeColor ? ThemeBrush : Palette.GetValueOrDefault(key);
    }
}
