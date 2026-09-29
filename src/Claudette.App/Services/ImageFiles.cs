using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Claudette.Core.Composer;

namespace Claudette.App.Services;

/// <summary>Attached images (DESIGN.md §5, "Attachments"): bitmaps as bytes to send, and bytes as thumbnails to show.</summary>
public static class ImageFiles
{
    /// <summary>Thumbnails are decoded at twice their shown height, for high-DPI screens.</summary>
    private const int ThumbnailHeight = 144;

    /// <summary>The JPEG quality for a bitmap too big as PNG: close to the original, at about a tenth of the size.</summary>
    internal const int JpegQuality = 90;

    /// <summary>
    /// A bitmap from the clipboard or a drag, as bytes to attach: PNG, or JPEG when the PNG would be over
    /// <see cref="Attachments.MaxImageBytes"/>, as a photo-like screenshot of a 5K screen (a game, say) can be.
    /// </summary>
    public static byte[] Encode(Bitmap bitmap) => Encode(options =>
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, options);
        return stream.ToArray();
    });

    /// <summary>The choice <see cref="Encode(Bitmap)"/> makes, given a way to save the bitmap.</summary>
    internal static byte[] Encode(Func<BitmapEncoderOptions, byte[]> save)
    {
        var png = save(PngBitmapEncoderOptions.Default);
        return png.LongLength <= Attachments.MaxImageBytes ? png : save(new JpegBitmapEncoderOptions { Quality = JpegQuality });
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
