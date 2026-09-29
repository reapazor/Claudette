namespace Claudette.Core.Status;

/// <summary>The parts of Claude that Claudette relies on, and so watches on the status page.</summary>
public enum WatchedService
{
    /// <summary>Claude Code itself: every tab.</summary>
    ClaudeCode,

    /// <summary>The Claude API (api.anthropic.com), which Claude Code calls.</summary>
    ClaudeApi,

    /// <summary>claude.ai: sign-in, and Remote Control (DESIGN.md §18).</summary>
    ClaudeAi,
}

/// <summary>
/// One level for everything watched, for the header's dot (DESIGN.md §18, "Service status"). Ordered from least to
/// most serious after <see cref="Unknown"/>, so a higher value is worse.
/// </summary>
public enum ServiceLevel
{
    /// <summary>The status page couldn't be read, or doesn't say.</summary>
    Unknown,
    Operational,
    Maintenance,
    Degraded,
    Outage,
}

/// <summary>One watched service and its status; <see cref="ComponentStatus.Unknown"/> when the page doesn't list it.</summary>
public sealed record WatchedServiceStatus(WatchedService Service, ComponentStatus Status)
{
    public string Name => ServiceStatusReport.NameOf(Service);
}

/// <summary>
/// What the status page says about the watched services (DESIGN.md §18, "Service status"): each one's status, one
/// overall level, the open incidents that concern them, and maintenance under way on them.
/// </summary>
/// <param name="Incidents">
/// Incidents that aren't resolved and name a watched service, or name no components at all; the most recently updated
/// first.
/// </param>
/// <param name="Maintenances">Maintenances in progress on a watched service, or naming no components.</param>
/// <param name="AsOf">When Claudette read the page.</param>
public sealed record ServiceStatusReport(
    ServiceLevel Level,
    IReadOnlyList<WatchedServiceStatus> Services,
    IReadOnlyList<StatusIncident> Incidents,
    IReadOnlyList<StatusMaintenance> Maintenances,
    DateTimeOffset AsOf)
{
    public static readonly IReadOnlyList<WatchedService> Watched = [WatchedService.ClaudeCode, WatchedService.ClaudeApi, WatchedService.ClaudeAi];

    /// <summary>The newest incident, for the banner and the dot's tooltip.</summary>
    public StatusIncident? LatestIncident => Incidents.Count > 0 ? Incidents[0] : null;

    /// <summary>Something watched is degraded or down, or an incident concerns it: the warning banner.</summary>
    public bool HasProblem => Level is ServiceLevel.Degraded or ServiceLevel.Outage;

    /// <summary>Only maintenance: the quieter banner.</summary>
    public bool IsMaintenance => Level == ServiceLevel.Maintenance;

    public bool ShowsBanner => HasProblem || IsMaintenance;

    /// <summary>Everything watched is up, with nothing open: a dismissed banner can be forgotten.</summary>
    public bool IsAllClear => Level == ServiceLevel.Operational && Incidents.Count == 0 && Maintenances.Count == 0;

    /// <summary>What the banner is about, for dismissing it: the incidents' ids, then the maintenances'.</summary>
    public IReadOnlyList<string> BannerIds => [.. Incidents.Select(i => i.Id), .. Maintenances.Select(m => m.Id)];

    /// <summary>The name Claudette shows for a watched service.</summary>
    public static string NameOf(WatchedService service) => service switch
    {
        WatchedService.ClaudeCode => "Claude Code",
        WatchedService.ClaudeApi => "Claude API",
        _ => "claude.ai",
    };

    /// <summary>
    /// Which watched service a component is, by name, tolerantly: "Claude Code"; a name containing "Claude API" or
    /// "api.anthropic.com" (today "Claude API (api.anthropic.com)"); and "claude.ai". Case and surrounding spaces don't
    /// matter. Null for anything else, such as "Claude Console (platform.claude.com)".
    /// </summary>
    public static WatchedService? Match(string componentName)
    {
        var name = componentName.Trim();
        if (name.Equals("Claude Code", StringComparison.OrdinalIgnoreCase))
        {
            return WatchedService.ClaudeCode;
        }
        if (name.Contains("Claude API", StringComparison.OrdinalIgnoreCase) || name.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase))
        {
            return WatchedService.ClaudeApi;
        }
        return name.Equals("claude.ai", StringComparison.OrdinalIgnoreCase) ? WatchedService.ClaudeAi : null;
    }

    /// <summary>A component's status as a level: a partial outage is an outage for whoever it hits.</summary>
    public static ServiceLevel LevelOf(ComponentStatus status) => status switch
    {
        ComponentStatus.Operational => ServiceLevel.Operational,
        ComponentStatus.DegradedPerformance => ServiceLevel.Degraded,
        ComponentStatus.PartialOutage or ComponentStatus.MajorOutage => ServiceLevel.Outage,
        ComponentStatus.UnderMaintenance => ServiceLevel.Maintenance,
        _ => ServiceLevel.Unknown,
    };

    /// <summary>The page's own roll-up as a level, for when none of the watched services is listed.</summary>
    public static ServiceLevel LevelOf(StatusIndicator indicator) => indicator switch
    {
        StatusIndicator.None => ServiceLevel.Operational,
        StatusIndicator.Minor => ServiceLevel.Degraded,
        StatusIndicator.Major or StatusIndicator.Critical => ServiceLevel.Outage,
        StatusIndicator.Maintenance => ServiceLevel.Maintenance,
        _ => ServiceLevel.Unknown,
    };

    /// <summary>Nothing is known: the page couldn't be read.</summary>
    public static ServiceStatusReport Unknown(DateTimeOffset asOf) =>
        new(ServiceLevel.Unknown, [.. Watched.Select(s => new WatchedServiceStatus(s, ComponentStatus.Unknown))], [], [], asOf);

    /// <summary>
    /// Works out the report from the page. The level is the worst of the watched services' statuses; with none of them
    /// listed (or none with a status Claudette knows), the page's own indicator. An open incident that concerns them
    /// makes it at least <see cref="ServiceLevel.Degraded"/>, and maintenance under way at least
    /// <see cref="ServiceLevel.Maintenance"/>.
    /// </summary>
    public static ServiceStatusReport From(StatusSummary summary, DateTimeOffset asOf)
    {
        var watchedIds = new HashSet<string>(StringComparer.Ordinal);
        var worst = new Dictionary<WatchedService, ComponentStatus>();
        foreach (var component in summary.Components)
        {
            if (Match(component.Name) is not { } service)
            {
                continue;
            }
            if (component.Id.Length > 0)
            {
                watchedIds.Add(component.Id);
            }
            // A service listed twice (a group and its part, say) counts as its worse listing.
            if (!worst.TryGetValue(service, out var seen) || LevelOf(component.Status) > LevelOf(seen))
            {
                worst[service] = component.Status;
            }
        }
        var services = Watched.Select(s => new WatchedServiceStatus(s, worst.GetValueOrDefault(s, ComponentStatus.Unknown))).ToList();

        bool Concerns(IReadOnlyList<StatusComponent> components) =>
            components.Count == 0 || components.Any(c => watchedIds.Contains(c.Id) || Match(c.Name) is not null);

        var incidents = summary.Incidents
            .Where(i => !i.IsResolved && Concerns(i.Components))
            .OrderByDescending(i => i.UpdatedAt ?? DateTimeOffset.MinValue)
            .ToList();
        var maintenances = summary.Maintenances.Where(m => m.IsInProgress && Concerns(m.Components)).ToList();

        var known = services.Select(s => LevelOf(s.Status)).Where(l => l != ServiceLevel.Unknown).ToList();
        var level = known.Count > 0 ? known.Max() : LevelOf(summary.Indicator);
        if (incidents.Count > 0 && level < ServiceLevel.Degraded)
        {
            level = ServiceLevel.Degraded;
        }
        if (maintenances.Count > 0 && level < ServiceLevel.Maintenance)
        {
            level = ServiceLevel.Maintenance;
        }
        return new ServiceStatusReport(level, services, incidents, maintenances, asOf);
    }
}

/// <summary>
/// A service status banner the user dismissed (DESIGN.md §18, "Service status"), kept with this machine's state: what
/// it was about and how bad things were. It stays hidden until a different incident or maintenance arrives, or the
/// level gets worse.
/// </summary>
public sealed class ServiceStatusDismissal
{
    /// <summary>The ids of the incidents and maintenances the banner was about.</summary>
    public List<string> IncidentIds { get; set; } = [];

    public ServiceLevel Level { get; set; }

    public static ServiceStatusDismissal Of(ServiceStatusReport report) => new() { IncidentIds = [.. report.BannerIds], Level = report.Level };

    /// <summary>The report's banner is one the user already dismissed: nothing new, and no worse.</summary>
    public bool Covers(ServiceStatusReport report) =>
        report.Level <= Level && report.BannerIds.All(id => IncidentIds.Contains(id, StringComparer.Ordinal));
}

/// <summary>Words for the status page's values.</summary>
public static class StatusText
{
    public static string Describe(this ComponentStatus status) => status switch
    {
        ComponentStatus.Operational => "Operational",
        ComponentStatus.DegradedPerformance => "Degraded performance",
        ComponentStatus.PartialOutage => "Partial outage",
        ComponentStatus.MajorOutage => "Major outage",
        ComponentStatus.UnderMaintenance => "Under maintenance",
        _ => "Unknown",
    };

    /// <summary>Lower case, for "(investigating)"; null when Claudette doesn't know the value.</summary>
    public static string? Describe(this IncidentStatus status) => status switch
    {
        IncidentStatus.Investigating => "investigating",
        IncidentStatus.Identified => "identified",
        IncidentStatus.Monitoring => "monitoring",
        IncidentStatus.Resolved => "resolved",
        IncidentStatus.Postmortem => "postmortem",
        _ => null,
    };
}
