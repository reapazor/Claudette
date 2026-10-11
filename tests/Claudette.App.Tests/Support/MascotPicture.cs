using System.Buffers.Binary;
using System.IO.Compression;
using Claudette.App.Mascot;

namespace Claudette.App.Tests.Support;

/// <summary>
/// Claudette on the composer drawn into pixels without Avalonia, for the README's animation: the composer box's top
/// edge in the Claude style's dark colours, and her on it, cell by cell, as <c>MascotFigure</c> draws her. Pixels are
/// indices into <see cref="Palette"/>, as a GIF wants them.
/// </summary>
internal sealed class MascotCanvas
{
    public const int Cell = 4;
    public const int WidthCells = 100;
    public const int HeightCells = 34;

    /// <summary>The box's top edge, in cells from the top, and its left side: her X counts from there.</summary>
    public const int EdgeRow = 24;
    public const int BoxLeft = 3;
    public const int BoxRight = WidthCells - 3;

    private readonly Dictionary<uint, byte> _index = [];
    private readonly byte _page;
    private readonly byte _box;
    private readonly byte _border;
    private readonly byte _muted;
    private readonly byte _placeholder;
    private readonly Dictionary<char, byte> _colors;

    public MascotCanvas()
    {
        _page = Color("#262624");
        _box = Color("#30302E");
        _border = Color("#4A4844");
        _muted = Color("#9C9A92");
        _placeholder = Color("#6F6D66");
        _colors = MascotArt.Palette.ToDictionary(p => p.Key, p => Color(p.Value));
    }

    public int Width => WidthCells * Cell;

    public int Height => HeightCells * Cell;

    /// <summary>The colours the pixels index, as 0xRRGGBB.</summary>
    public List<uint> Palette { get; } = [];

    /// <summary>One frame of her.</summary>
    public byte[] Draw(MascotFrame frame)
    {
        var pixels = new byte[Width * Height];
        Array.Fill(pixels, _page);
        var edge = EdgeRow * Cell;
        var pose = MascotArt.Poses[frame.Pose];
        var left = (BoxLeft + frame.X) * Cell;
        var top = edge - (int)Math.Round((pose.Height - frame.Drop) * Cell);
        Sprite(pixels, pose, left, top);
        if (frame.Hat is { } name && MascotArt.Hats.TryGetValue(name, out var hat))
        {
            Sprite(pixels, hat.Sprite, left + hat.X * Cell, top + hat.Y * Cell);
        }
        Props(pixels, frame, front: false, left, edge);
        DrawBox(pixels, edge);
        Props(pixels, frame, front: true, left, edge);
        return pixels;
    }

    private void Props(byte[] pixels, MascotFrame frame, bool front, int left, int edge)
    {
        foreach (var prop in frame.Props.Where(p => p.Front == front))
        {
            var sprite = MascotArt.Props[prop.Name];
            Sprite(pixels, sprite, left + prop.X * Cell, edge - (prop.Bottom + sprite.Height) * Cell);
        }
    }

    /// <summary>The composer box from its top edge down, rounded at the corners, with a line of placeholder text.</summary>
    private void DrawBox(byte[] pixels, int edge)
    {
        const int radius = 5 * Cell;
        var (x0, x1) = (BoxLeft * Cell, BoxRight * Cell);
        for (var y = edge; y < Height; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var dx = x < x0 + radius ? x0 + radius - x : x >= x1 - radius ? x - (x1 - radius - 1) : 0;
                var dy = y < edge + radius ? edge + radius - y : 0;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance <= radius)
                {
                    pixels[y * Width + x] = distance > radius - 1.5 || y == edge ? _border : _box;
                }
            }
        }
        // "Message Claude…", as a muted bar.
        for (var y = edge + 3 * Cell; y < edge + 4 * Cell; y++)
        {
            Array.Fill(pixels, _placeholder, y * Width + x0 + 5 * Cell, 25 * Cell);
        }
    }

    private void Sprite(byte[] pixels, MascotSprite sprite, int left, int top)
    {
        for (var row = 0; row < sprite.Height; row++)
        {
            for (var column = 0; column < sprite.Width; column++)
            {
                var key = sprite.Rows[row][column];
                if (key == '.')
                {
                    continue;
                }
                var color = key == MascotArt.ThemeColor ? _muted : _colors[key];
                for (var y = Math.Max(0, top + row * Cell); y < Math.Min(Height, top + (row + 1) * Cell); y++)
                {
                    for (var x = Math.Max(0, left + column * Cell); x < Math.Min(Width, left + (column + 1) * Cell); x++)
                    {
                        pixels[y * Width + x] = color;
                    }
                }
            }
        }
    }

    private byte Color(string hex)
    {
        var rgb = Convert.ToUInt32(hex[1..], 16);
        if (!_index.TryGetValue(rgb, out var index))
        {
            index = (byte)Palette.Count;
            Palette.Add(rgb);
            _index[rgb] = index;
        }
        return index;
    }
}

/// <summary>An animated GIF that loops, from frames of palette indices, each shown for its time.</summary>
internal static class GifWriter
{
    public static byte[] Write(int width, int height, IReadOnlyList<uint> palette, IReadOnlyList<(byte[] Pixels, TimeSpan Duration)> frames)
    {
        var bits = Math.Max(2, (int)Math.Ceiling(Math.Log2(Math.Max(2, palette.Count))));
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write("GIF89a"u8);
        writer.Write((ushort)width);
        writer.Write((ushort)height);
        writer.Write((byte)(0x80 | ((bits - 1) << 4) | (bits - 1)));
        writer.Write((ushort)0);
        for (var i = 0; i < 1 << bits; i++)
        {
            var color = i < palette.Count ? palette[i] : 0;
            writer.Write([(byte)(color >> 16), (byte)(color >> 8), (byte)color]);
        }
        writer.Write([0x21, 0xFF, 0x0B, .. "NETSCAPE2.0"u8, 0x03, 0x01, 0x00, 0x00, 0x00]);
        foreach (var (pixels, duration) in frames)
        {
            writer.Write([0x21, 0xF9, 0x04, 0x00]);
            writer.Write((ushort)Math.Max(2, Math.Round(duration.TotalMilliseconds / 10)));
            writer.Write([0x00, 0x00, 0x2C]);
            writer.Write(0u);
            writer.Write((ushort)width);
            writer.Write((ushort)height);
            writer.Write([0x00, (byte)bits]);
            var data = Lzw(pixels, bits);
            for (var i = 0; i < data.Length; i += 255)
            {
                var length = Math.Min(255, data.Length - i);
                writer.Write((byte)length);
                writer.Write(data, i, length);
            }
            writer.Write((byte)0);
        }
        writer.Write((byte)0x3B);
        writer.Flush();
        return output.ToArray();
    }

    /// <summary>
    /// GIF's variable-width LZW, in the classic encoder's order: each code is written at the current width, which grows
    /// once the next code to give out no longer fits it, as the decoder's table does; a clear code starts over when the
    /// table fills.
    /// </summary>
    private static byte[] Lzw(byte[] indices, int minimumBits)
    {
        var clear = 1 << minimumBits;
        var end = clear + 1;
        var output = new List<byte>(indices.Length / 4);
        var table = new Dictionary<int, int>();
        int buffer = 0, filled = 0, width = minimumBits + 1, nextCode = end + 1;
        void Write(int code)
        {
            buffer |= code << filled;
            filled += width;
            while (filled >= 8)
            {
                output.Add((byte)buffer);
                buffer >>= 8;
                filled -= 8;
            }
            if (code == clear)
            {
                table.Clear();
                nextCode = end + 1;
                width = minimumBits + 1;
            }
            else if (nextCode > (1 << width) - 1 && width < 12)
            {
                width++;
            }
        }
        Write(clear);
        int prefix = indices[0];
        for (var i = 1; i < indices.Length; i++)
        {
            var next = indices[i];
            var key = (prefix << 8) | next;
            if (table.TryGetValue(key, out var code))
            {
                prefix = code;
                continue;
            }
            Write(prefix);
            if (nextCode < 4096)
            {
                table[key] = nextCode++;
            }
            else
            {
                Write(clear);
            }
            prefix = next;
        }
        Write(prefix);
        Write(end);
        if (filled > 0)
        {
            output.Add((byte)buffer);
        }
        return [.. output];
    }
}

/// <summary>A PNG of palette indices, for looking over frames side by side.</summary>
internal static class PngWriter
{
    public static byte[] Write(int width, int height, IReadOnlyList<uint> palette, byte[] pixels)
    {
        var raw = new byte[(width * 3 + 1) * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = palette[pixels[y * width + x]];
                var at = y * (width * 3 + 1) + 1 + x * 3;
                (raw[at], raw[at + 1], raw[at + 2]) = ((byte)(color >> 16), (byte)(color >> 8), (byte)color);
            }
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        (header[8], header[9]) = (8, 2);
        Chunk(output, "IHDR", header);
        Chunk(output, "IDAT", compressed.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        byte[] body = [.. System.Text.Encoding.ASCII.GetBytes(type), .. data];
        output.Write(body);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(body));
        output.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return ~crc;
    }
}
