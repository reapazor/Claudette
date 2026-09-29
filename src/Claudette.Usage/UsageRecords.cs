using System.Text.Json.Nodes;
using Claudette.Core.Protocol;

namespace Claudette.Usage;

/// <summary>A model-specific weekly limit within a <see cref="UsageSample"/>.</summary>
public sealed record ModelSample(string Label, double Percent, DateTimeOffset? ResetsAt);

/// <summary>
/// One stored plan-usage sample (DESIGN.md §6, "Usage history"). A percentage is null when the snapshot it came from
/// didn't have that limit.
/// </summary>
public sealed record UsageSample(
    DateTimeOffset Timestamp,
    double? SessionPercent,
    DateTimeOffset? SessionResetsAt,
    double? WeeklyPercent,
    DateTimeOffset? WeeklyResetsAt,
    IReadOnlyList<ModelSample> Models)
{
    /// <summary>The sample as a <see cref="UsageSource.Stored"/> snapshot, for the header after a restart.</summary>
    public UsageSnapshot ToSnapshot()
    {
        var limits = new List<LimitReading>();
        if (SessionPercent is { } session)
        {
            limits.Add(new LimitReading(LimitKind.Session, UsageParser.SessionLabel, session, SessionResetsAt, null, false));
        }
        if (WeeklyPercent is { } weekly)
        {
            limits.Add(new LimitReading(LimitKind.WeeklyAll, UsageParser.WeeklyLabel, weekly, WeeklyResetsAt, null, false));
        }
        limits.AddRange(Models.Select(m => new LimitReading(LimitKind.WeeklyModel, m.Label, m.Percent, m.ResetsAt, null, false)));
        return new UsageSnapshot(limits, Timestamp, UsageSource.Stored);
    }
}

/// <summary>
/// One model's tokens for one turn of one tab (DESIGN.md §6, "Usage history"). Counts only: no conversation content.
/// </summary>
/// <param name="CostUsd">Claude Code's client-side estimate at list price; not a bill.</param>
public sealed record TurnRecord(
    DateTimeOffset Timestamp,
    string TabId,
    string? SessionId,
    string Model,
    long Input,
    long Output,
    long CacheWrite,
    long CacheRead,
    double CostUsd)
{
    public long Total => Input + Output + CacheWrite + CacheRead;

    /// <summary>One record per model in the result's <c>modelUsage</c>, read the same way as <c>TokenTotals.Add</c>.</summary>
    public static IReadOnlyList<TurnRecord> FromResult(ResultMessage result, string tabId, DateTimeOffset timestamp)
    {
        if (result.ModelUsage is not { Count: > 0 } byModel)
        {
            return [];
        }
        var records = new List<TurnRecord>();
        foreach (var (model, node) in byModel)
        {
            if (node is not JsonObject usage)
            {
                continue;
            }
            records.Add(new TurnRecord(
                timestamp,
                tabId,
                result.SessionId,
                model,
                Tokens(usage, "inputTokens"),
                Tokens(usage, "outputTokens"),
                Tokens(usage, "cacheCreationInputTokens"),
                Tokens(usage, "cacheReadInputTokens"),
                usage.GetDouble("costUSD") ?? 0));
        }
        return records;
    }

    private static long Tokens(JsonObject usage, string name) => usage.GetDouble(name) is { } value ? (long)value : 0;
}

/// <summary>A tab's tokens since some moment, for "which tab is burning the most".</summary>
/// <param name="Turns">Turns, not records: a turn that used two models counts once.</param>
public sealed record TabTokenSum(string TabId, long Input, long Output, long CacheWrite, long CacheRead, double CostUsd, int Turns)
{
    public long Total => Input + Output + CacheWrite + CacheRead;
}
