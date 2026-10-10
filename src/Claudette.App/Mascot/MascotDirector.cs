using Claudette.App.Services;
using Claudette.App.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Claudette.App.Mascot;

/// <summary>What the tab she stands on is doing, which she reacts to (DESIGN.md §5, "Claudette on the composer").</summary>
public enum MascotMood
{
    Idle,

    /// <summary>Claude is working: she types on her laptop.</summary>
    Working,

    /// <summary>A prompt waits on the user: she waves now and then.</summary>
    Waiting,

    /// <summary>A usage limit holds the task: she dozes by an hourglass.</summary>
    Resting,
}

/// <summary>A prop drawn with her: <see cref="X"/> cells from her left, its bottom row <see cref="Bottom"/> cells above the edge.</summary>
public sealed record MascotProp(string Name, int X, int Bottom);

/// <summary>
/// How she looks now. <see cref="X"/> is her left edge, in cells from the composer box's left; <see cref="Drop"/> is how
/// many cells lower she is than standing on its top edge, behind the box, or higher when it's negative (a hop).
/// </summary>
public sealed record MascotFrame(string Pose, int X, int Drop, IReadOnlyList<MascotProp> Props)
{
    /// <summary>All of her is behind the box.</summary>
    public bool IsHidden => Drop >= MascotArt.HeightOf(Pose);
}

/// <summary>
/// The stretch of the box's top edge she may stand on, in cells from its left: her left edge goes from
/// <see cref="Left"/> to <see cref="Right"/> less her width.
/// </summary>
public readonly record struct MascotRoom(int Left, int Right)
{
    public int Width => Right - Left;

    public int MaxX => Math.Max(Left, Right - MascotArt.Width);

    public int Clamp(int x) => Math.Clamp(x, Left, MaxX);
}

/// <summary>One step of what she's doing: a pose held for a while, a step sideways, how far down she is.</summary>
internal sealed record MascotStep(string Pose, TimeSpan Duration, int Move = 0, int Drop = 0, IReadOnlyList<MascotProp>? Props = null);

/// <summary>
/// Claudette on the composer (DESIGN.md §5): what she does, step by step. She stands about and blinks, and every 30 to
/// 90 seconds does something (walks, looks around, leans on the edge, stretches, waves, hops, and now and then falls
/// off behind the box and climbs back up). She reacts to the tab she's on (<see cref="MascotMood"/>), ducks behind the
/// box while something sits on it, and stands still while motion is reduced. Timed by the injected clock; between
/// steps nothing ticks.
/// </summary>
public sealed partial class MascotDirector : ObservableObject, IDisposable
{
    /// <summary>The shortest and longest wait between the things she does.</summary>
    public static readonly TimeSpan ShortestCalm = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan LongestCalm = TimeSpan.FromSeconds(90);

    /// <summary>She naps once nothing has happened for this long.</summary>
    public static readonly TimeSpan NapAfter = TimeSpan.FromMinutes(5);

    /// <summary>Falling off the edge on her own stays a surprise: at most this often.</summary>
    public static readonly TimeSpan FallsAtMostEvery = TimeSpan.FromMinutes(3);

    /// <summary>A second poke this soon after the first tips her off the edge.</summary>
    public static readonly TimeSpan DoublePoke = TimeSpan.FromSeconds(1.5);

    /// <summary>While a prompt waits, she waves this often.</summary>
    public static readonly TimeSpan WaveEvery = TimeSpan.FromSeconds(15);

    /// <summary>A <see cref="MascotFrame.Drop"/> that has all of her behind the box, in any pose.</summary>
    public const int Behind = 16;

    /// <summary>The least room she stands in: her width and a cell either side. With less she ducks.</summary>
    public const int LeastRoom = MascotArt.Width + 2;

    private readonly TimeProvider _time;
    private readonly Random _random;
    private readonly UiTimeout _next;
    private readonly Queue<MascotStep> _steps = new();
    private readonly HashSet<object> _views = [];
    private Activity _activity;
    private MascotRoom? _room;
    private MascotMood _mood;
    private bool _still;
    private bool _placed;
    private bool _cheerPending;
    private DateTimeOffset _nextAntic;
    private DateTimeOffset _lastActivity;
    private DateTimeOffset _lastFall = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPoke = DateTimeOffset.MinValue;
    private DateTimeOffset _lastWave = DateTimeOffset.MinValue;

    public MascotDirector(TimeProvider time, IUiDispatcher dispatcher, Random random)
    {
        _time = time;
        _random = random;
        _next = new UiTimeout(time, dispatcher);
        _lastActivity = _nextAntic = Now;
    }

    private enum Activity
    {
        /// <summary>Standing about, blinking now and then.</summary>
        Idle,

        /// <summary>One of the things she does every so often.</summary>
        Antic,

        /// <summary>What the tab's mood has her doing: typing, waving, dozing.</summary>
        Mood,

        /// <summary>Asleep on the edge after a long quiet spell.</summary>
        Nap,

        /// <summary>Falling off behind the box, or climbing up from behind it: only losing her room stops it.</summary>
        Tumble,

        /// <summary>Behind the box while something sits on it.</summary>
        Ducked,
    }

    /// <summary>How she looks now.</summary>
    [ObservableProperty]
    public partial MascotFrame Frame { get; private set; } = new("stand", 0, Behind, []);

    /// <summary>What the tab she's on is doing, as she was last told.</summary>
    internal MascotMood Mood => _mood;

    /// <summary>Whether she's on screen anywhere: a view showing her says so. Out of sight, nothing ticks.</summary>
    public bool IsShown => _views.Count > 0;

    private DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>A view shows her, or stops: she moves while any view does.</summary>
    public void SetShown(object view, bool shown)
    {
        var was = IsShown;
        if (shown)
        {
            _views.Add(view);
        }
        else
        {
            _views.Remove(view);
        }
        if (IsShown == was)
        {
            return;
        }
        if (!IsShown)
        {
            _next.Cancel();
            _steps.Clear();
            return;
        }
        _lastActivity = Now;
        Restart();
    }

    /// <summary>
    /// Where on the box's top edge she may stand, or null when nothing is free (a card sits on the box), which has her
    /// duck behind it until there's room again.
    /// </summary>
    public void SetRoom(MascotRoom? room)
    {
        room = room is { Width: >= LeastRoom } ? room : null;
        if (_room == room)
        {
            return;
        }
        var had = _room is not null;
        _room = room;
        if (room is not { } free)
        {
            Duck();
            return;
        }
        if (_placed && free.Clamp(Frame.X) != Frame.X)
        {
            // The box changed under her, so she doesn't walk there.
            Frame = Frame with { X = free.Clamp(Frame.X) };
        }
        if (!had)
        {
            Restart();
        }
    }

    /// <summary>The tab she's on: she types while Claude works, waves while a prompt waits, dozes at a usage limit.</summary>
    public void SetMood(MascotMood mood)
    {
        if (_mood == mood)
        {
            return;
        }
        _mood = mood;
        _lastActivity = Now;
        if (mood != MascotMood.Idle)
        {
            _cheerPending = false;
        }
        if (mood == MascotMood.Waiting)
        {
            _lastWave = DateTimeOffset.MinValue;
        }
        if (_activity is not (Activity.Tumble or Activity.Ducked))
        {
            Restart();
        }
    }

    /// <summary>Motion is reduced (DESIGN.md §3, "Accessibility"): she stands still in the pose for the tab's mood.</summary>
    public void SetStill(bool still)
    {
        if (_still == still)
        {
            return;
        }
        _still = still;
        Restart();
    }

    /// <summary>The tab's turn finished: she hops, once she's free to.</summary>
    public void TurnFinished()
    {
        _cheerPending = true;
        if (_mood == MascotMood.Idle && _activity is Activity.Idle or Activity.Nap)
        {
            Restart();
        }
    }

    /// <summary>The user did something, such as typing: it wakes her from a nap, and puts the next one off.</summary>
    public void Nudge()
    {
        _lastActivity = Now;
        if (_activity == Activity.Nap && _mood == MascotMood.Idle && IsShown && !_still)
        {
            _nextAntic = Now + Calm();
            Play(Activity.Idle, MascotAntics.WakeUp());
        }
    }

    /// <summary>A click on her: she jumps, startled. A second click straight after tips her off the edge.</summary>
    public void Poke()
    {
        if (!IsShown || _still || Frame.IsHidden || _activity is Activity.Tumble or Activity.Ducked)
        {
            return;
        }
        var now = Now;
        _lastActivity = now;
        var twice = now - _lastPoke <= DoublePoke;
        _lastPoke = twice ? DateTimeOffset.MinValue : now;
        if (twice)
        {
            _lastFall = now;
            Play(Activity.Tumble, [.. MascotAntics.TopplesOff(_random), .. MascotAntics.ClimbUp()]);
        }
        else
        {
            Play(Activity.Antic, MascotAntics.Startled());
        }
    }

    /// <summary>Drops what she's doing and works out what to do now.</summary>
    private void Restart()
    {
        _next.Cancel();
        _steps.Clear();
        if (!IsShown)
        {
            return;
        }
        if (_room is not { } room)
        {
            Duck();
            return;
        }
        if (_still)
        {
            Place(room, atRightEnd: true);
            _activity = Activity.Idle;
            Frame = StillFrame();
            return;
        }
        if (!_placed || Frame.IsHidden)
        {
            Place(room, atRightEnd: false);
            Play(Activity.Tumble, MascotAntics.ClimbUp());
            return;
        }
        if (Frame.Drop != 0)
        {
            // Back on her feet first, from a hop or leaning on the edge.
            Play(Activity.Idle, [MascotAntics.Settle(Frame.Drop)]);
            return;
        }
        PlanNext();
    }

    /// <summary>Her first place on the edge: anywhere, or at its right end when she won't be walking.</summary>
    private void Place(MascotRoom room, bool atRightEnd)
    {
        if (!_placed)
        {
            _placed = true;
            Frame = Frame with { X = atRightEnd ? room.MaxX : _random.Next(room.Left, room.MaxX + 1) };
        }
    }

    private void Duck()
    {
        _next.Cancel();
        _steps.Clear();
        _activity = Activity.Ducked;
        if (Frame.IsHidden || _still || !IsShown)
        {
            Frame = Frame with { Drop = Behind, Props = [] };
            return;
        }
        Play(Activity.Ducked, MascotAntics.Duck());
    }

    private void Play(Activity activity, IEnumerable<MascotStep> steps)
    {
        _next.Cancel();
        _steps.Clear();
        foreach (var step in steps)
        {
            _steps.Enqueue(step);
        }
        _activity = activity;
        Advance();
    }

    private void Advance()
    {
        if (!IsShown)
        {
            return;
        }
        if (_steps.TryDequeue(out var step))
        {
            var x = Frame.X + step.Move;
            Frame = new MascotFrame(step.Pose, _room is { } room ? room.Clamp(x) : x, step.Drop, step.Props ?? []);
            _next.Restart(step.Duration, Advance);
            return;
        }
        // Done with something, or back up on the edge: a calm spell before the next thing.
        if (_activity is Activity.Antic or Activity.Tumble)
        {
            _nextAntic = Now + Calm();
        }
        PlanNext();
    }

    /// <summary>What she does once she's done with the last thing.</summary>
    private void PlanNext()
    {
        if (_room is not { } room)
        {
            Duck();
            return;
        }
        if (Frame.IsHidden)
        {
            Play(Activity.Tumble, MascotAntics.ClimbUp());
            return;
        }
        var now = Now;
        var napping = _activity == Activity.Nap;
        switch (_mood)
        {
            case MascotMood.Working:
                Play(Activity.Mood, MascotAntics.Typing(_random));
                return;
            case MascotMood.Waiting when now - _lastWave >= WaveEvery:
                _lastWave = now;
                Play(Activity.Mood, MascotAntics.Wave(4));
                return;
            case MascotMood.Waiting:
                Play(Activity.Mood, MascotAntics.StandAbout(Min(BlinkWait(), _lastWave + WaveEvery - now)));
                return;
            case MascotMood.Resting:
                Play(Activity.Nap, MascotAntics.Doze(_random, hourglass: true, settle: !napping));
                return;
        }
        if (_cheerPending)
        {
            _cheerPending = false;
            Play(Activity.Antic, MascotAntics.Hop());
            return;
        }
        if (now - _lastActivity >= NapAfter)
        {
            Play(Activity.Nap, MascotAntics.Doze(_random, hourglass: false, settle: !napping));
            return;
        }
        if (now >= _nextAntic)
        {
            PlayAntic(room, now);
            return;
        }
        Play(Activity.Idle, MascotAntics.StandAbout(Min(BlinkWait(), _nextAntic - now)));
    }

    /// <summary>Something she does every so often, picked by weight.</summary>
    private void PlayAntic(MascotRoom room, DateTimeOffset now)
    {
        var canFall = now - _lastFall >= FallsAtMostEvery;
        var walkTo = _random.Next(room.Left, room.MaxX + 1);
        // A walk across a wide window would take a while: at most 40 cells at a time.
        var walk = Math.Clamp(walkTo - Frame.X, -40, 40);
        (int Weight, bool Falls, Func<IEnumerable<MascotStep>> Steps)[] choices =
        [
            (3, false, () => MascotAntics.LookAround(_random)),
            (Math.Abs(walk) >= 3 ? 3 : 0, false, () => MascotAntics.Walk(walk)),
            (2, false, () => MascotAntics.Lean(_random)),
            (1, false, MascotAntics.Stretch),
            (1, false, () => MascotAntics.Wave(3)),
            (1, false, MascotAntics.Hop),
            (canFall ? 1 : 0, true, () => MascotAntics.TopplesOff(_random).Concat(MascotAntics.ClimbUp())),
        ];
        var pick = _random.Next(choices.Sum(c => c.Weight));
        foreach (var (weight, falls, steps) in choices)
        {
            if (pick < weight)
            {
                if (falls)
                {
                    _lastFall = now;
                }
                Play(falls ? Activity.Tumble : Activity.Antic, steps());
                return;
            }
            pick -= weight;
        }
    }

    /// <summary>Her pose while motion is reduced: the first frame of what the tab's mood has her doing.</summary>
    private MascotFrame StillFrame() => _mood switch
    {
        MascotMood.Working => new("typeLeft", Frame.X, 0, [MascotAntics.Laptop]),
        MascotMood.Waiting => new("wave", Frame.X, 0, []),
        MascotMood.Resting => new("leanBlink", Frame.X, 2, [MascotAntics.SmallZ, MascotAntics.Hourglass(0)]),
        _ => new("stand", Frame.X, 0, []),
    };

    private TimeSpan Calm() => ShortestCalm + (LongestCalm - ShortestCalm) * _random.NextDouble();

    /// <summary>Three to six seconds between blinks.</summary>
    private TimeSpan BlinkWait() => TimeSpan.FromSeconds(3 + 3 * _random.NextDouble());

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    public void Dispose() => _next.Dispose();
}
