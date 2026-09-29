namespace Claudette.App.Services;

/// <summary>One frame of an <see cref="AppIconAnimation"/>: a PNG, and how long it shows.</summary>
public sealed record AppIconFrame(byte[] Png, TimeSpan Duration);

/// <summary>An animation for the Dock icon or the taskbar button's overlay (DESIGN.md §10).</summary>
/// <param name="Description">What it means, for screen readers.</param>
public sealed record AppIconAnimation(string Name, string Description, IReadOnlyList<AppIconFrame> Frames);

/// <summary>
/// The icon's animations (DESIGN.md §10). <c>packaging/icon/build-icons.mjs</c> draws their frames into
/// <c>Assets/AppIcon</c>, embedded as <c>AppIcon.&lt;name&gt;-&lt;frame&gt;.png</c>.
/// </summary>
public static class AppIconAnimations
{
    /// <summary>The taskbar overlay while tabs work (Windows): Claude's spark, pulsing.</summary>
    public static AppIconAnimation Spark { get; } = Load("spark", "Claude is working", frames: 6, TimeSpan.FromMilliseconds(160));

    /// <summary>The Dock icon while tabs work (macOS): Claudette typing.</summary>
    public static AppIconAnimation Typing { get; } = Load("typing", "Claude is working", frames: 2, TimeSpan.FromMilliseconds(220));

    /// <summary>The Dock icon while a tab needs input (macOS): Claudette waving.</summary>
    public static AppIconAnimation Waving { get; } = Load("waving", "A tab needs your input", frames: 2, TimeSpan.FromMilliseconds(450));

    private static AppIconAnimation Load(string name, string description, int frames, TimeSpan each) =>
        new(name, description, [.. Enumerable.Range(0, frames).Select(i => new AppIconFrame(Read($"{name}-{i}.png"), each))]);

    private static byte[] Read(string file)
    {
        using var stream = typeof(AppIconAnimations).Assembly.GetManifestResourceStream($"AppIcon.{file}")
            ?? throw new InvalidOperationException($"The icon frame {file} isn't embedded. Run packaging/icon/build-icons.mjs.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}
