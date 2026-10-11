using Claudette.App.Mascot;
using Claudette.App.Services;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.App.Tests.Support;

/// <summary>Claudette on the composer's director on a fake clock, with every frame she shows and when.</summary>
internal sealed class MascotStage : IDisposable
{
    /// <summary>Room to walk either side of her home, which is over the Send button, and a free edge wider than her patch.</summary>
    public static readonly MascotRoom DefaultRoom = new(10, 70, Home: 50) { EdgeLeft = 2 };

    private readonly DateTimeOffset _start;

    public MascotStage(int seed = 1, IMascotLines? lines = null, DateTimeOffset? at = null)
    {
        Time = new FakeTimeProvider(at ?? DateTimeOffset.Parse("2026-10-10T12:00:00Z"));
        _start = Time.GetUtcNow();
        Lines = lines;
        Director = new MascotDirector(Time, new Immediately(), new Random(seed), lines);
        Director.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MascotDirector.Frame))
            {
                Frames.Add((Time.GetUtcNow() - _start, Director.Frame));
            }
        };
    }

    /// <summary>Local time is UTC, so her hats go by the times the tests give.</summary>
    public FakeTimeProvider Time { get; }

    public MascotDirector Director { get; }

    public IMascotLines? Lines { get; }

    public object View { get; } = new();

    public List<(TimeSpan At, MascotFrame Frame)> Frames { get; } = [];

    public MascotFrame Frame => Director.Frame;

    /// <summary>The frames from the <paramref name="from"/>th on.</summary>
    public IReadOnlyList<MascotFrame> Since(int from) => [.. Frames.Skip(from).Select(f => f.Frame)];

    /// <summary>On screen, on <paramref name="room"/>, and up from behind the box.</summary>
    public void Show(MascotRoom? room = null)
    {
        Director.SetRoom(room ?? DefaultRoom);
        Director.SetShown(View, true);
    }

    /// <summary>On screen and standing on the box, done climbing up.</summary>
    public void ShowStanding(MascotRoom? room = null)
    {
        Show(room);
        Run(TimeSpan.FromSeconds(5));
    }

    public void Run(TimeSpan span, TimeSpan? step = null, Action? each = null)
    {
        var by = step ?? TimeSpan.FromMilliseconds(10);
        for (var t = TimeSpan.Zero; t < span; t += by)
        {
            Time.Advance(by);
            each?.Invoke();
        }
    }

    public void Dispose() => Director.Dispose();

    /// <summary>
    /// Runs what's posted straight away. The fake clock fires her timers on the test's own thread, so she needs no UI
    /// thread, and hours of her don't hold up other tests on the one <see cref="InlineDispatcher"/> shares.
    /// </summary>
    private sealed class Immediately : IUiDispatcher
    {
        public void Post(Action action) => action();
    }
}

/// <summary>What she says, made up: a tip each time she's asked, and how many tabs wait.</summary>
internal sealed class FakeMascotLines : IMascotLines
{
    public string? NextTip { get; set; } = "A tip";

    public string? Tip(Random random) => NextTip;

    public string OthersWaiting(int count) => $"{count} waiting";
}
