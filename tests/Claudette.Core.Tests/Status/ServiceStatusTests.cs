using System.Net;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using Claudette.Core.Status;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.Status;

/// <summary>
/// Claude's status page read and worked out (DESIGN.md §18, "Service status"): the Statuspage summary, the watched
/// services and their level, the incidents that concern them, dismissing the banner, and which API errors are worth
/// checking the page for.
/// </summary>
public class ServiceStatusTests
{
    /// <summary>The real summary during an incident on 2026-09-29, trimmed.</summary>
    private static readonly string Incident = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "status", "summary-incident.json"));

    private static readonly DateTimeOffset Now = new(2026, 9, 29, 14, 45, 0, TimeSpan.Zero);

    private static string Summary(string indicator = "none", string components = "", string incidents = "", string maintenances = "") => $$"""
        {
          "page": { "id": "p", "name": "Claude", "url": "https://status.claude.com", "updated_at": "2026-09-29T14:00:00Z" },
          "status": { "indicator": "{{indicator}}", "description": "Whatever" },
          "components": [ {{components}} ],
          "incidents": [ {{incidents}} ],
          "scheduled_maintenances": [ {{maintenances}} ]
        }
        """;

    private static string Component(string id, string name, string status) => $$"""{ "id": "{{id}}", "name": "{{name}}", "status": "{{status}}", "group": false }""";

    private static string Watched(string code = "operational", string api = "operational", string web = "operational") =>
        string.Join(",", Component("code", "Claude Code", code), Component("api", "Claude API (api.anthropic.com)", api), Component("web", "claude.ai", web),
            Component("console", "Claude Console (platform.claude.com)", "operational"));

    private static string IncidentJson(string id, string status = "investigating", string updated = "2026-09-29T14:30:00Z", string components = "") => $$"""
        { "id": "{{id}}", "name": "Incident {{id}}", "status": "{{status}}", "impact": "minor", "shortlink": "https://stspg.io/{{id}}",
          "updated_at": "{{updated}}", "components": [ {{components}} ] }
        """;

    private static ServiceStatusReport Report(string json) => ServiceStatusReport.From(StatusSummary.TryParse(json)!, Now);

    // ---- The summary -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_sample_reads_as_a_minor_indicator_with_Claude_Code_partly_down_and_one_major_incident()
    {
        var summary = StatusSummary.TryParse(Incident)!;

        Assert.Equal(StatusIndicator.Minor, summary.Indicator);
        Assert.Equal("Minor Service Outage", summary.Description);
        Assert.Equal(DateTimeOffset.Parse("2026-09-29T14:41:08.294Z"), summary.UpdatedAt);
        Assert.Equal(6, summary.Components.Count);
        Assert.Equal(new StatusComponent("yyzkbfz2thpt", "Claude Code", ComponentStatus.PartialOutage), summary.Components.Single(c => c.Name == "Claude Code"));
        Assert.Equal(ComponentStatus.DegradedPerformance, summary.Components.Single(c => c.Name == "Claude API (api.anthropic.com)").Status);
        var incident = Assert.Single(summary.Incidents);
        Assert.Equal("4xvtc2gnq73l", incident.Id);
        Assert.Equal("Elevated errors on claude.ai, Claude Code, Claude Cowork and the Claude API", incident.Name);
        Assert.Equal(IncidentStatus.Investigating, incident.Status);
        Assert.Equal(IncidentImpact.Major, incident.Impact);
        Assert.Equal("https://stspg.io/br61xzj05pp5", incident.Shortlink);
        Assert.Equal(DateTimeOffset.Parse("2026-09-29T14:41:08.276Z"), incident.UpdatedAt);
        Assert.Equal(5, incident.Components.Count);
        Assert.False(incident.IsResolved);
        Assert.Empty(summary.Maintenances);
    }

    [Fact]
    public void The_sample_is_an_outage_of_every_watched_service_with_its_incident()
    {
        var report = Report(Incident);

        Assert.Equal(ServiceLevel.Outage, report.Level);
        Assert.Equal(
            [("Claude Code", ComponentStatus.PartialOutage), ("Claude API", ComponentStatus.DegradedPerformance), ("claude.ai", ComponentStatus.PartialOutage)],
            report.Services.Select(s => (s.Name, s.Status)));
        Assert.Equal("4xvtc2gnq73l", report.LatestIncident?.Id);
        Assert.True(report.HasProblem);
        Assert.True(report.ShowsBanner);
        Assert.False(report.IsMaintenance);
        Assert.False(report.IsAllClear);
        Assert.Equal(["4xvtc2gnq73l"], report.BannerIds);
        Assert.Equal(Now, report.AsOf);
    }

    [Fact]
    public void All_operational_is_all_clear()
    {
        var report = Report(Summary(components: Watched()));

        Assert.Equal(ServiceLevel.Operational, report.Level);
        Assert.All(report.Services, s => Assert.Equal(ComponentStatus.Operational, s.Status));
        Assert.Empty(report.Incidents);
        Assert.True(report.IsAllClear);
        Assert.False(report.ShowsBanner);
    }

    [Fact]
    public void Maintenance_under_way_on_a_watched_service_is_maintenance_but_scheduled_or_elsewhere_is_not()
    {
        var maintenances = string.Join(",",
            """{ "id": "m1", "name": "Database upgrade", "status": "in_progress", "shortlink": "https://stspg.io/m1", "scheduled_for": "2026-09-29T14:00:00Z", "scheduled_until": "2026-09-29T16:00:00Z", "components": [ { "id": "api", "name": "Claude API (api.anthropic.com)", "status": "under_maintenance" } ] }""",
            """{ "id": "m2", "name": "Next week", "status": "scheduled", "components": [ { "id": "code", "name": "Claude Code", "status": "operational" } ] }""",
            """{ "id": "m3", "name": "Console only", "status": "in_progress", "components": [ { "id": "console", "name": "Claude Console (platform.claude.com)", "status": "under_maintenance" } ] }""",
            """{ "id": "m4", "name": "Done", "status": "completed", "components": [] }""");
        var summary = StatusSummary.TryParse(Summary("maintenance", Watched(api: "under_maintenance"), maintenances: maintenances))!;
        var report = ServiceStatusReport.From(summary, Now);

        Assert.Equal(4, summary.Maintenances.Count);
        Assert.Equal(MaintenanceStatus.InProgress, summary.Maintenances[0].Status);
        Assert.Equal(DateTimeOffset.Parse("2026-09-29T16:00:00Z"), summary.Maintenances[0].ScheduledUntil);
        Assert.Equal(StatusIndicator.Maintenance, summary.Indicator);
        Assert.Equal(ServiceLevel.Maintenance, report.Level);
        Assert.Equal(["m1"], report.Maintenances.Select(m => m.Id));
        Assert.True(report.IsMaintenance);
        Assert.False(report.HasProblem);
        Assert.True(report.ShowsBanner);
        Assert.Equal(["m1"], report.BannerIds);

        // Maintenance under way while everything is still operational is maintenance too.
        Assert.Equal(ServiceLevel.Maintenance, Report(Summary(components: Watched(), maintenances: maintenances)).Level);
    }

    [Fact]
    public void Values_Claudette_does_not_know_read_as_unknown_and_an_unknown_incident_status_is_still_open()
    {
        var incident = IncidentJson("new", status: "escalated", components: Component("code", "Claude Code", "on_fire")).Replace("\"minor\"", "\"severe\"", StringComparison.Ordinal);
        var summary = StatusSummary.TryParse(Summary("catastrophic", Watched(code: "on_fire"), incident))!;
        var report = ServiceStatusReport.From(summary, Now);

        Assert.Equal(StatusIndicator.Unknown, summary.Indicator);
        Assert.Equal(ComponentStatus.Unknown, summary.Components[0].Status);
        Assert.Equal(IncidentStatus.Unknown, summary.Incidents[0].Status);
        Assert.Equal(IncidentImpact.Unknown, summary.Incidents[0].Impact);
        Assert.False(summary.Incidents[0].IsResolved);
        // Claude Code's status is unknown; the others are fine, but the open incident on Claude Code makes it degraded.
        Assert.Equal(ComponentStatus.Unknown, report.Services[0].Status);
        Assert.Equal(ServiceLevel.Degraded, report.Level);
        Assert.Equal("new", report.LatestIncident?.Id);
    }

    [Fact]
    public void Without_the_watched_services_listed_the_page_indicator_decides_and_an_unknown_one_is_unknown()
    {
        var other = Component("gov", "Claude for Government", "major_outage");

        Assert.Equal(ServiceLevel.Operational, Report(Summary("none", other)).Level);
        Assert.Equal(ServiceLevel.Degraded, Report(Summary("minor", other)).Level);
        Assert.Equal(ServiceLevel.Outage, Report(Summary("critical", other)).Level);
        var unknown = Report(Summary("purple", other));
        Assert.Equal(ServiceLevel.Unknown, unknown.Level);
        Assert.False(unknown.ShowsBanner);
        Assert.All(unknown.Services, s => Assert.Equal(ComponentStatus.Unknown, s.Status));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{ \"status\": ")]
    public void Malformed_answers_read_as_nothing(string json) => Assert.Null(StatusSummary.TryParse(json));

    [Fact]
    public void Wrongly_typed_parts_are_skipped_and_an_empty_summary_is_unknown()
    {
        var summary = StatusSummary.TryParse("""
            { "status": "fine", "components": [ 42, { "name": 7 }, { "id": "code", "name": "Claude Code", "status": "operational" } ],
              "incidents": { "not": "a list" }, "scheduled_maintenances": [ { "name": "no id" } ] }
            """)!;

        Assert.Equal(StatusIndicator.Unknown, summary.Indicator);
        Assert.Equal(["Claude Code"], summary.Components.Select(c => c.Name));
        Assert.Empty(summary.Incidents);
        Assert.Empty(summary.Maintenances);
        Assert.Equal(ServiceLevel.Unknown, ServiceStatusReport.From(StatusSummary.TryParse("{}")!, Now).Level);
    }

    // ---- Levels and names ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ComponentStatus.Operational, ServiceLevel.Operational)]
    [InlineData(ComponentStatus.DegradedPerformance, ServiceLevel.Degraded)]
    [InlineData(ComponentStatus.PartialOutage, ServiceLevel.Outage)]
    [InlineData(ComponentStatus.MajorOutage, ServiceLevel.Outage)]
    [InlineData(ComponentStatus.UnderMaintenance, ServiceLevel.Maintenance)]
    [InlineData(ComponentStatus.Unknown, ServiceLevel.Unknown)]
    public void Component_statuses_map_to_levels(ComponentStatus status, ServiceLevel level) => Assert.Equal(level, ServiceStatusReport.LevelOf(status));

    [Fact]
    public void The_worst_watched_service_sets_the_level()
    {
        Assert.Equal(ServiceLevel.Degraded, Report(Summary(components: Watched(web: "degraded_performance"))).Level);
        Assert.Equal(ServiceLevel.Outage, Report(Summary(components: Watched(api: "major_outage", web: "degraded_performance"))).Level);
        // Services Claudette doesn't watch don't count.
        Assert.Equal(ServiceLevel.Operational, Report(Summary("major", Watched() + "," + Component("gov", "Claude for Government", "major_outage"))).Level);
    }

    [Theory]
    [InlineData("Claude Code", WatchedService.ClaudeCode)]
    [InlineData("  claude code ", WatchedService.ClaudeCode)]
    [InlineData("Claude API (api.anthropic.com)", WatchedService.ClaudeApi)]
    [InlineData("Claude API", WatchedService.ClaudeApi)]
    [InlineData("api.anthropic.com", WatchedService.ClaudeApi)]
    [InlineData("claude.ai", WatchedService.ClaudeAi)]
    [InlineData("Claude.ai", WatchedService.ClaudeAi)]
    [InlineData("Claude Console (platform.claude.com)", null)]
    [InlineData("Claude Cowork", null)]
    [InlineData("Claude Code on the web", null)]
    [InlineData("claude.ai (mobile)", null)]
    public void Watched_services_are_matched_by_name(string name, WatchedService? expected) => Assert.Equal(expected, ServiceStatusReport.Match(name));

    // ---- Incidents ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Open_incidents_on_watched_services_or_naming_none_count_newest_first()
    {
        var incidents = string.Join(",",
            IncidentJson("older", updated: "2026-09-29T13:00:00Z", components: Component("code", "Claude Code", "degraded_performance")),
            IncidentJson("console", components: Component("console", "Claude Console (platform.claude.com)", "degraded_performance")),
            IncidentJson("unnamed", updated: "2026-09-29T14:40:00Z"),
            IncidentJson("done", status: "resolved", components: Component("api", "Claude API (api.anthropic.com)", "operational")),
            IncidentJson("written-up", status: "postmortem"),
            // Named by id only: the watched component's id still counts.
            IncidentJson("by-id", updated: "2026-09-29T14:35:00Z", components: Component("web", "Renamed", "operational")));
        var report = Report(Summary(components: Watched(), incidents: incidents));

        Assert.Equal(["unnamed", "by-id", "older"], report.Incidents.Select(i => i.Id));
        // The services are fine, but open incidents make it degraded.
        Assert.Equal(ServiceLevel.Degraded, report.Level);
        Assert.True(report.HasProblem);
    }

    // ---- Dismissing -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_dismissal_covers_the_same_incidents_until_a_new_one_or_worse()
    {
        var degraded = Report(Summary(components: Watched(code: "degraded_performance"), incidents: IncidentJson("a", components: Component("code", "Claude Code", "degraded_performance"))));
        var dismissal = ServiceStatusDismissal.Of(degraded);

        Assert.Equal(["a"], dismissal.IncidentIds);
        Assert.Equal(ServiceLevel.Degraded, dismissal.Level);
        Assert.True(dismissal.Covers(degraded));
        // Recovering, with the same incident still open: still dismissed.
        Assert.True(dismissal.Covers(Report(Summary(components: Watched(), incidents: IncidentJson("a", status: "monitoring")))));
        // A different incident.
        Assert.False(dismissal.Covers(Report(Summary(components: Watched(code: "degraded_performance"), incidents: IncidentJson("b")))));
        // Worse.
        Assert.False(dismissal.Covers(Report(Summary(components: Watched(code: "major_outage"), incidents: IncidentJson("a")))));
    }

    // ---- API errors worth a check ------------------------------------------------------------------------------------------

    private static SessionEvent Parse(string line)
    {
        Assert.True(MessageParser.TryParse(line, out var message, out var error), error);
        return message switch
        {
            SystemMessage system => new SystemNotice(system),
            ToolProgressMessage progress => new ToolProgress(progress),
            ResultMessage result => new TurnCompleted(result),
            _ => throw new InvalidOperationException($"Not expected here: {message.GetType().Name}"),
        };
    }

    [Theory]
    [InlineData("""{"type":"system","subtype":"api_retry","attempt":1,"max_retries":10,"retry_delay_ms":567,"error_status":529,"error":"overloaded","session_id":"s"}""", true)]
    [InlineData("""{"type":"system","subtype":"api_retry","attempt":1,"error_status":500,"error":"server_error","session_id":"s"}""", true)]
    [InlineData("""{"type":"system","subtype":"api_retry","attempt":1,"error_status":null,"error":"overloaded","session_id":"s"}""", true)]
    [InlineData("""{"type":"system","subtype":"api_retry","attempt":1,"error_status":429,"error":"rate_limit","session_id":"s"}""", false)]
    [InlineData("""{"type":"system","subtype":"api_retry","attempt":1,"error_status":null,"error":"unknown","session_id":"s"}""", false)]
    [InlineData("""{"type":"system","subtype":"status","status":"requesting","session_id":"s"}""", false)]
    [InlineData("""{"type":"tool_progress","tool_use_id":"t","tool_name":"Agent","parent_tool_use_id":"p","elapsed_time_seconds":3,"subagent_retry":{"agent_id":"a","attempt":1,"max_retries":10,"retry_delay_ms":500,"error_status":529,"error_category":"overloaded"},"session_id":"s"}""", true)]
    [InlineData("""{"type":"tool_progress","tool_use_id":"t","tool_name":"Agent","parent_tool_use_id":"p","elapsed_time_seconds":3,"subagent_retry":{"agent_id":"a","attempt":1,"max_retries":10,"retry_delay_ms":500,"error_status":401,"error_category":"authentication_failed"},"session_id":"s"}""", false)]
    [InlineData("""{"type":"tool_progress","tool_use_id":"t","tool_name":"Bash","parent_tool_use_id":null,"elapsed_time_seconds":30,"heartbeat":true,"session_id":"s"}""", false)]
    [InlineData("""{"type":"result","subtype":"success","is_error":true,"api_error_status":529,"result":"API Error: 529 Overloaded","session_id":"s"}""", true)]
    [InlineData("""{"type":"result","subtype":"success","is_error":true,"api_error_status":400,"result":"API Error: 400","session_id":"s"}""", false)]
    [InlineData("""{"type":"result","subtype":"success","is_error":false,"api_error_status":null,"result":"pong","session_id":"s"}""", false)]
    public void API_errors_on_Anthropics_side_are_worth_a_check_and_the_users_own_are_not(string line, bool expected) =>
        Assert.Equal(expected, ApiTrouble.Reports(Parse(line)));

    // ---- The feed -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_feed_asks_for_the_summary_with_Claudettes_user_agent()
    {
        using var handler = new FakeHttpHandler().OnJson(StatusFeed.SummaryUrl, Incident);
        using var http = new HttpClient(handler);

        var summary = await new StatusFeed(http, "Claudette/1.2.0").GetSummaryAsync(TestContext.Current.CancellationToken);

        Assert.Equal(StatusIndicator.Minor, summary.Indicator);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://status.claude.com/api/v2/summary.json", request.RequestUri!.ToString());
        Assert.Equal("Claudette/1.2.0", request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task A_failed_or_unreadable_answer_is_a_feed_error()
    {
        using var handler = new FakeHttpHandler()
            .On(StatusFeed.SummaryUrl, _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { ReasonPhrase = "Service Unavailable" });
        using var http = new HttpClient(handler);
        var feed = new StatusFeed(http, "Claudette/1.2.0");

        var down = await Assert.ThrowsAsync<StatusFeedException>(() => feed.GetSummaryAsync(TestContext.Current.CancellationToken));
        Assert.Equal("status.claude.com answered 503 Service Unavailable.", down.Message);

        handler.OnJson(StatusFeed.SummaryUrl, "<html>Oops</html>");
        var garbled = await Assert.ThrowsAsync<StatusFeedException>(() => feed.GetSummaryAsync(TestContext.Current.CancellationToken));
        Assert.Equal("status.claude.com's answer couldn't be read.", garbled.Message);

        using var offline = new HttpClient(new ThrowingHandler());
        var unreachable = await Assert.ThrowsAsync<StatusFeedException>(() => new StatusFeed(offline, "Claudette/1.2.0").GetSummaryAsync(TestContext.Current.CancellationToken));
        Assert.StartsWith("Couldn't reach status.claude.com:", unreachable.Message);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No network in tests.");
    }
}
