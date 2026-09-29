using System.Text.Json.Nodes;
using Claudette.Usage.Tests.Support;

namespace Claudette.Usage.Tests;

public sealed class UsageParserTests
{
    private static readonly DateTimeOffset Now = UsageFixtures.RecordedAt;

    [Fact]
    public void Get_usage_reads_the_limits_list()
    {
        var snapshot = UsageParser.FromGetUsage(UsageFixtures.GetUsage(), Now);

        Assert.NotNull(snapshot);
        Assert.Equal(UsageSource.GetUsage, snapshot.Source);
        Assert.Equal(Now, snapshot.AsOf);
        Assert.Equal(3, snapshot.Limits.Count);
        Assert.Equal(new LimitReading(LimitKind.Session, "Session", 13, UsageFixtures.SessionResetsAt, "normal", false), snapshot.Session);
        Assert.Equal(new LimitReading(LimitKind.WeeklyAll, "Weekly", 57, UsageFixtures.WeekResetsAt, "normal", false), snapshot.WeeklyAll);
        var fable = Assert.Single(snapshot.WeeklyModels);
        Assert.Equal(new LimitReading(LimitKind.WeeklyModel, "Fable", 100, UsageFixtures.WeekResetsAt, "critical", true), fable);
    }

    [Fact]
    public void Get_usage_without_the_limits_list_falls_back_to_the_window_fields()
    {
        var response = UsageFixtures.GetUsage();
        response["rate_limits"]!.AsObject().Remove("limits");

        var snapshot = UsageParser.FromGetUsage(response, Now);

        Assert.NotNull(snapshot);
        Assert.Equal(13, snapshot.Session!.Percent);
        Assert.Equal(UsageFixtures.SessionResetsAt, snapshot.Session.ResetsAt);
        Assert.Equal(57, snapshot.WeeklyAll!.Percent);
        var fable = Assert.Single(snapshot.WeeklyModels);
        Assert.Equal(("Fable", 100.0, UsageFixtures.WeekResetsAt), (fable.Label, fable.Percent, fable.ResetsAt!.Value));
    }

    [Fact]
    public void Get_usage_skips_unknown_kinds_and_scoped_limits_without_a_model_name()
    {
        var response = Parse("""
            {"rate_limits":{"limits":[
              {"kind":"monthly","percent":40},
              {"kind":"weekly_scoped","percent":20,"scope":null},
              {"kind":"weekly_scoped","percent":30,"scope":{"model":{"id":"x","display_name":null}}},
              {"kind":"weekly_scoped","percent":50,"scope":{"model":{"display_name":"Opus"}},"resets_at":null,"new_field":[1,2]},
              {"kind":"session","percent":null},
              {"kind":"session","percent":10,"severity":"brand_new_level"}
            ]}}
            """);

        var snapshot = UsageParser.FromGetUsage(response, Now);

        Assert.NotNull(snapshot);
        Assert.Collection(
            snapshot.Limits,
            l => Assert.Equal(new LimitReading(LimitKind.WeeklyModel, "Opus", 50, null, null, false), l),
            l => Assert.Equal(new LimitReading(LimitKind.Session, "Session", 10, null, "brand_new_level", false), l));
    }

    [Fact]
    public void Get_usage_reads_numbers_built_in_code()
    {
        var response = new JsonObject
        {
            ["rate_limits"] = new JsonObject
            {
                ["limits"] = new JsonArray(new JsonObject { ["kind"] = "session", ["percent"] = 42, ["resets_at"] = 1790648400L }),
            },
        };

        var session = UsageParser.FromGetUsage(response, Now)?.Session;

        Assert.Equal(42, session?.Percent);
        Assert.Equal(UsageFixtures.SessionResetsAt, session?.ResetsAt);
    }

    [Fact]
    public void Get_usage_returns_null_when_rate_limits_are_unavailable()
    {
        var response = UsageFixtures.GetUsage();
        response["rate_limits_available"] = false;

        Assert.Null(UsageParser.FromGetUsage(response, Now));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"rate_limits":null}""")]
    [InlineData("""{"rate_limits":[1,2]}""")]
    [InlineData("""{"rate_limits":{"limits":"soon"}}""")]
    [InlineData("""{"rate_limits":{"limits":[]}}""")]
    [InlineData("""{"rate_limits":{"limits":[1,"a",null,true,{"kind":"session","percent":"13"}]}}""")]
    [InlineData("""{"rate_limits":{"five_hour":{"utilization":null},"seven_day":"57","model_scoped":{"a":1}}}""")]
    [InlineData("""{"rate_limits":{"model_scoped":[{"utilization":50}]}}""")]
    public void Get_usage_returns_null_for_unusable_shapes(string json)
    {
        Assert.Null(UsageParser.FromGetUsage(Parse(json), Now));
    }

    [Fact]
    public void Get_usage_tolerates_a_bad_reset_time()
    {
        var response = Parse("""{"rate_limits":{"limits":[{"kind":"session","percent":5,"resets_at":"next tuesday"},{"kind":"weekly_all","percent":6,"resets_at":{"at":1}}]}}""");

        var snapshot = UsageParser.FromGetUsage(response, Now);

        Assert.Null(snapshot!.Session!.ResetsAt);
        Assert.Null(snapshot.WeeklyAll!.ResetsAt);
    }

    [Fact]
    public void Rate_limit_event_reads_the_unified_windows()
    {
        var snapshot = UsageParser.FromRateLimitEvent(UsageFixtures.RateLimitInfo(), Now);

        Assert.NotNull(snapshot);
        Assert.Equal(UsageSource.RateLimitEvent, snapshot.Source);
        Assert.Equal(new LimitReading(LimitKind.Session, "Session", 12, UsageFixtures.SessionResetsAt, null, false), snapshot.Session);
        Assert.Equal(new LimitReading(LimitKind.WeeklyAll, "Weekly", 57, UsageFixtures.WeekResetsAt, null, false), snapshot.WeeklyAll);
        Assert.Empty(snapshot.WeeklyModels);
    }

    [Fact]
    public void Rate_limit_event_and_get_usage_agree_on_reset_times()
    {
        var fromEvent = UsageParser.FromRateLimitEvent(UsageFixtures.RateLimitInfo(), Now)!;
        var fromPoll = UsageParser.FromGetUsage(UsageFixtures.GetUsage(), Now)!;

        Assert.Equal(fromPoll.Session!.ResetsAt, fromEvent.Session!.ResetsAt);
        Assert.Equal(fromPoll.WeeklyAll!.ResetsAt, fromEvent.WeeklyAll!.ResetsAt);
    }

    [Theory]
    [InlineData("five_hour", LimitKind.Session)]
    [InlineData("seven_day", LimitKind.WeeklyAll)]
    public void Rate_limit_event_without_unified_windows_uses_the_top_level_window(string type, LimitKind kind)
    {
        var info = Parse($$"""{"status":"allowed_warning","rateLimitType":"{{type}}","utilization":0.8,"resetsAt":1790648400}""");

        var reading = Assert.Single(UsageParser.FromRateLimitEvent(info, Now)!.Limits);

        Assert.Equal(kind, reading.Kind);
        Assert.Equal(80, reading.Percent);
        Assert.Equal(UsageFixtures.SessionResetsAt, reading.ResetsAt);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"status":"allowed"}""")]
    [InlineData("""{"rateLimitType":"seven_day_opus","utilization":0.5}""")]
    [InlineData("""{"rateLimitType":"five_hour"}""")]
    [InlineData("""{"unifiedWindows":[1]}""")]
    [InlineData("""{"unifiedWindows":{"five_hour":{"utilization":"x"},"seven_day":null}}""")]
    public void Rate_limit_event_returns_null_without_a_usable_window(string json)
    {
        Assert.Null(UsageParser.FromRateLimitEvent(Parse(json), Now));
    }

    [Fact]
    public void Usage_command_reads_the_one_line_form()
    {
        var snapshot = UsageParser.FromUsageCommand(UsageFixtures.UsageCommand(), Now);

        Assert.NotNull(snapshot);
        Assert.Equal(UsageSource.UsageCommand, snapshot.Source);
        // 10:19pm in Toronto (EDT) on Sep 28 is 02:19 UTC on Sep 29; /usage shows minutes only.
        Assert.Equal(new LimitReading(LimitKind.Session, "Session", 13, new DateTimeOffset(2026, 9, 29, 2, 19, 0, TimeSpan.Zero), null, false), snapshot.Session);
        Assert.Equal(new LimitReading(LimitKind.WeeklyAll, "Weekly", 57, new DateTimeOffset(2026, 9, 30, 10, 59, 0, TimeSpan.Zero), null, false), snapshot.WeeklyAll);
        var fable = Assert.Single(snapshot.WeeklyModels);
        Assert.Equal(new LimitReading(LimitKind.WeeklyModel, "Fable", 100, new DateTimeOffset(2026, 9, 30, 10, 59, 0, TimeSpan.Zero), null, false), fable);
    }

    [Fact]
    public void Usage_command_reads_the_block_form()
    {
        const string text = """
            Current session
            [1m█████▌[0m                                             23% used
            Resets 3am (Europe/London)

            Current week (all models)
            ██▌                                                9% used
            Resets Oct 3, 11pm (Europe/London)

            Current week (Sonnet only)
            █                                                  2.5% used

            Extra usage
            Resets Nov 1 (Europe/London)
            """;

        var snapshot = UsageParser.FromUsageCommand(text.Replace("[", "\u001b[", StringComparison.Ordinal), Now);

        Assert.NotNull(snapshot);
        // 19:00 in London (BST) now, so 3am is tomorrow.
        Assert.Equal(new LimitReading(LimitKind.Session, "Session", 23, new DateTimeOffset(2026, 9, 29, 2, 0, 0, TimeSpan.Zero), null, false), snapshot.Session);
        Assert.Equal(new LimitReading(LimitKind.WeeklyAll, "Weekly", 9, new DateTimeOffset(2026, 10, 3, 22, 0, 0, TimeSpan.Zero), null, false), snapshot.WeeklyAll);
        Assert.Equal(new LimitReading(LimitKind.WeeklyModel, "Sonnet", 2.5, null, null, false), Assert.Single(snapshot.WeeklyModels));
    }

    [Fact]
    public void Usage_command_infers_the_year_as_the_next_occurrence()
    {
        var now = new DateTimeOffset(2026, 12, 30, 12, 0, 0, TimeSpan.Zero);

        var snapshot = UsageParser.FromUsageCommand("Current week (all models): 90% used · resets Jan 2, 1:05am (UTC)", now);

        Assert.Equal(new DateTimeOffset(2027, 1, 2, 1, 5, 0, TimeSpan.Zero), snapshot!.WeeklyAll!.ResetsAt);
    }

    [Theory]
    [InlineData("7pm", 2026, 9, 28, 19, 0)]
    [InlineData("5:30pm", 2026, 9, 29, 17, 30)]
    [InlineData("17:59", 2026, 9, 28, 17, 59)] // a minute ago: the reset shown is still this one
    [InlineData("12am", 2026, 9, 29, 0, 0)]
    [InlineData("12:15 p.m.", 2026, 9, 29, 12, 15)]
    [InlineData("Sep 30", 2026, 9, 30, 0, 0)]
    [InlineData("Sep 1, 9am", 2027, 9, 1, 9, 0)]
    [InlineData("October 3rd at 6:59am", 2026, 10, 3, 6, 59)]
    [InlineData("Sep 30, 2027, 6:59am", 2027, 9, 30, 6, 59)]
    public void Usage_command_reset_times_are_the_next_occurrence(string when, int year, int month, int day, int hour, int minute)
    {
        var resetsAt = UsageParser.ParseResetTime($"{when} (UTC)", Now);

        Assert.Equal(new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero), resetsAt);
    }

    [Fact]
    public void Usage_command_reads_a_relative_reset_time()
    {
        Assert.Equal(Now + new TimeSpan(2, 14, 0), UsageParser.ParseResetTime("in 2h 14m", Now));
        Assert.Equal(Now + TimeSpan.FromDays(3) + TimeSpan.FromHours(1), UsageParser.ParseResetTime("in 3 days, 1 hour", Now));
    }

    [Fact]
    public void Usage_command_uses_the_local_zone_for_an_unknown_zone()
    {
        var wallClock = new DateTime(2026, 9, 30, 6, 59, 0, DateTimeKind.Unspecified);
        var expected = new DateTimeOffset(wallClock, TimeZoneInfo.Local.GetUtcOffset(wallClock));

        Assert.Equal(expected, UsageParser.ParseResetTime("Sep 30, 6:59am (Mars/Olympus_Mons)", Now));
        Assert.Equal(expected, UsageParser.ParseResetTime("Sep 30, 6:59am", Now));
    }

    [Theory]
    [InlineData("tomorrow-ish")]
    [InlineData("Smarch 3, 1pm")]
    [InlineData("25:00")]
    [InlineData("13pm")]
    [InlineData("Feb 30")]
    [InlineData("in 99999999999999h")]
    public void Usage_command_ignores_reset_times_it_cannot_read(string when)
    {
        var snapshot = UsageParser.FromUsageCommand($"Current session: 13% used · resets {when}", Now);

        Assert.Equal(13, snapshot!.Session!.Percent);
        Assert.Null(snapshot.Session.ResetsAt);
    }

    [Fact]
    public void Usage_command_reads_a_line_without_a_reset_time()
    {
        var snapshot = UsageParser.FromUsageCommand("Current session: 0% used", Now);

        Assert.Equal(new LimitReading(LimitKind.Session, "Session", 0, null, null, false), snapshot!.Session);
    }

    [Theory]
    [InlineData("")]
    [InlineData("You are currently using your subscription to power your Claude Code usage")]
    [InlineData("Current session: soon")]
    [InlineData("Unknown command: /usage")]
    public void Usage_command_returns_null_without_any_percentage(string text)
    {
        Assert.Null(UsageParser.FromUsageCommand(text, Now));
    }

    [Fact]
    public void Merge_with_nothing_before_is_the_update()
    {
        var update = UsageParser.FromRateLimitEvent(UsageFixtures.RateLimitInfo(), Now)!;

        Assert.Same(update, UsageParser.Merge(null, update));
    }

    [Fact]
    public void A_rate_limit_event_keeps_the_model_readings()
    {
        var previous = UsageParser.FromGetUsage(UsageFixtures.GetUsage(), Now)!;
        var update = UsageParser.FromRateLimitEvent(UsageFixtures.RateLimitInfo(), Now.AddMinutes(1))!;

        var merged = UsageParser.Merge(previous, update);

        Assert.Equal(UsageSource.RateLimitEvent, merged.Source);
        Assert.Equal(Now.AddMinutes(1), merged.AsOf);
        Assert.Equal(12, merged.Session!.Percent);
        Assert.Equal(57, merged.WeeklyAll!.Percent);
        Assert.Equal(previous.WeeklyModels, merged.WeeklyModels);
    }

    [Fact]
    public void A_rate_limit_event_with_only_the_session_keeps_the_weekly_reading()
    {
        var previous = UsageParser.FromGetUsage(UsageFixtures.GetUsage(), Now)!;
        var update = UsageParser.FromRateLimitEvent(Parse("""{"rateLimitType":"five_hour","utilization":0.2,"resetsAt":1790648400}"""), Now)!;

        var merged = UsageParser.Merge(previous, update);

        Assert.Equal(new[] { 20.0, 57.0, 100.0 }, merged.Limits.Select(l => l.Percent));
    }

    [Fact]
    public void Get_usage_and_usage_command_updates_replace_everything()
    {
        var previous = UsageParser.FromGetUsage(UsageFixtures.GetUsage(), Now)!;
        var fromPoll = UsageFixtures.Snapshot(Now, 20);
        var fromCommand = fromPoll with { Source = UsageSource.UsageCommand };

        Assert.Same(fromPoll, UsageParser.Merge(previous, fromPoll));
        Assert.Same(fromCommand, UsageParser.Merge(previous, fromCommand));
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();
}
