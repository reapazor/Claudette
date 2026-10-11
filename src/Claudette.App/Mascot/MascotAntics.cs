namespace Claudette.App.Mascot;

/// <summary>
/// The things Claudette on the composer does, as steps (DESIGN.md §5). Her poses and props are in
/// <see cref="MascotArt"/>; a drop is how many cells lower she is than standing on the edge. Falls and jumps go by
/// gravity, a step every 25 milliseconds; everything else moves a whole cell at a time, as pixel art does.
/// </summary>
internal static class MascotAntics
{
    /// <summary>How fast she falls, in cells a second, a second.</summary>
    public const double Gravity = 140;

    /// <summary>A step of a fall or a jump.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(25);

    /// <summary>Her laptop's lid, in front of her while she types.</summary>
    public static readonly MascotProp Laptop = new("laptop", 1, 0);

    /// <summary>The clipboard she holds in plan mode, and writes on while Claude works.</summary>
    public static readonly MascotProp Clipboard = new("clipboard", 2, 1);

    /// <summary>Her hands gripping the edge while she climbs up, and as they first reach it, a cell lower.</summary>
    public static readonly MascotProp Hands = new("hands", 0, 0);

    private static readonly MascotProp Reaching = new("hands", 0, -1);

    /// <summary>The z's while she sleeps leaning on the edge, over her ponytail.</summary>
    public static readonly MascotProp SmallZ = new("z", 9, 11);

    public static readonly MascotProp BigZ = new("bigZ", 12, 16);

    /// <summary>The hourglass beside her while a usage limit holds the task, its sand at <paramref name="frame"/> of three.</summary>
    public static MascotProp Hourglass(int frame) => new($"hourglass-{frame}", 13, 0);

    /// <summary>Her coffee on the edge beside her through a long turn, steaming.</summary>
    public static MascotProp Mug(int frame) => new($"mug-{frame % 2}", 13, 0);

    /// <summary>"!" over her head, at <paramref name="drop"/>.</summary>
    private static MascotProp Bang(double drop) => new("bang", 5, (int)Math.Round(MascotArt.Height - drop) + 1);

    /// <summary>"?" over her ponytail.</summary>
    private static readonly MascotProp Question = new("question", 10, 13);

    /// <summary>An arrow toward the sidebar, on her left.</summary>
    private static readonly MascotProp Arrow = new("arrow", -6, 6);

    private static MascotProp Ball(int x, int bottom) => new("ball", x, bottom);

    private static MascotProp Puff(int x, int bottom) => new("puff", x, bottom);

    private static MascotStep Step(string pose, int ms, int move = 0, double drop = 0, params MascotProp[] props) =>
        new(pose, TimeSpan.FromMilliseconds(ms), move, drop, props);

    private static MascotStep Saying(string pose, int ms, string? say, params MascotProp[] props) =>
        new(pose, TimeSpan.FromMilliseconds(ms), Props: props, Say: say);

    // ---- Moving -------------------------------------------------------------------------------------------------------

    /// <summary>Falling from <paramref name="from"/> to <paramref name="to"/> cells down under gravity.</summary>
    public static IEnumerable<MascotStep> Fall(string pose, double from, double to, params MascotProp[] props)
    {
        for (var t = Tick.TotalSeconds; ; t += Tick.TotalSeconds)
        {
            var drop = from + Gravity * t * t / 2;
            if (drop >= to)
            {
                yield return new MascotStep(pose, Tick, Drop: to, Props: props);
                yield break;
            }
            yield return new MascotStep(pose, Tick, Drop: drop, Props: props);
        }
    }

    /// <summary>A jump <paramref name="height"/> cells up and back down under gravity, in <paramref name="pose"/>.</summary>
    public static IEnumerable<MascotStep> Jump(string pose, double height, Func<double, MascotProp[]>? props = null)
    {
        var speed = Math.Sqrt(2 * Gravity * height);
        var time = 2 * speed / Gravity;
        for (var t = Tick.TotalSeconds; t < time; t += Tick.TotalSeconds)
        {
            var drop = -(speed * t - Gravity * t * t / 2);
            yield return new MascotStep(pose, Tick, Drop: drop, Props: props?.Invoke(drop) ?? []);
        }
    }

    /// <summary>Standing about for <paramref name="wait"/>, then a blink; holding her clipboard in plan mode.</summary>
    public static IEnumerable<MascotStep> StandAbout(TimeSpan wait, bool planning = false) =>
    [
        new("stand", wait, Props: planning ? [Clipboard] : []),
        new("blink", TimeSpan.FromMilliseconds(140), Props: planning ? [Clipboard] : []),
    ];

    /// <summary>Back on her feet from <paramref name="drop"/>.</summary>
    public static MascotStep Settle(double drop) => Step("stand", 100, drop: drop > 0 ? 1 : 0);

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

    /// <summary>Sitting on the edge, arms beside her on it and her legs over the front of the box, swinging.</summary>
    public static IEnumerable<MascotStep> Sit(Random random)
    {
        yield return Step("stand", 110, drop: 1);
        string[] swing = ["dangle-1", "dangle-0", "dangle-2", "dangle-0"];
        var steps = 20 + random.Next(12);
        for (var i = 0; i < steps; i++)
        {
            yield return Step(i == steps / 2 ? "leanBlink" : "lean", 230, drop: 2, props: new MascotProp(swing[i % 4], 0, -2, Front: true));
        }
        yield return Step("stand", 110, drop: 1);
        yield return Step("stand", 0);
    }

    public static IEnumerable<MascotStep> Stretch() =>
        [Step("stretch", 1300), Step("stand", 200), Step("blink", 140), Step("stand", 0)];

    public static IEnumerable<MascotStep> Wave(int times) =>
        [.. Enumerable.Range(0, times).SelectMany(_ => new[] { Step("wave", 280), Step("waveHigh", 280) }), Step("stand", 0)];

    /// <summary>A crouch and a little jump, arms up.</summary>
    public static IEnumerable<MascotStep> Hop() =>
        [Step("stand", 140, drop: 1), .. Jump("armsUp", 2), Step("stand", 110, drop: 1), Step("stand", 0)];

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
            Step("waveRight", 250, props: Ball(10, 8)),
            Step("stand", 90, props: Ball(10, 11)),
            Step("stand", 90, props: Ball(10, 14)),
            Step("stand", 110, props: Ball(10, 16)),
            Step("stand", 160, props: Ball(10, 17)),
            Step("stand", 110, props: Ball(10, 16)),
            Step("stand", 90, props: Ball(10, 14)),
            Step("stand", 90, props: Ball(10, 11)),
        }),
        Step("waveRight", 300, props: Ball(10, 8)),
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
        Step("achoo", 140, drop: 1, props: Puff(-4, 7)),
        Step("stand", 260, props: Puff(-6, 8)),
        Step("stand", 200, props: Puff(-8, 9)),
        Step("blink", 140),
        Step("stand", 0),
    ];

    /// <summary>The back of her hand to her brow, and a drop of sweat falls: the context is nearly full.</summary>
    public static IEnumerable<MascotStep> WipeBrow() =>
    [
        Step("waveRight", 250, props: new MascotProp("sweat", 11, 9)),
        Step("waveRight", 200, props: new MascotProp("sweat", 11, 8)),
        Step("stand", 200, props: new MascotProp("sweat", 11, 7)),
        Step("stand", 200, props: new MascotProp("sweat", 11, 5)),
        Step("stand", 0),
    ];

    /// <summary>A poke: she jumps, with a "!" over her head.</summary>
    public static IEnumerable<MascotStep> Startled() =>
        [.. Jump("armsUp", 3, drop => [Bang(drop)]), Step("stand", 450, props: Bang(0)), Step("blink", 140), Step("stand", 0)];

    /// <summary>Poked once too often: she turns her back, under a storm cloud, then comes round.</summary>
    public static IEnumerable<MascotStep> Grumpy() =>
    [
        .. Enumerable.Range(0, 6).SelectMany(_ => new[]
        {
            Step("back", 500, props: new MascotProp("cloud", 3, 13)),
            Step("back", 500, props: new MascotProp("cloud", 3, 14)),
        }),
        Step("lookLeft", 250),
        Step("stand", 300),
        Step("blink", 140),
        Step("stand", 0),
    ];

    /// <summary>A file dragged over the composer: arms up, bouncing, ready to catch it.</summary>
    public static IEnumerable<MascotStep> Excited() => [Step("armsUp", 220), Step("armsUp", 220, drop: -1)];

    // ---- Off the edge and back ----------------------------------------------------------------------------------------

    /// <summary>She looks down, flails, and drops behind the box, where she stays a moment.</summary>
    public static IEnumerable<MascotStep> TopplesOff(Random random) =>
    [
        Step("lookDown", 800),
        Step("armsUp", 130),
        Step("wave", 130),
        Step("armsUp", 130),
        Step("waveHigh", 130),
        .. Fall("armsUp", 0, MascotDirector.Behind),
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
    public static IEnumerable<MascotStep> Duck() => [Step("stand", 70, drop: 1), .. Fall("stand", 1, MascotDirector.Behind)];

    /// <summary>Set down from being carried: she falls to the edge and lands with a bob.</summary>
    public static IEnumerable<MascotStep> Land(double from) =>
        [.. from < 0 ? Fall("carried", from, 0) : [], Step("stand", 120, drop: 1), Step("stand", 0)];

    /// <summary>Set down below the edge: she falls behind the box, and a moment later climbs back up.</summary>
    public static IEnumerable<MascotStep> DroppedBehind(double from) =>
        [.. Fall("carried", Math.Max(0, from), MascotDirector.Behind), Step("carried", 900, drop: MascotDirector.Behind), .. ClimbUp()];

    // ---- Working --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A burst of work while Claude works, with whatever suits its tool, then a pause; with her coffee beside her
    /// through a long turn.
    /// </summary>
    public static IEnumerable<MascotStep> Work(MascotSituation situation, Random random, bool coffee)
    {
        var steps = situation.Tool switch
        {
            MascotTool.Agents => Juggle(Math.Clamp(situation.Agents, 1, 3)),
            MascotTool.Reading => Magnify(),
            MascotTool.Editing => Hammer(),
            MascotTool.Running => Terminal(random),
            MascotTool.Web => Globe(),
            _ when situation.Agents > 0 => Juggle(Math.Clamp(situation.Agents, 1, 3)),
            _ => Typing(random, situation.Planning),
        };
        return coffee ? steps.Select((step, i) => step with { Props = [.. step.Props ?? [], Mug(i)] }) : steps;
    }

    /// <summary>What she works with while Claude works, held still: the first step of <see cref="Work"/>.</summary>
    public static MascotStep StillWork(MascotSituation situation) => Work(situation, new Random(0), coffee: false).First();

    /// <summary>A burst of typing on her laptop, or writing on her clipboard in plan mode, then a pause.</summary>
    public static IEnumerable<MascotStep> Typing(Random random, bool planning = false)
    {
        var on = planning ? Clipboard : Laptop;
        var keys = 10 + random.Next(8);
        for (var i = 0; i < keys; i++)
        {
            yield return Step(i % 2 == 0 ? "typeLeft" : "typeRight", 220, props: on);
        }
        yield return Step("stand", 1200 + random.Next(1800), props: on);
        yield return Step("blink", 140, props: on);
    }

    /// <summary>Reading: a magnifying glass held out to her right, peering through it.</summary>
    public static IEnumerable<MascotStep> Magnify()
    {
        static MascotProp At(int bottom) => new("magnifier", 10, bottom);
        return
        [
            Step("lookRight", 450, props: At(4)),
            Step("lookRight", 450, props: At(5)),
            Step("blink", 160, props: At(4)),
            Step("lookRight", 450, props: At(5)),
            Step("lookRight", 450, props: At(4)),
            Step("stand", 600, props: At(4)),
        ];
    }

    /// <summary>Editing: hammering at the edge, then a rest.</summary>
    public static IEnumerable<MascotStep> Hammer()
    {
        var down = new MascotProp("hammer-down", 11, 0);
        return
        [
            .. Enumerable.Range(0, 4).SelectMany(_ => new[]
            {
                Step("waveRight", 240, props: new MascotProp("hammer-up", 10, 4)),
                Step("typeRight", 120, props: [down, Puff(15, 0)]),
                Step("stand", 200, props: down),
            }),
            Step("stand", 900, props: down),
            Step("blink", 140, props: down),
        ];
    }

    /// <summary>Running a command: typing at a little terminal, its output scrolling.</summary>
    public static IEnumerable<MascotStep> Terminal(Random random)
    {
        var keys = 10 + random.Next(8);
        for (var i = 0; i < keys; i++)
        {
            yield return Step(i % 2 == 0 ? "typeLeft" : "typeRight", 220, props: new MascotProp($"terminal-{i % 3}", 1, 0));
        }
        yield return Step("stand", 500, props: new MascotProp("terminal-0", 1, 0));
        yield return Step("stand", 500, props: new MascotProp("terminal-1", 1, 0));
        yield return Step("blink", 140, props: new MascotProp("terminal-0", 1, 0));
    }

    /// <summary>The web: a globe on her left, which she spins and watches.</summary>
    public static IEnumerable<MascotStep> Globe() =>
    [
        .. Enumerable.Range(0, 9).Select(i => Step("lookLeft", 260, props: new MascotProp($"globe-{i % 3}", -6, 0))),
        Step("blink", 140, props: new MascotProp("globe-0", -6, 0)),
        Step("stand", 700, props: new MascotProp("globe-0", -6, 0)),
    ];

    /// <summary>Subagents: juggling a ball for each one running, round over her head.</summary>
    public static IEnumerable<MascotStep> Juggle(int balls)
    {
        for (var step = 0; step < 16; step++)
        {
            var props = Enumerable.Range(0, balls).Select(ball =>
            {
                var angle = (step * 45 + ball * 360.0 / balls) * Math.PI / 180;
                return Ball(5 + (int)Math.Round(4.5 * Math.Cos(angle)), 15 + (int)Math.Round(2.5 * Math.Sin(angle)));
            });
            yield return Step(step % 2 == 0 ? "wave" : "waveRight", 130, props: [.. props]);
        }
    }

    /// <summary>A long turn: she ducks behind the box and comes back up with a coffee, which she sets down beside her.</summary>
    public static IEnumerable<MascotStep> FetchCoffee() =>
    [
        .. Duck(),
        Step("stand", 1000, drop: MascotDirector.Behind),
        Step("stand", 60, drop: 10),
        Step("stand", 60, drop: 6),
        Step("stand", 60, drop: 3),
        Step("stand", 80, drop: 1),
        Step("lookRight", 700, props: Mug(0)),
        Step("blink", 140, props: Mug(1)),
    ];

    /// <summary>Compacting: sweeping the edge with a broom, dust flying.</summary>
    public static IEnumerable<MascotStep> Sweep() =>
    [
        .. Enumerable.Range(0, 3).SelectMany(_ => new[]
        {
            Step("lookDown", 260, props: new MascotProp("broom", 9, 0)),
            Step("lookDown", 260, props: [new MascotProp("broom", 12, 0), Puff(15, 0)]),
        }),
        Step("blink", 140, props: new MascotProp("broom", 10, 0)),
    ];

    // ---- Reacting -------------------------------------------------------------------------------------------------------

    /// <summary>Another tab needs the user: she turns to the sidebar, waves toward it with an arrow, and says so.</summary>
    public static IEnumerable<MascotStep> PointToSidebar(string say) =>
    [
        .. Enumerable.Range(0, 3).SelectMany(_ => new[]
        {
            Saying("waveLookLeft", 300, say, Arrow),
            Saying("waveHighLookLeft", 300, say, Arrow),
        }),
        Saying("lookLeft", 1400, say, Arrow),
        Step("stand", 0),
    ];

    /// <summary>Pointing at the sidebar, held still while motion is reduced.</summary>
    public static MascotStep StillPoint(string say) => Saying("waveLookLeft", 0, say, Arrow);

    /// <summary>A tip, held up in a bubble for a few seconds.</summary>
    public static IEnumerable<MascotStep> Tip(string say) =>
        [Saying("waveRight", 400, say), Saying("stand", 4500, say), Saying("blink", 140, say), Step("stand", 0)];

    /// <summary>A turn failed: she's dizzy, stars going round her head, swaying, then shakes it off.</summary>
    public static IEnumerable<MascotStep> Dizzy()
    {
        for (var step = 0; step < 16; step++)
        {
            var stars = Enumerable.Range(0, 3).Select(star =>
            {
                var angle = (step * 45 + star * 120) * Math.PI / 180;
                return new MascotProp("star", 4 + (int)Math.Round(4.5 * Math.Cos(angle)), 12 + (int)Math.Round(1.5 * Math.Sin(angle)));
            });
            var sway = step % 8 == 0 ? -1 : step % 8 == 4 ? 1 : 0;
            yield return Step(step % 4 < 2 ? "blink" : "lookDown", 120, move: sway, drop: step is > 3 and < 12 ? 1 : 0, props: [.. stars]);
        }
        yield return Step("lookLeft", 120);
        yield return Step("lookRight", 120);
        yield return Step("lookLeft", 120);
        yield return Step("lookRight", 120);
        yield return Step("blink", 140);
        yield return Step("stand", 0);
    }

    /// <summary>All of Claude's tasks done: confetti bursts out of her as she hops.</summary>
    public static IEnumerable<MascotStep> Confetti()
    {
        (double X, double Y)[] throws =
            [(-14, 22), (-9, 28), (-4, 31), (4, 30), (9, 27), (14, 21), (-11, 18), (11, 19), (-6, 25), (6, 24), (-2, 33), (2, 29)];
        string[] colors = ["confetti-y", "confetti-r", "confetti-d", "confetti-v", "confetti-k"];
        int[] bounce = [1, 0, -1, -2, -2, -1, 0, 1, 0, -1, -2, -2, -1, 0];
        for (var step = 0; step < 30; step++)
        {
            var t = step * 0.06;
            var pieces = throws
                .Select((v, i) => new MascotProp(colors[i % colors.Length], 5 + (int)Math.Round(v.X * t), 13 + (int)Math.Round(v.Y * t - 30 * t * t)))
                .Where(p => p.Bottom > -2);
            var drop = step < bounce.Length ? bounce[step] : 0;
            yield return Step(drop < 0 ? "armsUp" : "stand", 60, drop: drop, props: [.. pieces]);
        }
        yield return Step("stand", 0);
    }

    /// <summary>Every changed file reviewed: she stamps the edge, and leaves a check mark there.</summary>
    public static IEnumerable<MascotStep> Stamp() =>
    [
        Step("wave", 350, props: new MascotProp("stamp", -3, 8)),
        Step("typeLeft", 120, props: [new MascotProp("stamp", -5, 0), Puff(-9, 0)]),
        Step("lookLeft", 1600, props: new MascotProp("check", -7, 0)),
        Step("blink", 140, props: new MascotProp("check", -7, 0)),
        Step("stand", 0),
    ];

    /// <summary>A message sent: she throws a paper plane up into the conversation.</summary>
    public static IEnumerable<MascotStep> PaperPlane() =>
    [
        Step("waveRight", 250, props: new MascotProp("plane", 11, 8)),
        .. new (int X, int Bottom)[] { (8, 12), (4, 16), (-1, 20), (-7, 24), (-14, 27), (-22, 30), (-31, 32) }
            .Select((at, i) => Step(i < 2 ? "armsUp" : "stand", 60, props: new MascotProp("plane", at.X, at.Bottom))),
        Step("stand", 0),
    ];

    /// <summary>A file attached: it drops from above, she catches it, and puts it in the box.</summary>
    public static IEnumerable<MascotStep> CatchFile()
    {
        static MascotProp File(int bottom) => new("file", 4, bottom);
        return
        [
            Step("armsUp", 60, props: File(24)),
            Step("armsUp", 60, props: File(20)),
            Step("armsUp", 60, props: File(16)),
            Step("armsUp", 220, drop: 1, props: File(12)),
            Step("armsUp", 300, props: File(13)),
            .. new[] { 9, 5, 1, -3, -7 }.Select(bottom => Step("stand", 70, props: File(bottom))),
            Step("blink", 140),
            Step("stand", 0),
        ];
    }

    /// <summary>A huge paste: she staggers under a heavy crate, drops it in the box, and wipes her brow.</summary>
    public static IEnumerable<MascotStep> HeavyCrate()
    {
        static MascotProp Crate(int bottom) => new("crate", 2, bottom);
        return
        [
            Step("armsUp", 300, props: Crate(13)),
            Step("armsUp", 220, move: -1, drop: 1, props: Crate(12)),
            Step("armsUp", 220, move: 1, props: Crate(13)),
            Step("armsUp", 220, move: 1, drop: 1, props: Crate(12)),
            Step("armsUp", 220, move: -1, props: Crate(13)),
            Step("armsUp", 300, drop: 1, props: Crate(12)),
            .. new[] { 9, 5, 0 }.Select(bottom => Step("stand", 60, props: Crate(bottom))),
            Step("stand", 60, props: [Crate(-4), Puff(1, 0)]),
            Step("stand", 200, props: Puff(0, 1)),
            .. WipeBrow(),
        ];
    }

    /// <summary>Typed her name: she waves back and blows a heart.</summary>
    public static IEnumerable<MascotStep> WaveBack() =>
        [Step("wave", 280), Step("waveHigh", 280), Step("wave", 280), Step("waveHigh", 280), .. BlowHeart()];

    // ---- Waiting and resting ----------------------------------------------------------------------------------------------

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
