using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Claudette.App.Services;

/// <summary>
/// Draws the taskbar badge (DESIGN.md §10): the number of tabs needing input in a red circle, as a PNG. Windows shows
/// it as an overlay icon on the taskbar button; macOS draws its own Dock badge from the text instead.
/// </summary>
public static class BadgeIcon
{
    private const int Size = 32;

    /// <summary>Call on the UI thread.</summary>
    public static byte[]? Render(int count)
    {
        if (count <= 0)
        {
            return null;
        }
        var text = count > 9 ? "9+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)), new Pen(Brushes.White, 2), new Point(Size / 2.0, Size / 2.0), Size / 2.0 - 1, Size / 2.0 - 1);
            var label = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), text.Length > 1 ? 15 : 19, Brushes.White);
            context.DrawText(label, new Point((Size - label.Width) / 2, (Size - label.Height) / 2));
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        return stream.ToArray();
    }
}
