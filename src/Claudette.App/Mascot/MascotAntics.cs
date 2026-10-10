namespace Claudette.App.Mascot;

/// <summary>
/// The things Claudette on the composer does, as steps (DESIGN.md §5). Her poses and props are in
/// <see cref="MascotArt"/>; a drop is how many cells lower she is than standing on the edge.
/// </summary>
internal static class MascotAntics
{
    /// <summary>Her laptop's lid, in front of her while she types.</summary>
    public static readonly MascotProp Laptop = new("laptop", 1, 0);

    /// <summary>The z's while she sleeps leaning on the edge, over her ponytail.</summary>
    public static readonly MascotProp SmallZ = new("z", 9, 11);

    public static readonly MascotProp BigZ = new("bigZ", 12, 16);

    /// <summary>The hourglass beside her while a usage limit holds the task, its sand at <paramref name="frame"/> of three.</summary>
    public static MascotProp Hourglass(int frame) => new($"hourglass-{frame}", 13, 0);

    /// <summary>"!" over her head, at <paramref name="drop"/>.</summary>
    private static MascotProp Bang(int drop) => new("bang", 5, MascotArt.Height - drop + 1);

    private static MascotStep Step(string pose, int ms, int move = 0, int drop = 0, params MascotProp[] props) =>
        new(pose, TimeSpan.FromMilliseconds(ms), move, drop, props);

    /// <summary>Standing about for <paramref name="wait"/>, then a blink.</summary>
    public static IEnumerable<MascotStep> StandAbout(TimeSpan wait) => [new("stand", wait), Step("blink", 140)];

    /// <summary>Back on her feet from <paramref name="drop"/>.</summary>
    public static MascotStep Settle(int drop) => Step("stand", 100, drop: drop > 0 ? 1 : 0);

    public static IEnumerable<MascotStep> LookAround(Random random) =>
    [
        Step("lookLeft", 900 + random.Next(600)),
        Step("stand", 300),
        Step("lookRight", 900 + random.Next(600)),
        Step("blink", 140),
        Step("stand", 0),
    ];

    /// <summary><paramref name="cells"/> steps, left when negative, a cell at a time, then a look the way she went.</summary>
    public static IEnumerable<MascotStep> Walk(int cells)
    {
        var way = cells < 0 ? "Left" : "Right";
        for (var i = 0; i < Math.Abs(cells); i++)
        {
            yield return Step($"walk{way}-{i % 4}", 130, move: Math.Sign(cells));
        }
        yield return Step($"look{way}", 500);
        yield return Step("stand", 0);
    }

    /// <summary>Arms folded on the edge for a few seconds, standing behind it.</summary>
    public static IEnumerable<MascotStep> Lean(Random random) =>
    [
        Step("stand", 110, drop: 1),
        Step("lean", 2000 + random.Next(2000), drop: 2),
        Step("leanBlink", 140, drop: 2),
        Step("lean", 1500 + random.Next(2000), drop: 2),
        Step("stand", 110, drop: 1),
        Step("stand", 0),
    ];

    public static IEnumerable<MascotStep> Stretch() =>
        [Step("stretch", 1300), Step("stand", 200), Step("blink", 140), Step("stand", 0)];

    public static IEnumerable<MascotStep> Wave(int times) =>
        [.. Enumerable.Range(0, times).SelectMany(_ => new[] { Step("wave", 280), Step("waveHigh", 280) }), Step("stand", 0)];

    /// <summary>A crouch and a little jump, arms up.</summary>
    public static IEnumerable<MascotStep> Hop() =>
    [
        Step("stand", 140, drop: 1),
        Step("armsUp", 90, drop: -1),
        Step("armsUp", 160, drop: -2),
        Step("armsUp", 90, drop: -1),
        Step("stand", 90),
        Step("stand", 110, drop: 1),
        Step("stand", 0),
    ];

    /// <summary>A poke: she jumps, with a "!" over her head.</summary>
    public static IEnumerable<MascotStep> Startled() =>
    [
        Step("armsUp", 100, drop: -2, props: Bang(-2)),
        Step("armsUp", 280, drop: -3, props: Bang(-3)),
        Step("armsUp", 100, drop: -2, props: Bang(-2)),
        Step("stand", 450, props: Bang(0)),
        Step("blink", 140),
        Step("stand", 0),
    ];

    /// <summary>She looks down, flails, and drops behind the box, where she stays a moment.</summary>
    public static IEnumerable<MascotStep> TopplesOff(Random random) =>
    [
        Step("lookDown", 800),
        Step("armsUp", 130),
        Step("wave", 130),
        Step("armsUp", 130),
        Step("waveHigh", 130),
        Step("armsUp", 90, drop: 1),
        Step("armsUp", 80, drop: 3),
        Step("armsUp", 70, drop: 6),
        Step("armsUp", 60, drop: 10),
        Step("armsUp", 1500 + random.Next(1500), drop: MascotDirector.Behind),
    ];

    /// <summary>
    /// From behind the box: her hands on the edge, then her head, a look each way, and up onto it. Hanging, she's 14
    /// cells tall, so at 13 only her hands show.
    /// </summary>
    public static IEnumerable<MascotStep> ClimbUp() =>
    [
        Step("hang", 600, drop: 13),
        Step("hang", 450, drop: 12),
        Step("hang", 140, drop: 10),
        Step("hang", 140, drop: 8),
        Step("hang", 700, drop: 6),
        Step("hangLookLeft", 450, drop: 6),
        Step("hangLookRight", 450, drop: 6),
        Step("hang", 120, drop: 4),
        Step("lean", 260, drop: 2),
        Step("stand", 120, drop: 1),
        Step("stand", 0),
    ];

    /// <summary>Down behind the box, quickly.</summary>
    public static IEnumerable<MascotStep> Duck() =>
        [Step("stand", 70, drop: 1), Step("stand", 60, drop: 4), Step("stand", 60, drop: 8), Step("stand", 0, drop: MascotDirector.Behind)];

    /// <summary>A burst of typing on her laptop, then a pause.</summary>
    public static IEnumerable<MascotStep> Typing(Random random)
    {
        var keys = 10 + random.Next(8);
        for (var i = 0; i < keys; i++)
        {
            yield return Step(i % 2 == 0 ? "typeLeft" : "typeRight", 220, props: Laptop);
        }
        yield return Step("stand", 1200 + random.Next(1800), props: Laptop);
        yield return Step("blink", 140, props: Laptop);
    }

    /// <summary>
    /// Asleep, leaning on the edge, the z's rising; by the hourglass while a usage limit holds the task.
    /// <paramref name="settle"/> leans her on the edge first.
    /// </summary>
    public static IEnumerable<MascotStep> Doze(Random random, bool hourglass, bool settle)
    {
        MascotProp[] With(int frame, params MascotProp[] props) => hourglass ? [.. props, Hourglass(frame)] : props;
        if (settle)
        {
            yield return Step("stand", 110, drop: 1, props: With(0));
            yield return Step("lean", 500 + random.Next(500), drop: 2, props: With(0));
        }
        yield return Step("leanBlink", 1100, drop: 2, props: With(0, SmallZ));
        yield return Step("leanBlink", 1100, drop: 2, props: With(1, BigZ));
        yield return Step("leanBlink", 900, drop: 2, props: With(2));
    }

    /// <summary>Out of a nap: a blink, and up.</summary>
    public static IEnumerable<MascotStep> WakeUp() =>
        [Step("leanBlink", 200, drop: 2), Step("lean", 350, drop: 2), Step("stand", 110, drop: 1), Step("stand", 0)];
}
