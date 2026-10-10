namespace Claudette.App.Mascot;

/// <summary>
/// The things Claudette on the composer does, as steps (DESIGN.md §5). Her poses and props are in
/// <see cref="MascotArt"/>; a drop is how many cells lower she is than standing on the edge.
/// </summary>
internal static class MascotAntics
{
    /// <summary>Her hands gripping the edge while she climbs up, and as they first reach it, a cell lower.</summary>
    public static readonly MascotProp Hands = new("hands", 0, 0);

    private static readonly MascotProp Reaching = new("hands", 0, -1);

    /// <summary>Her laptop's lid, in front of her while she types.</summary>
    public static readonly MascotProp Laptop = new("laptop", 1, 0);

    /// <summary>The z's while she sleeps leaning on the edge, over her ponytail.</summary>
    public static readonly MascotProp SmallZ = new("z", 9, 11);

    public static readonly MascotProp BigZ = new("bigZ", 12, 16);

    /// <summary>The hourglass beside her while a usage limit holds the task, its sand at <paramref name="frame"/> of three.</summary>
    public static MascotProp Hourglass(int frame) => new($"hourglass-{frame}", 13, 0);

    /// <summary>"!" over her head, at <paramref name="drop"/>.</summary>
    private static MascotProp Bang(int drop) => new("bang", 5, MascotArt.Height - drop + 1);

    /// <summary>"?" over her ponytail.</summary>
    private static readonly MascotProp Question = new("question", 10, 13);

    /// <summary>The ball, <paramref name="bottom"/> cells up, over her right hand.</summary>
    private static MascotProp Ball(int bottom) => new("ball", 10, bottom);

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

    /// <summary>A little dance: swaying a cell each way, one arm up, then both, then the other.</summary>
    public static IEnumerable<MascotStep> Dance() =>
    [
        .. Enumerable.Range(0, 3).SelectMany(_ => new[]
        {
            Step("wave", 200, move: -1),
            Step("armsUp", 200, move: 1, drop: -1),
            Step("waveRight", 200, move: 1),
            Step("armsUp", 200, move: -1, drop: -1),
        }),
        Step("stand", 0),
    ];

    /// <summary>A twirl, twice round: the back of her head half-way.</summary>
    public static IEnumerable<MascotStep> Twirl() =>
    [
        .. Enumerable.Range(0, 2).SelectMany(_ => new[] { Step("lookLeft", 110), Step("back", 110), Step("lookRight", 110), Step("stand", 110) }),
        Step("stand", 200),
        Step("blink", 140),
        Step("stand", 0),
    ];

    public static IEnumerable<MascotStep> Yawn() => [Step("stand", 200), Step("yawn", 1400), Step("blink", 250), Step("stand", 0)];

    public static IEnumerable<MascotStep> TapFoot() =>
        [.. Enumerable.Range(0, 6).SelectMany(_ => new[] { Step("tap", 180), Step("stand", 180) }), Step("blink", 140), Step("stand", 0)];

    /// <summary>She tosses a ball up from her raised hand and catches it, twice.</summary>
    public static IEnumerable<MascotStep> TossBall() =>
    [
        .. Enumerable.Range(0, 2).SelectMany(_ => new[]
        {
            Step("waveRight", 250, props: Ball(8)),
            Step("stand", 90, props: Ball(11)),
            Step("stand", 90, props: Ball(14)),
            Step("stand", 110, props: Ball(16)),
            Step("stand", 160, props: Ball(17)),
            Step("stand", 110, props: Ball(16)),
            Step("stand", 90, props: Ball(14)),
            Step("stand", 90, props: Ball(11)),
        }),
        Step("waveRight", 300, props: Ball(8)),
        Step("stand", 0),
    ];

    /// <summary>A look each way with a "?" over her.</summary>
    public static IEnumerable<MascotStep> Puzzled() =>
    [
        Step("lookLeft", 600, props: Question),
        Step("lookRight", 600, props: Question),
        Step("stand", 700, props: Question),
        Step("blink", 140),
        Step("stand", 0),
    ];

    /// <summary>A little bounce, and a heart floats up from her, drifting.</summary>
    public static IEnumerable<MascotStep> BlowHeart() =>
    [
        Step("stand", 200, drop: 1),
        Step("armsUp", 150, drop: -1, props: new MascotProp("heart", 4, 14)),
        Step("stand", 300, props: new MascotProp("heart", 4, 15)),
        Step("stand", 300, props: new MascotProp("heart", 5, 16)),
        Step("blink", 300, props: new MascotProp("heart", 5, 17)),
        Step("stand", 300, props: new MascotProp("heart", 6, 18)),
        Step("stand", 0),
    ];

    /// <summary>Eyes shut, her head goes back, and achoo: a puff blows off to her side.</summary>
    public static IEnumerable<MascotStep> Sneeze() =>
    [
        Step("blink", 400),
        Step("blink", 300, drop: -1),
        Step("achoo", 140, drop: 1, props: new MascotProp("puff", -4, 7)),
        Step("stand", 260, props: new MascotProp("puff", -6, 8)),
        Step("stand", 200, props: new MascotProp("puff", -8, 9)),
        Step("blink", 140),
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
    /// From behind the box: her hands reach the edge and grip it, and stay there while she pulls herself up between
    /// them, peeks over and looks each way; once her shoulders are up, her arms are on the edge, and she climbs onto it.
    /// </summary>
    public static IEnumerable<MascotStep> ClimbUp() =>
    [
        Step("climb", 250, drop: MascotArt.Height, props: Reaching),
        Step("climb", 600, drop: MascotArt.Height, props: Hands),
        Step("climb", 110, drop: 11, props: Hands),
        Step("climb", 110, drop: 10, props: Hands),
        Step("climb", 110, drop: 9, props: Hands),
        Step("climb", 110, drop: 8, props: Hands),
        Step("climb", 110, drop: 7, props: Hands),
        Step("climb", 700, drop: 6, props: Hands),
        Step("climbLookLeft", 450, drop: 6, props: Hands),
        Step("climbLookRight", 450, drop: 6, props: Hands),
        Step("climb", 130, drop: 5, props: Hands),
        Step("climb", 160, drop: 4, props: Hands),
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
