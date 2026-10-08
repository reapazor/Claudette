using System.Text.Json.Nodes;
using Claudette.Core.Settings;
using Claudette.Usage;

namespace Claudette.Demo;

/// <summary>
/// The demo's plan usage (DESIGN.md §6): a history in the usage database that ends now, and the <c>get_usage</c>
/// answer fake-claude gives from <c>FAKE_CLAUDE_USAGE</c>, where the history ends, so the header's meters, sparkline,
/// projection and detailed charts agree.
/// </summary>
internal static class DemoUsage
{
    /// <summary>Session use through the window so far, by minute: a slow start, a busy stretch, a lull, then steady work.</summary>
    private static readonly (double Minutes, double Percent)[] Curve = [(0, 0), (25, 4), (70, 29), (105, 32), (150, 47), (196, 61)];

    /// <summary>The weekly limit used when the current session window started; the week so far climbs to it.</summary>
    private const double WeeklyAtSessionStart = 34;

    /// <summary>Each tab's share of this window's tokens, for the detailed header's busiest tabs.</summary>
    private static readonly Dictionary<string, double> TabShares = new()
    {
        ["tab-applepay"] = 0.44,
        ["tab-refunds"] = 0.27,
        ["tab-pool"] = 0.15,
        ["tab-flaky"] = 0.09,
        ["tab-upgrade"] = 0.05,
    };

    public static void Write(string database, string answer, DateTimeOffset now, IReadOnlyList<TabState> tabs)
    {
        var sessionResets = Minute(now.AddMinutes(104));
        var sessionStart = sessionResets.AddHours(-5);
        var weeklyResets = Minute(now.AddDays(3).AddHours(6));
        var weekStart = weeklyResets.AddDays(-7);

        // The store stamps each sample with its clock's time, and keeps at most one a minute.
        var clock = new Clock();
        using (var store = new UsageStore(database, clock))
        {
            // The week before this window: busy in working hours, quiet at night, each 5-hour window its own session.
            var activity = Activity(weekStart, sessionStart);
            for (var at = weekStart.AddMinutes(15); at < sessionStart; at = at.AddMinutes(15))
            {
                clock.Now = at;
                var weekly = WeeklyAtSessionStart * Activity(weekStart, at) / activity;
                var windowStart = sessionStart.AddHours(-5 * Math.Ceiling((sessionStart - at).TotalHours / 5));
                var session = Math.Min(100, 9 * (weekly - (WeeklyAtSessionStart * Activity(weekStart, windowStart) / activity)));
                store.AddSample(Snapshot(at, session, windowStart.AddHours(5), weekly, weeklyResets));
            }
            // This window.
            for (var at = sessionStart.AddMinutes(3); at < now; at = at.AddMinutes(3))
            {
                clock.Now = at;
                var session = Session((at - sessionStart).TotalMinutes);
                store.AddSample(Snapshot(at, session, sessionResets, Weekly(session), weeklyResets));
            }

            // This window's turns, a few per tab, for the busiest tabs.
            const long WindowTokens = 2_400_000;
            var turns = new List<TurnRecord>();
            foreach (var tab in tabs)
            {
                var share = TabShares.GetValueOrDefault(tab.Id, 0.05);
                for (var turn = 0; turn < 4; turn++)
                {
                    var tokens = (long)(WindowTokens * share / 4);
                    var at = sessionStart.AddMinutes(20 + (turn * 45) + (turns.Count * 3));
                    turns.Add(new TurnRecord(at, tab.Id, tab.SessionId, "claude-opus-5-5",
                        tokens / 200, tokens / 25, tokens / 10, tokens - (tokens / 200) - (tokens / 25) - (tokens / 10), tokens / 400_000.0));
                }
            }
            store.AddTurns(turns);
            foreach (var tab in tabs)
            {
                store.SetTabName(tab.Id, tab.UserName ?? tab.Id);
            }
        }

        var last = Session((now - sessionStart).TotalMinutes);
        File.WriteAllText(answer, new JsonObject
        {
            ["subscription_type"] = "max",
            ["rate_limits_available"] = true,
            ["rate_limits"] = new JsonObject
            {
                ["limits"] = new JsonArray(
                    Limit("session", last, sessionResets),
                    Limit("weekly_all", Weekly(last), weeklyResets),
                    Limit("weekly_scoped", Fable(Weekly(last)), weeklyResets, "Fable")),
            },
        }.ToJsonString());
    }

    private static UsageSnapshot Snapshot(DateTimeOffset at, double session, DateTimeOffset sessionResets, double weekly, DateTimeOffset weeklyResets) => new(
    [
        new LimitReading(LimitKind.Session, "Session", Math.Round(session), sessionResets, null, false),
        new LimitReading(LimitKind.WeeklyAll, "Weekly", Math.Round(weekly), weeklyResets, null, false),
        new LimitReading(LimitKind.WeeklyModel, "Fable", Math.Round(Fable(weekly)), weeklyResets, null, false),
    ], at, UsageSource.GetUsage);

    private static double Session(double minutes)
    {
        for (var i = 1; i < Curve.Length; i++)
        {
            if (minutes <= Curve[i].Minutes)
            {
                var (from, to) = (Curve[i - 1], Curve[i]);
                return from.Percent + ((to.Percent - from.Percent) * (minutes - from.Minutes) / (to.Minutes - from.Minutes));
            }
        }
        return Curve[^1].Percent;
    }

    private static double Weekly(double session) => WeeklyAtSessionStart + (session / 9);

    /// <summary>Fable is about a third of the week's use.</summary>
    private static double Fable(double weekly) => weekly / 3.1;

    /// <summary>How much work happened from <paramref name="from"/> to <paramref name="to"/>, in busy quarter hours.</summary>
    private static double Activity(DateTimeOffset from, DateTimeOffset to)
    {
        var total = 0.0;
        for (var at = from; at < to; at = at.AddMinutes(15))
        {
            var hour = TimeZoneInfo.ConvertTime(at, TimeZoneInfo.Local).Hour;
            total += hour is >= 9 and < 18 ? 1 : hour is >= 19 and < 23 ? 0.3 : 0;
        }
        return total;
    }

    /// <summary>A limit as <c>get_usage</c> reports it, as fake-claude's own demo plan does.</summary>
    private static JsonObject Limit(string kind, double percent, DateTimeOffset resets, string? model = null) => new()
    {
        ["kind"] = kind,
        ["group"] = kind == "session" ? "session" : "weekly",
        ["percent"] = Math.Round(percent),
        ["severity"] = percent >= 90 ? "critical" : percent >= 75 ? "warning" : "normal",
        ["resets_at"] = resets.ToString("o"),
        ["scope"] = model is null ? null : new JsonObject { ["model"] = new JsonObject { ["id"] = null, ["display_name"] = model }, ["surface"] = null },
        ["is_active"] = false,
    };

    private static DateTimeOffset Minute(DateTimeOffset at) => new(at.Ticks - (at.Ticks % TimeSpan.TicksPerMinute), at.Offset);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
