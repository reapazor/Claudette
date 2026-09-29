using System.Globalization;
using System.Text.Json;

namespace Claudette.Core.Status;

/// <summary>The page's own roll-up of every component: <c>status.indicator</c>.</summary>
public enum StatusIndicator
{
    Unknown,
    None,
    Minor,
    Major,
    Critical,
    Maintenance,
}

/// <summary>A component's status, as Statuspage names them.</summary>
public enum ComponentStatus
{
    Unknown,
    Operational,
    DegradedPerformance,
    PartialOutage,
    MajorOutage,
    UnderMaintenance,
}

/// <summary>Where an incident is, from the first report to the write-up.</summary>
public enum IncidentStatus
{
    Unknown,
    Investigating,
    Identified,
    Monitoring,
    Resolved,
    Postmortem,
}

/// <summary>How bad the status page says an incident is.</summary>
public enum IncidentImpact
{
    Unknown,
    None,
    Minor,
    Major,
    Critical,
    Maintenance,
}

/// <summary>Where a scheduled maintenance is.</summary>
public enum MaintenanceStatus
{
    Unknown,
    Scheduled,
    InProgress,
    Verifying,
    Completed,
}

/// <summary>One component on the status page, such as "Claude Code".</summary>
public sealed record StatusComponent(string Id, string Name, ComponentStatus Status);

/// <summary>An incident on the status page. The summary only lists ones that aren't resolved yet.</summary>
/// <param name="Components">The components it names; empty when it names none.</param>
/// <param name="Shortlink">A short link to its page, such as <c>https://stspg.io/…</c>.</param>
public sealed record StatusIncident(
    string Id,
    string Name,
    IncidentStatus Status,
    IncidentImpact Impact,
    string? Shortlink,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<StatusComponent> Components)
{
    /// <summary>Resolved, or written up afterwards: over.</summary>
    public bool IsResolved => Status is IncidentStatus.Resolved or IncidentStatus.Postmortem;
}

/// <summary>A maintenance on the status page, upcoming or under way.</summary>
public sealed record StatusMaintenance(
    string Id,
    string Name,
    MaintenanceStatus Status,
    string? Shortlink,
    DateTimeOffset? ScheduledFor,
    DateTimeOffset? ScheduledUntil,
    IReadOnlyList<StatusComponent> Components)
{
    /// <summary>Under way now, rather than scheduled or done.</summary>
    public bool IsInProgress => Status is MaintenanceStatus.InProgress or MaintenanceStatus.Verifying;
}

/// <summary>
/// Claude's status page as its Atlassian Statuspage v2 summary gives it (<c>/api/v2/summary.json</c>, DESIGN.md §18,
/// "Service status"): the roll-up indicator, every component, the incidents that aren't resolved, and the scheduled
/// maintenances. Read tolerantly: unknown fields are ignored and unknown values read as <c>Unknown</c>.
/// </summary>
public sealed record StatusSummary(
    StatusIndicator Indicator,
    string? Description,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<StatusComponent> Components,
    IReadOnlyList<StatusIncident> Incidents,
    IReadOnlyList<StatusMaintenance> Maintenances)
{
    /// <summary>Reads a summary. Null when it isn't JSON, or isn't an object: the status is unknown then.</summary>
    public static StatusSummary? TryParse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? Read(document.RootElement) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static StatusSummary Read(JsonElement root)
    {
        var status = Object(root, "status");
        var page = Object(root, "page");
        return new StatusSummary(
            status is { } s ? ToIndicator(Text(s, "indicator")) : StatusIndicator.Unknown,
            status is { } d ? Text(d, "description") : null,
            page is { } p ? Time(p, "updated_at") : null,
            ReadComponents(root, "components"),
            Items(root, "incidents", ReadIncident),
            Items(root, "scheduled_maintenances", ReadMaintenance));
    }

    private static StatusIncident? ReadIncident(JsonElement item) =>
        Text(item, "id") is { Length: > 0 } id
            ? new StatusIncident(id, Text(item, "name") ?? "", ToIncidentStatus(Text(item, "status")), ToImpact(Text(item, "impact")), Text(item, "shortlink"),
                Time(item, "updated_at"), ReadComponents(item, "components"))
            : null;

    private static StatusMaintenance? ReadMaintenance(JsonElement item) =>
        Text(item, "id") is { Length: > 0 } id
            ? new StatusMaintenance(id, Text(item, "name") ?? "", ToMaintenanceStatus(Text(item, "status")), Text(item, "shortlink"),
                Time(item, "scheduled_for"), Time(item, "scheduled_until"), ReadComponents(item, "components"))
            : null;

    private static IReadOnlyList<StatusComponent> ReadComponents(JsonElement parent, string name) =>
        Items(parent, name, item => Text(item, "name") is { Length: > 0 } component
            ? new StatusComponent(Text(item, "id") ?? "", component, ToComponentStatus(Text(item, "status")))
            : null);

    private static IReadOnlyList<T> Items<T>(JsonElement parent, string name, Func<JsonElement, T?> read) where T : class
    {
        if (!parent.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var items = new List<T>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && read(item) is { } value)
            {
                items.Add(value);
            }
        }
        return items;
    }

    private static StatusIndicator ToIndicator(string? value) => value switch
    {
        "none" => StatusIndicator.None,
        "minor" => StatusIndicator.Minor,
        "major" => StatusIndicator.Major,
        "critical" => StatusIndicator.Critical,
        "maintenance" => StatusIndicator.Maintenance,
        _ => StatusIndicator.Unknown,
    };

    private static ComponentStatus ToComponentStatus(string? value) => value switch
    {
        "operational" => ComponentStatus.Operational,
        "degraded_performance" => ComponentStatus.DegradedPerformance,
        "partial_outage" => ComponentStatus.PartialOutage,
        "major_outage" => ComponentStatus.MajorOutage,
        "under_maintenance" => ComponentStatus.UnderMaintenance,
        _ => ComponentStatus.Unknown,
    };

    private static IncidentStatus ToIncidentStatus(string? value) => value switch
    {
        "investigating" => IncidentStatus.Investigating,
        "identified" => IncidentStatus.Identified,
        "monitoring" => IncidentStatus.Monitoring,
        "resolved" => IncidentStatus.Resolved,
        "postmortem" => IncidentStatus.Postmortem,
        _ => IncidentStatus.Unknown,
    };

    private static IncidentImpact ToImpact(string? value) => value switch
    {
        "none" => IncidentImpact.None,
        "minor" => IncidentImpact.Minor,
        "major" => IncidentImpact.Major,
        "critical" => IncidentImpact.Critical,
        "maintenance" => IncidentImpact.Maintenance,
        _ => IncidentImpact.Unknown,
    };

    private static MaintenanceStatus ToMaintenanceStatus(string? value) => value switch
    {
        "scheduled" => MaintenanceStatus.Scheduled,
        "in_progress" => MaintenanceStatus.InProgress,
        "verifying" => MaintenanceStatus.Verifying,
        "completed" => MaintenanceStatus.Completed,
        _ => MaintenanceStatus.Unknown,
    };

    private static JsonElement? Object(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? Time(JsonElement element, string name) =>
        Text(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;
}
