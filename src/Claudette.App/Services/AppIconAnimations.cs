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

    /// <summary>
    /// The taskbar overlay while a tab waits for a usage limit to reset (Windows): an hourglass whose sand runs slowly
    /// down, rests, and turns over.
    /// </summary>
    public static AppIconAnimation Hourglass { get; } = Load("hourglass", "Waiting for a usage limit to reset", SandRunning);

    /// <summary>The Dock icon while a tab waits for a usage limit to reset (macOS): Claudette by the hourglass.</summary>
    public static AppIconAnimation Waiting { get; } = Load("waiting", "Waiting for a usage limit to reset", SandRunning);

    /// <summary>The hourglass's frames: full, three as the sand runs, empty, and turning over.</summary>
    private static TimeSpan[] SandRunning => [.. new[] { 800, 700, 700, 700, 1000, 250 }.Select(ms => TimeSpan.FromMilliseconds(ms))];

    private static AppIconAnimation Load(string name, string description, int frames, TimeSpan each) =>
        Load(name, description, [.. Enumerable.Repeat(each, frames)]);

    private static AppIconAnimation Load(string name, string description, IReadOnlyList<TimeSpan> durations) =>
        new(name, description, [.. durations.Select((duration, i) => new AppIconFrame(Read($"{name}-{i}.png"), duration))]);

    private static byte[] Read(string file)
    {
        using var stream = typeof(AppIconAnimations).Assembly.GetManifestResourceStream($"AppIcon.{file}")
            ?? throw new InvalidOperationException($"The icon frame {file} isn't embedded. Run packaging/icon/build-icons.mjs.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}
