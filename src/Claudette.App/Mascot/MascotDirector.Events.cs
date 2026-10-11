namespace Claudette.App.Mascot;

/// <summary>
/// Claudette on the composer's reactions to what happens (DESIGN.md §5): a turn finishing or failing, Claude's tasks
/// all done, every changed file reviewed, a message sent, a file attached, a huge paste, her name typed, being poked,
/// a file dragged over the composer, and being picked up and carried.
/// </summary>
public sealed partial class MascotDirector
{
    /// <summary>A second poke this soon after the first tips her off the edge.</summary>
    public static readonly TimeSpan DoublePoke = TimeSpan.FromSeconds(1.5);

    /// <summary>Poked this many times within <see cref="GrumpyWithin"/>, she turns her back.</summary>
    public const int GrumpyPokes = 10;

    public static readonly TimeSpan GrumpyWithin = TimeSpan.FromMinutes(1);

    /// <summary>Reactions waiting for her to be back on the edge.</summary>
    private readonly Queue<Func<IEnumerable<MascotStep>>> _pending = new();

    private readonly List<DateTimeOffset> _pokes = [];
    private DateTimeOffset _lastPoke = DateTimeOffset.MinValue;
    private bool _grumpy;
    private bool _dragHover;

    /// <summary>The tab's turn finished: she hops, once she's free to.</summary>
    public void TurnFinished()
    {
        _cheerPending = true;
        if (_situation.Mood == MascotMood.Idle && _activity is Activity.Idle or Activity.Nap)
        {
            Restart();
        }
    }

    /// <summary>The tab's turn failed: she's dizzy for a moment.</summary>
    public void TurnFailed() => React(MascotAntics.Dizzy);

    /// <summary>Every one of Claude's tasks is done: confetti.</summary>
    public void AllTasksDone() => React(MascotAntics.Confetti);

    /// <summary>Every file the tab changed is reviewed: she stamps it.</summary>
    public void AllReviewed() => React(MascotAntics.Stamp);

    /// <summary>The user sent a message: she throws a paper plane up into the conversation.</summary>
    public void MessageSent()
    {
        _lastActivity = Now;
        React(MascotAntics.PaperPlane);
    }

    /// <summary>A file or image was attached: she catches it.</summary>
    public void Caught() => React(MascotAntics.CatchFile);

    /// <summary>A paste too large for the box was kept as an attachment: she staggers under it.</summary>
    public void HeavyPaste() => React(MascotAntics.HeavyCrate);

    /// <summary>The user typed her name: she waves back.</summary>
    public void Greeted() => React(MascotAntics.WaveBack);

    /// <summary>A file is being dragged over the composer, or no longer is: she's ready to catch it.</summary>
    public void SetDragHover(bool hovering)
    {
        if (_dragHover == hovering)
        {
            return;
        }
        _dragHover = hovering;
        if (_activity is not (Activity.Tumble or Activity.Ducked or Activity.Carried or Activity.Reaction))
        {
            Restart();
        }
    }

    /// <summary>
    /// A click on her: she jumps, startled. A second click straight after tips her off the edge; poked too often, she
    /// turns her back for a while, and pokes do nothing till she comes round.
    /// </summary>
    public void Poke()
    {
        if (!IsShown || _still || _grumpy || Frame.IsBehind || _activity is Activity.Tumble or Activity.Ducked or Activity.Carried)
        {
            return;
        }
        var now = Now;
        _lastActivity = now;
        _pokes.RemoveAll(poke => now - poke > GrumpyWithin);
        _pokes.Add(now);
        if (_pokes.Count >= GrumpyPokes)
        {
            _pokes.Clear();
            _lastPoke = DateTimeOffset.MinValue;
            Play(Activity.Reaction, MascotAntics.Grumpy(), grumpy: true);
            return;
        }
        var twice = now - _lastPoke <= DoublePoke;
        _lastPoke = twice ? DateTimeOffset.MinValue : now;
        if (twice)
        {
            _lastFall = now;
            Play(Activity.Tumble, [.. MascotAntics.TopplesOff(_random), .. MascotAntics.ClimbUp()]);
        }
        else
        {
            Play(Activity.Reaction, MascotAntics.Startled());
        }
    }

    /// <summary>The user picked her up: she hangs from their pointer until they let go.</summary>
    public void BeginCarry()
    {
        if (!IsShown || _still || _room is null || Frame.IsBehind || _activity is Activity.Carried or Activity.Tumble or Activity.Ducked)
        {
            return;
        }
        _next.Cancel();
        _steps.Clear();
        _activity = Activity.Carried;
        _grumpy = false;
        _showing = null;
        Frame = new MascotFrame("carried", Frame.X, Frame.Drop, []) { Hat = Frame.Hat };
    }

    /// <summary>Carried to <paramref name="x"/> cells along the box, <paramref name="drop"/> cells below its edge (negative is above).</summary>
    public void Carry(int x, double drop)
    {
        if (_activity == Activity.Carried)
        {
            Frame = Frame with { X = x, Drop = drop };
        }
    }

    /// <summary>
    /// Let go: above the edge she falls onto it where she is, and walks home if that's outside her patch; below it, she
    /// falls behind the box and climbs back up.
    /// </summary>
    public void Release()
    {
        if (_activity != Activity.Carried)
        {
            return;
        }
        _lastActivity = Now;
        _activity = Activity.Idle;
        if (_room is not { } room)
        {
            Duck();
            return;
        }
        var drop = Frame.Drop;
        if (drop > 1)
        {
            Play(Activity.Tumble, MascotAntics.DroppedBehind(drop));
            return;
        }
        var x = room.ClampToEdge(Frame.X);
        Frame = Frame with { X = x };
        var steps = MascotAntics.Land(drop);
        if (!room.InPatch(x))
        {
            // All the way home, however far she was carried.
            steps = [.. steps, new MascotStep("stand", TimeSpan.FromMilliseconds(600)), .. MascotAntics.Walk(room.HomeX - x)];
        }
        Play(Activity.Antic, steps);
    }

    /// <summary>Plays a reaction now, or once she's back on the edge; nothing while motion is reduced or she's out of sight.</summary>
    private void React(Func<IEnumerable<MascotStep>> steps)
    {
        if (_still || !IsShown)
        {
            return;
        }
        if (_room is null || Frame.IsBehind || _activity is Activity.Tumble or Activity.Ducked or Activity.Carried)
        {
            if (_pending.Count < 3)
            {
                _pending.Enqueue(steps);
            }
            return;
        }
        _lastActivity = Now;
        Play(Activity.Reaction, steps());
    }
}
