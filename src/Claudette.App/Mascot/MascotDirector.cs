using Claudette.App.Services;
using Claudette.App.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Claudette.App.Mascot;

/// <summary>
/// Claudette on the composer (DESIGN.md §5): what she does, step by step. She stands about and blinks, and every so
/// often (<see cref="MascotSpell"/>) does something: walks, looks around, leans or sits on the edge, dances, juggles and
/// the rest, and now and then falls off behind the box and climbs back up. Her walks tend to take her back home, over
/// the Send button. She reacts to the tab she's on (<see cref="MascotSituation"/>) and to what happens in it (the
/// one-off reactions in <c>MascotDirector.Events.cs</c>), ducks behind the box while something sits on it, and stands
/// still while motion is reduced. Timed by the injected clock; between steps nothing ticks.
/// </summary>
public sealed partial class MascotDirector : ObservableObject, IDisposable
{
    /// <summary>She naps once nothing has happened for this long; sooner late at night.</summary>
    public static readonly TimeSpan NapAfter = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan NapAfterAtNight = TimeSpan.FromMinutes(3);

    /// <summary>Falling off the edge on her own stays a surprise: at most this often.</summary>
    public static readonly TimeSpan FallsAtMostEvery = TimeSpan.FromMinutes(3);

    /// <summary>While a prompt waits, she waves this often.</summary>
    public static readonly TimeSpan WaveEvery = TimeSpan.FromSeconds(15);

    /// <summary>While another tab waits on the user, she points it out this often.</summary>
    public static readonly TimeSpan PointEvery = TimeSpan.FromSeconds(20);

    /// <summary>A turn running this long, she fetches a coffee.</summary>
    public static readonly TimeSpan CoffeeAfter = TimeSpan.FromMinutes(3);

    /// <summary>With the context nearly full, she wipes her brow this often while Claude works.</summary>
    public static readonly TimeSpan SweatEvery = TimeSpan.FromSeconds(25);

    /// <summary>A tip at most this often, and not while the user is typing.</summary>
    public static readonly TimeSpan TipsAtMostEvery = TimeSpan.FromMinutes(10);

    /// <summary>A <see cref="MascotFrame.Drop"/> that has all of her behind the box, in any pose.</summary>
    public const int Behind = 16;

    /// <summary>The least room she stands in: her width and a cell either side. With less she ducks.</summary>
    public const int LeastRoom = MascotArt.Width + 2;

    private readonly TimeProvider _time;
    private readonly Random _random;
    private readonly IMascotLines? _lines;
    private readonly UiTimeout _next;
    private readonly Queue<MascotStep> _steps = new();
    private readonly HashSet<object> _views = [];
    private Activity _activity;
    private MascotStep? _showing;
    private MascotRoom? _room;
    private MascotSituation _situation = MascotSituation.Quiet;
    private MascotSpell _spell = MascotSpell.Lively;
    private bool _still;
    private bool _tips = true;
    private bool _placed;
    private bool _cheerPending;
    private bool _coffee;
    private int _gaze;
    private DateTimeOffset _nextAntic;
    private DateTimeOffset _lastActivity;
    private DateTimeOffset _lastTyped = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTip;
    private DateTimeOffset _lastFall = DateTimeOffset.MinValue;
    private DateTimeOffset _lastWave = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPoint = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSweat = DateTimeOffset.MinValue;
    private DateTimeOffset _workingSince;

    /// <param name="lines">What she says: tips, and that another tab needs the user. Null says nothing.</param>
    public MascotDirector(TimeProvider time, IUiDispatcher dispatcher, Random random, IMascotLines? lines = null)
    {
        _time = time;
        _random = random;
        _lines = lines;
        _next = new UiTimeout(time, dispatcher);
        _lastActivity = _nextAntic = _lastTip = Now;
    }

    private enum Activity
    {
        /// <summary>Standing about, blinking now and then.</summary>
        Idle,

        /// <summary>One of the things she does every so often.</summary>
        Antic,

        /// <summary>What the tab has her doing: working, waving, dozing, sweeping.</summary>
        Mood,

        /// <summary>Asleep on the edge after a long quiet spell.</summary>
        Nap,

        /// <summary>A reaction to something that happened: what the tab does next waits for it to finish.</summary>
        Reaction,

        /// <summary>Falling off behind the box, or climbing up from behind it: only losing her room stops it.</summary>
        Tumble,

        /// <summary>Behind the box while something sits on it.</summary>
        Ducked,

        /// <summary>Picked up by the user, and carried.</summary>
        Carried,
    }

    /// <summary>How she looks now.</summary>
    [ObservableProperty]
    public partial MascotFrame Frame { get; private set; } = new("stand", 0, Behind, []);

    /// <summary>What the tab she's on is doing, as she was last told.</summary>
    internal MascotSituation Situation => _situation;

    internal MascotMood Mood => _situation.Mood;

    /// <summary>Where on the edge she may stand, as she was last told.</summary>
    internal MascotRoom? Room => _room;

    /// <summary>Whether she's on screen anywhere: a view showing her says so. Out of sight, nothing ticks.</summary>
    public bool IsShown => _views.Count > 0;

    /// <summary>The user has picked her up.</summary>
    public bool IsCarried => _activity == Activity.Carried;

    private DateTimeOffset Now => _time.GetUtcNow();

    private bool IsNight => MascotCalendar.IsNight(_time.GetLocalNow());

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
            if (_activity == Activity.Carried)
            {
                _activity = Activity.Idle;
            }
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
        if (_placed && _still)
        {
            Frame = Frame with { X = free.HomeX };
        }
        else if (_placed && _activity != Activity.Carried && Within(free, Frame.X) != Frame.X)
        {
            // The box changed under her, so she doesn't walk there.
            Frame = Frame with { X = Within(free, Frame.X) };
        }
        if (!had)
        {
            Restart();
        }
    }

    /// <summary>What the tab she's on is doing now.</summary>
    public void SetSituation(MascotSituation situation)
    {
        var was = _situation;
        if (situation == was)
        {
            return;
        }
        _situation = situation;
        if (situation.Mood != was.Mood)
        {
            _lastActivity = Now;
            if (situation.Mood != MascotMood.Idle)
            {
                _cheerPending = false;
            }
            if (situation.Mood == MascotMood.Waiting)
            {
                _lastWave = DateTimeOffset.MinValue;
            }
            if (situation.Mood == MascotMood.Working)
            {
                _workingSince = Now;
            }
            else
            {
                _coffee = false;
            }
        }
        if (situation.OthersWaiting > was.OthersWaiting)
        {
            // A tab newly waiting is pointed out straight away.
            _lastPoint = DateTimeOffset.MinValue;
        }
        if (_still)
        {
            Restart();
            return;
        }
        // A change of tool doesn't stop what she's doing: tools change many times a second, so her next burst of work
        // takes up the newest.
        var changed = situation.Mood != was.Mood
            || situation.Compacting != was.Compacting
            || situation.OthersWaiting > was.OthersWaiting
            || situation.Planning != was.Planning && _activity == Activity.Idle;
        if (changed && _activity is not (Activity.Tumble or Activity.Ducked or Activity.Carried or Activity.Reaction))
        {
            Restart();
        }
    }

    /// <summary>The tab she's on now does only <paramref name="mood"/>, nothing else she reacts to.</summary>
    public void SetMood(MascotMood mood) => SetSituation(_situation with { Mood = mood });

    /// <summary>Motion is reduced (DESIGN.md §3, "Accessibility"): she stands still at home, in the pose for what the tab is doing.</summary>
    public void SetStill(bool still)
    {
        if (_still == still)
        {
            return;
        }
        _still = still;
        Restart();
    }

    /// <summary>How long she waits between the things she does (Settings → Appearance).</summary>
    public void SetSpell(MascotSpell spell)
    {
        _spell = spell;
        if (_nextAntic > Now + spell.Longest)
        {
            _nextAntic = Now + Calm();
        }
    }

    /// <summary>Whether she shares a tip now and then (Settings → Appearance).</summary>
    public void SetTips(bool tips) => _tips = tips;

    /// <summary>Which way the pointer is from her: she looks that way while she stands about.</summary>
    public void SetGaze(int direction)
    {
        direction = Math.Sign(direction);
        if (direction == _gaze)
        {
            return;
        }
        _gaze = direction;
        if (!_still && _activity != Activity.Carried && _showing is { Pose: "stand" })
        {
            Frame = Frame with { Pose = Gazing("stand") };
        }
    }

    /// <summary>The user did something, such as typing: it wakes her from a nap, and puts the next one off.</summary>
    public void Nudge()
    {
        _lastActivity = _lastTyped = Now;
        if (_activity == Activity.Nap && _situation.Mood == MascotMood.Idle && IsShown && !_still)
        {
            _nextAntic = Now + Calm();
            Play(Activity.Idle, MascotAntics.WakeUp());
        }
    }

    /// <summary>Drops what she's doing and works out what to do now.</summary>
    private void Restart()
    {
        _next.Cancel();
        _steps.Clear();
        if (!IsShown || _activity == Activity.Carried)
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
            // At home, where she won't be walking from.
            _placed = true;
            _activity = Activity.Idle;
            _showing = null;
            Frame = StillFrame(room);
            return;
        }
        if (!_placed || Frame.IsBehind)
        {
            Place(room);
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

    /// <summary>Her first place on the edge: home.</summary>
    private void Place(MascotRoom room)
    {
        if (!_placed)
        {
            _placed = true;
            Frame = Frame with { X = room.HomeX };
        }
    }

    private void Duck()
    {
        _next.Cancel();
        _steps.Clear();
        _activity = Activity.Ducked;
        if (Frame.IsBehind || _still || !IsShown)
        {
            _showing = null;
            Frame = Frame with { Drop = Behind, Props = [], Say = null };
            return;
        }
        Play(Activity.Ducked, MascotAntics.Duck());
    }

    private void Play(Activity activity, IEnumerable<MascotStep> steps, bool grumpy = false)
    {
        _next.Cancel();
        _steps.Clear();
        foreach (var step in steps)
        {
            _steps.Enqueue(step);
        }
        _activity = activity;
        _grumpy = grumpy;
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
            Show(step);
            _next.Restart(step.Duration, Advance);
            return;
        }
        // Done with something, or back up on the edge: a calm spell before the next thing.
        if (_activity is Activity.Antic or Activity.Tumble or Activity.Reaction)
        {
            _nextAntic = Now + Calm();
        }
        _grumpy = false;
        PlanNext();
    }

    /// <summary>Her place on <paramref name="room"/>: in her patch, or on the free edge while she's been set down outside it.</summary>
    private int Within(MascotRoom room, int x) => room.InPatch(Frame.X) ? room.Clamp(x) : room.ClampToEdge(x);

    /// <summary>Steps she's told to take, for the README's animation and tests: a reaction, which nothing interrupts.</summary>
    internal void Perform(IEnumerable<MascotStep> steps) => Play(Activity.Reaction, steps);

    private void Show(MascotStep step)
    {
        _showing = step;
        var x = Frame.X + step.Move;
        Frame = new MascotFrame(Gazing(step.Pose), _room is { } room ? Within(room, x) : x, step.Drop, step.Props ?? [])
        {
            Hat = MascotCalendar.HatAt(_time.GetLocalNow()),
            Say = step.Say,
        };
    }

    /// <summary>Standing about, she looks the way the pointer is.</summary>
    private string Gazing(string pose) => pose == "stand" && !_still && _gaze != 0 ? _gaze < 0 ? "lookLeft" : "lookRight" : pose;

    /// <summary>What she does once she's done with the last thing.</summary>
    private void PlanNext()
    {
        if (_room is not { } room)
        {
            Duck();
            return;
        }
        if (Frame.IsBehind)
        {
            Play(Activity.Tumble, MascotAntics.ClimbUp());
            return;
        }
        if (_pending.TryDequeue(out var reaction))
        {
            Play(Activity.Reaction, reaction());
            return;
        }
        var now = Now;
        var napping = _activity == Activity.Nap;
        var situation = _situation;
        if (_dragHover)
        {
            Play(Activity.Mood, MascotAntics.Excited());
            return;
        }
        if (situation.Compacting)
        {
            Play(Activity.Mood, MascotAntics.Sweep());
            return;
        }
        if (situation.OthersWaiting > 0 && situation.Mood != MascotMood.Waiting && _lines is { } lines && now - _lastPoint >= PointEvery)
        {
            _lastPoint = now;
            Play(Activity.Antic, MascotAntics.PointToSidebar(lines.OthersWaiting(situation.OthersWaiting)));
            return;
        }
        switch (situation.Mood)
        {
            case MascotMood.Working when !_coffee && now - _workingSince >= CoffeeAfter:
                _coffee = true;
                Play(Activity.Mood, MascotAntics.FetchCoffee());
                return;
            case MascotMood.Working when situation.ContextFull && now - _lastSweat >= SweatEvery:
                _lastSweat = now;
                Play(Activity.Mood, MascotAntics.WipeBrow());
                return;
            case MascotMood.Working:
                Play(Activity.Mood, MascotAntics.Work(situation, _random, _coffee));
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
        if (now - _lastActivity >= (IsNight ? NapAfterAtNight : NapAfter))
        {
            Play(Activity.Nap, MascotAntics.Doze(_random, hourglass: false, settle: !napping));
            return;
        }
        if (now >= _nextAntic)
        {
            PlayAntic(room, now);
            return;
        }
        Play(Activity.Idle, MascotAntics.StandAbout(Min(BlinkWait(), _nextAntic - now), situation.Planning));
    }

    /// <summary>Something she does every so often, picked by weight.</summary>
    private void PlayAntic(MascotRoom room, DateTimeOffset now)
    {
        var canFall = now - _lastFall >= FallsAtMostEvery;
        var tip = _tips && _lines is { } lines && now - _lastTip >= TipsAtMostEvery && now - _lastTyped >= TimeSpan.FromSeconds(30)
            ? lines.Tip(_random)
            : null;
        // A walk across a wide window would take a while: at most 40 cells at a time. Away from home, a walk is more
        // likely to take her back there than further off, and set down outside her patch, she's soon on her way back.
        var wander = Math.Clamp(_random.Next(room.Left, room.MaxX + 1) - Frame.X, -40, 40);
        var home = Math.Clamp(room.HomeX - Frame.X, -40, 40);
        (int Weight, Activity Activity, Func<IEnumerable<MascotStep>> Steps)[] choices =
        [
            (3, Activity.Antic, () => MascotAntics.LookAround(_random)),
            (Math.Abs(wander) >= 3 ? 2 : 0, Activity.Antic, () => MascotAntics.Walk(wander)),
            (Math.Abs(home) >= 3 ? room.InPatch(Frame.X) ? 5 : 40 : 0, Activity.Antic, () => MascotAntics.Walk(home)),
            (2, Activity.Antic, () => MascotAntics.Lean(_random)),
            (2, Activity.Antic, () => MascotAntics.Sit(_random)),
            (1, Activity.Antic, MascotAntics.Stretch),
            (1, Activity.Antic, () => MascotAntics.Wave(3)),
            (1, Activity.Antic, MascotAntics.Hop),
            (2, Activity.Antic, MascotAntics.Dance),
            (2, Activity.Antic, MascotAntics.Twirl),
            (IsNight ? 5 : 1, Activity.Antic, MascotAntics.Yawn),
            (2, Activity.Antic, MascotAntics.TapFoot),
            (2, Activity.Antic, MascotAntics.TossBall),
            (1, Activity.Antic, MascotAntics.Puzzled),
            (1, Activity.Antic, MascotAntics.BlowHeart),
            (1, Activity.Antic, MascotAntics.Sneeze),
            (_situation.ContextFull ? 3 : 0, Activity.Antic, MascotAntics.WipeBrow),
            // The tip and the fall stay last: the tip's place is how its time is kept.
            (tip is not null ? 2 : 0, Activity.Antic, () => MascotAntics.Tip(tip!)),
            (canFall ? 1 : 0, Activity.Tumble, () => MascotAntics.TopplesOff(_random).Concat(MascotAntics.ClimbUp())),
        ];
        var pick = _random.Next(choices.Sum(c => c.Weight));
        for (var i = 0; i < choices.Length; i++)
        {
            var (weight, activity, steps) = choices[i];
            if (pick < weight)
            {
                if (activity == Activity.Tumble)
                {
                    _lastFall = now;
                }
                if (i == choices.Length - 2)
                {
                    _lastTip = now;
                }
                Play(activity, steps());
                return;
            }
            pick -= weight;
        }
    }

    /// <summary>Her pose while motion is reduced, at home: the first frame of what the tab has her doing.</summary>
    private MascotFrame StillFrame(MascotRoom room)
    {
        var situation = _situation;
        var step = situation switch
        {
            { Compacting: true } => MascotAntics.Sweep().First(),
            { OthersWaiting: > 0, Mood: not MascotMood.Waiting } when _lines is { } lines => MascotAntics.StillPoint(lines.OthersWaiting(situation.OthersWaiting)),
            { Mood: MascotMood.Working } => MascotAntics.StillWork(situation),
            { Mood: MascotMood.Waiting } => new MascotStep("wave", TimeSpan.Zero),
            { Mood: MascotMood.Resting } => new MascotStep("leanBlink", TimeSpan.Zero, Drop: 2, Props: [MascotAntics.SmallZ, MascotAntics.Hourglass(0)]),
            _ => MascotAntics.StandAbout(TimeSpan.Zero, situation.Planning).First(),
        };
        return new MascotFrame(step.Pose, room.HomeX, step.Drop, step.Props ?? [])
        {
            Hat = MascotCalendar.HatAt(_time.GetLocalNow()),
            Say = step.Say,
        };
    }

    private TimeSpan Calm() => _spell.Shortest + (_spell.Longest - _spell.Shortest) * _random.NextDouble();

    /// <summary>Three to six seconds between blinks.</summary>
    private TimeSpan BlinkWait() => TimeSpan.FromSeconds(3 + 3 * _random.NextDouble());

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    public void Dispose() => _next.Dispose();
}
