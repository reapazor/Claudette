using System.Text.Json.Nodes;

namespace Claudette.Usage.Tests.Support;

/// <summary>
/// Real payloads recorded from Claude Code 2.1.284 in the milestone 1 spike (trimmed, with account details removed),
/// and helpers for building snapshots by hand.
/// </summary>
internal static class UsageFixtures
{
    /// <summary>A moment on the day the fixtures were recorded, before the session in them resets.</summary>
    public static readonly DateTimeOffset RecordedAt = new(2026, 9, 28, 18, 0, 0, TimeSpan.Zero);

    public static readonly DateTimeOffset SessionResetsAt = new(2026, 9, 29, 2, 20, 0, TimeSpan.Zero);

    public static readonly DateTimeOffset WeekResetsAt = new(2026, 9, 30, 11, 0, 0, TimeSpan.Zero);

    public static JsonObject GetUsage() => Json("get_usage.json");

    /// <summary>The <c>rate_limit_info</c> of the recorded <c>rate_limit_event</c>.</summary>
    public static JsonObject RateLimitInfo() => Json("rate_limit_event.json")["rate_limit_info"]!.AsObject();

    public static string UsageCommand() => File.ReadAllText(PathOf("usage_command.txt"));

    public static UsageSnapshot Snapshot(DateTimeOffset asOf, double session, DateTimeOffset? sessionResetsAt = null, double? weekly = null, double? fable = null) =>
        new(
            [
                new LimitReading(LimitKind.Session, "Session", session, sessionResetsAt ?? SessionResetsAt, null, false),
                .. weekly is { } w ? [new LimitReading(LimitKind.WeeklyAll, "Weekly", w, WeekResetsAt, null, false)] : Array.Empty<LimitReading>(),
                .. fable is { } f ? [new LimitReading(LimitKind.WeeklyModel, "Fable", f, WeekResetsAt, null, false)] : Array.Empty<LimitReading>(),
            ],
            asOf,
            UsageSource.GetUsage);

    private static JsonObject Json(string name) => JsonNode.Parse(File.ReadAllText(PathOf(name)))!.AsObject();

    private static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "2.1.284", name);
}
