using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering;
using Claudette.App.Mascot;

namespace Claudette.App.Views;

/// <summary>
/// Claudette on the composer, drawn (DESIGN.md §5): her pose, her hat and her props, cell by cell, in the palette
/// <c>build-icons.mjs</c> gives them; the z's, "!" and other marks in the theme's muted text colour, and her hair tie in
/// her tab's group's colour. A figure in front of the box draws only the props that go in front of it. Her whole
/// figure takes clicks, gaps and all.
/// </summary>
internal sealed class MascotFigure : Control, ICustomHitTest
{
    private static readonly Dictionary<char, IBrush> Palette =
        MascotArt.Palette.ToDictionary(p => p.Key, p => (IBrush)new ImmutableSolidColorBrush(Color.Parse(p.Value)));

    private IBrush? _hairTie;

    public MascotFigure(bool front)
    {
        Front = front;
        // The layer puts her on whole screen pixels itself, which layout rounding would undo at a zoom.
        UseLayoutRounding = false;
        IsHitTestVisible = !front;
        if (!front)
        {
            Cursor = new Cursor(StandardCursorType.Hand);
        }
        // She's decoration: screen readers pass her by.
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
    }

    /// <summary>Draws only what goes in front of the box.</summary>
    public bool Front { get; }

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
    } = 3;

    public IBrush? ThemeBrush
    {
        get;
        set
        {
            field = value;
            InvalidateVisual();
        }
    }

    public Color? HairTie
    {
        set
        {
            _hairTie = value is { } color ? new ImmutableSolidColorBrush(color) : null;
            InvalidateVisual();
        }
    }

    /// <summary>Something of hers is drawn here.</summary>
    public bool HasSomething(MascotFrame? frame) => frame is not null && (Front ? frame.Props.Any(p => p.Front) : frame.IsVisible);

    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    protected override AutomationPeer OnCreateAutomationPeer() => new NoneAutomationPeer(this);

    public override void Render(DrawingContext context)
    {
        if (Frame is not { } frame || !MascotArt.Poses.TryGetValue(frame.Pose, out var pose))
        {
            return;
        }
        if (!Front)
        {
            Draw(context, pose, 0, 0);
            if (frame.Hat is { } name && MascotArt.Hats.TryGetValue(name, out var hat))
            {
                Draw(context, hat.Sprite, hat.X, hat.Y);
            }
        }
        // Props stand on the edge, which is this many rows below her top.
        var edge = pose.Height - frame.Drop;
        foreach (var prop in frame.Props.Where(p => p.Front == Front))
        {
            if (MascotArt.Props.TryGetValue(prop.Name, out var sprite))
            {
                Draw(context, sprite, prop.X, edge - prop.Bottom - sprite.Height);
            }
        }
    }

    /// <summary>Each row's runs of one colour as one rectangle.</summary>
    private void Draw(DrawingContext context, MascotSprite sprite, double left, double top)
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

    private IBrush? Brush(char key) => key switch
    {
        MascotArt.ThemeColor => ThemeBrush,
        MascotArt.HairTie when _hairTie is not null => _hairTie,
        _ => Palette.GetValueOrDefault(key),
    };
}
