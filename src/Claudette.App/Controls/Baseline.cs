using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Claudette.App.Controls;

/// <summary>
/// Puts a text block's first line on another's baseline (<see cref="AlignWithProperty"/>), for text in different fonts
/// or sizes side by side, such as monospace beside the interface font (DESIGN.md §3, "Visual style"). Lining them up by
/// their tops or centres only looks right when the two fonts happen to agree; baselines are what the eye reads as one
/// line, whatever the fonts are (each OS has its own) and however big (Settings → Appearance sets the code font's size).
/// After each layout pass it moves the text by the difference between the two baselines, on whole screen pixels, with
/// a render transform, so its layout, and its row's height, stay as they were.
/// </summary>
public static class Baseline
{
    /// <summary>The text block whose first baseline this one's first line goes on.</summary>
    public static readonly AttachedProperty<TextBlock?> AlignWithProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, TextBlock?>("AlignWith", typeof(Baseline));

    static Baseline()
    {
        AlignWithProperty.Changed.AddClassHandler<TextBlock>((text, e) =>
        {
            text.LayoutUpdated -= OnLayoutUpdated;
            if (e.GetNewValue<TextBlock?>() is not null)
            {
                text.LayoutUpdated += OnLayoutUpdated;
            }
            else
            {
                text.RenderTransform = null;
            }
        });
    }

    public static TextBlock? GetAlignWith(TextBlock element) => element.GetValue(AlignWithProperty);

    public static void SetAlignWith(TextBlock element, TextBlock? value) => element.SetValue(AlignWithProperty, value);

    /// <summary>How far down this text block's first line is moved, in pixels: up when it's negative.</summary>
    internal static double ShiftOf(TextBlock text) => text.RenderTransform is TranslateTransform shift ? shift.Y : 0;

    /// <summary>Where a text block's first baseline is drawn, in <paramref name="relativeTo"/>'s coordinates.</summary>
    internal static double? BaselineIn(TextBlock text, Visual relativeTo) =>
        text.TranslatePoint(new Point(0, text.Padding.Top + text.TextLayout.Baseline), relativeTo)?.Y;

    private static void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is not TextBlock text || GetAlignWith(text) is not { } other || !text.IsEffectivelyVisible || !other.IsEffectivelyVisible
            || TopLevel.GetTopLevel(text) is not { } top || BaselineIn(other, text) is not { } theirs)
        {
            return;
        }
        // Their baseline is measured where this one is drawn, already moved by the shift it has now.
        var current = ShiftOf(text);
        var shift = current + theirs - (text.Padding.Top + text.TextLayout.Baseline);
        var scale = top.RenderScaling * (text.TransformToVisual(top)?.M22 is > 0 and var zoom ? zoom : 1);
        shift = Math.Round(shift * scale) / scale;
        if (Math.Abs(shift - current) > 0.01)
        {
            text.RenderTransform = shift == 0 ? null : new TranslateTransform(0, shift);
        }
    }
}
