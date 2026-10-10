using System.Text.Json;

namespace Claudette.App.Mascot;

/// <summary>A pose or prop as rows of cells: a palette letter each, or <c>.</c> for none.</summary>
public sealed record MascotSprite(IReadOnlyList<string> Rows)
{
    public int Width => Rows.Count == 0 ? 0 : Rows[0].Length;

    public int Height => Rows.Count;
}

/// <summary>
/// Claudette on the composer's poses and props (DESIGN.md §5). <c>packaging/icon/build-icons.mjs</c> draws them from
/// the icon's sprite into <c>Assets/Mascot/mascot.json</c>, embedded as <c>Mascot.mascot.json</c>.
/// </summary>
public static class MascotArt
{
    /// <summary>Her width in cells, in every pose.</summary>
    public const int Width = 12;

    /// <summary>Her height in cells.</summary>
    public const int Height = 12;

    /// <summary>The palette letter drawn in the theme's muted text colour rather than one of its own.</summary>
    public const char ThemeColor = 'Z';

    private static readonly Lazy<Art> Loaded = new(Load);

    /// <summary>Her poses, by name.</summary>
    public static IReadOnlyDictionary<string, MascotSprite> Poses => Loaded.Value.Poses;

    /// <summary>What she has with her: the z's, the laptop, the hourglass.</summary>
    public static IReadOnlyDictionary<string, MascotSprite> Props => Loaded.Value.Props;

    /// <summary>Each palette letter's colour, as <c>#RRGGBB</c>.</summary>
    public static IReadOnlyDictionary<char, string> Palette => Loaded.Value.Palette;

    /// <summary>How tall <paramref name="pose"/> is in cells.</summary>
    public static int HeightOf(string pose) => Poses.TryGetValue(pose, out var sprite) ? sprite.Height : Height;

    private sealed record Art(
        IReadOnlyDictionary<string, MascotSprite> Poses,
        IReadOnlyDictionary<string, MascotSprite> Props,
        IReadOnlyDictionary<char, string> Palette);

    private static Art Load()
    {
        using var stream = typeof(MascotArt).Assembly.GetManifestResourceStream("Mascot.mascot.json")
            ?? throw new InvalidOperationException("Claudette's poses aren't embedded. Run packaging/icon/build-icons.mjs.");
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;
        return new Art(
            Sprites(root.GetProperty("poses")),
            Sprites(root.GetProperty("props")),
            root.GetProperty("palette").EnumerateObject().ToDictionary(p => p.Name[0], p => p.Value.GetString() ?? "#000000"));
    }

    private static Dictionary<string, MascotSprite> Sprites(JsonElement element) =>
        element.EnumerateObject().ToDictionary(
            p => p.Name,
            p => new MascotSprite([.. p.Value.EnumerateArray().Select(row => row.GetString() ?? "")]),
            StringComparer.Ordinal);
}
