namespace Claudette.Usage;

/// <summary>Which plan limit a reading is for (DESIGN.md §6, "Header meters").</summary>
public enum LimitKind
{
    /// <summary>The 5-hour session window.</summary>
    Session,

    /// <summary>The weekly limit across all models.</summary>
    WeeklyAll,

    /// <summary>A model-specific weekly limit, such as Fable.</summary>
    WeeklyModel,
}

/// <summary>One plan limit at one moment.</summary>
/// <param name="Label">"Session", "Weekly", or the model's display name (for example "Fable").</param>
/// <param name="Percent">0–100.</param>
/// <param name="ResetsAt">When the window resets, if known. Rounded to the second, so sources agree.</param>
/// <param name="Severity">Claude Code's own rating (<c>normal</c> … <c>critical</c>), from <c>get_usage</c> only.</param>
/// <param name="IsActive">Claude Code's <c>is_active</c> flag, from <c>get_usage</c> only.</param>
/// <param name="ReportedAt">
/// When a source last reported this value, for a reading kept from an earlier snapshot (see
/// <see cref="UsageParser.Merge"/>). Null means the snapshot's own <see cref="UsageSnapshot.AsOf"/>.
/// </param>
public sealed record LimitReading(LimitKind Kind, string Label, double Percent, DateTimeOffset? ResetsAt, string? Severity, bool IsActive, DateTimeOffset? ReportedAt = null);

/// <summary>Where a <see cref="UsageSnapshot"/> came from (DESIGN.md §6, "Data source").</summary>
public enum UsageSource
{
    GetUsage,
    RateLimitEvent,
    UsageCommand,

    /// <summary>The latest sample from the usage history, shown after a restart until Claude Code reports.</summary>
    Stored,
}

/// <summary>The plan limits known at <see cref="AsOf"/>. Plan usage is app-wide, not per tab (DESIGN.md §6, "Sampling").</summary>
public sealed record UsageSnapshot(IReadOnlyList<LimitReading> Limits, DateTimeOffset AsOf, UsageSource Source)
{
    public LimitReading? Session => Limits.FirstOrDefault(l => l.Kind == LimitKind.Session);

    public LimitReading? WeeklyAll => Limits.FirstOrDefault(l => l.Kind == LimitKind.WeeklyAll);

    public IReadOnlyList<LimitReading> WeeklyModels => [.. Limits.Where(l => l.Kind == LimitKind.WeeklyModel)];
}
