using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace Claudette.App.Services;

/// <summary>Attached images (DESIGN.md §5, "Attachments"): bitmaps as bytes to send, and bytes as thumbnails to show.</summary>
public static class ImageFiles
{
    /// <summary>Thumbnails are decoded at twice their shown height, for high-DPI screens.</summary>
    private const int ThumbnailHeight = 144;

    /// <summary>A bitmap from the clipboard or a drag, as PNG.</summary>
    public static byte[] ToPng(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    /// <summary>Image bytes to a small bitmap; null when they can't be decoded.</summary>
    public static readonly IValueConverter Thumbnail = new FuncValueConverter<byte[]?, Bitmap?>(data =>
    {
        if (data is not { Length: > 0 })
        {
            return null;
        }
        try
        {
            using var stream = new MemoryStream(data);
            return Bitmap.DecodeToHeight(stream, ThumbnailHeight, BitmapInterpolationMode.MediumQuality);
        }
        catch (Exception)
        {
            // A format Skia can't decode (for example an animated WebP it doesn't support); show nothing.
            return null;
        }
    });
}
