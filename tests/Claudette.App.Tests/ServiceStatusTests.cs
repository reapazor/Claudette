using System.Net;
using System.Text.Json.Nodes;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Settings;
using Claudette.Core.Status;
using Claudette.Core.Tests.Support;

namespace Claudette.App.Tests;

/// <summary>
/// Claude's service status (DESIGN.md §18, "Service status"): reading status.claude.com at launch, every 5 minutes and
/// when a tab's API requests fail, backing off when it can't be read, the header's dot, and the banner with Dismiss.
/// The status page is a <see cref="FakeHttpHandler"/>: nothing reaches the network.
/// </summary>
public class ServiceStatusTests
{
    private static readonly string Incident = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "status", "summary-incident.json"));

    private static string Component(string id, string name, string status) => $$"""{ "id": "{{id}}", "name": "{{name}}", "status": "{{status}}" }""";

    /// <summary>A summary with the watched services at the given statuses, and the given incidents and maintenances.</summary>
    private static string Summary(string code = "operational", string api = "operational", string web = "operational", string incidents = "", string maintenances = "") => $$"""
        {
          "status": { "indicator": "none", "description": "All Systems Operational" },
          "components": [ {{Component("code", "Claude Code", code)}}, {{Component("api", "Claude API (api.anthropic.com)", api)}}, {{Component("web", "claude.ai", web)}} ],
          "incidents": [ {{incidents}} ],
          "scheduled_maintenances": [ {{maintenances}} ]
        }
        """;

    private static string IncidentJson(string id, string name = "Elevated errors on Claude Code", string status = "investigating") => $$"""
        { "id": "{{id}}", "name": "{{name}}", "status": "{{status}}", "impact": "minor", "shortlink": "https://stspg.io/{{id}}",
          "updated_at": "2026-09-28T11:50:00Z", "components": [ {{Component("code", "Claude Code", "degraded_performance")}} ] }
        """;

    private sealed class Setup(TabTestHarness harness, FakeHttpHandler page, MainWindowViewModel main) : IAsyncDisposable
    {
        public TabTestHarness H => harness;

        public FakeHttpHandler Page => page;

        public MainWindowViewModel Main => main;

        public ServiceStatusService Service => harness.Services.ServiceStatus;

        public ServiceStatusViewModel Status => main.ServiceStatus;

        public int Requests
        {
            get
            {
                lock (page.Requests)
                {
                    return page.Requests.Count;
                }
            }
        }

        /// <summary>The status page answers with <paramref name="json"/> from now on.</summary>
        public void Answer(string json) => page.OnJson(StatusFeed.SummaryUrl, json);

        public void Fail() => page.On(StatusFeed.SummaryUrl, _ => new HttpResponseMessage(HttpStatusCode.BadGateway) { ReasonPhrase = "Bad Gateway" });

        /// <summary>Waits for the <paramref name="count"/>th request, and for its check to finish.</summary>
        public async Task CheckedAsync(int count)
        {
            await TabTestHarness.Eventually(() => Requests >= count, $"check {count}");
            await Service.LastCheck!;
            Assert.Equal(count, Requests);
        }

        public async ValueTask DisposeAsync()
        {
            await main.DisposeAsync();
            await harness.DisposeAsync();
        }
    }

    private static Setup Create(string? summary = null, Action<AppSettings>? configure = null)
    {
        var page = new FakeHttpHandler().OnJson(StatusFeed.SummaryUrl, summary ?? Summary());
        var h = new TabTestHarness(configure, http: page);
        return new Setup(h, page, new MainWindowViewModel(h.Services));
    }

    [Fact]
    public async Task It_checks_at_launch_and_the_dot_shows_the_watched_services()
    {
        await using var s = Create(Incident);
        Assert.False(s.Status.IsVisible);

        s.Service.Start();
        await s.CheckedAsync(1);

        var request = s.Page.Requests[0];
        Assert.Equal("https://status.claude.com/api/v2/summary.json", request.RequestUri!.ToString());
        Assert.Equal($"Claudette/{TabTestHarness.TestVersion}", request.Headers.UserAgent.ToString());
        Assert.True(s.Status.IsVisible);
        Assert.Equal(ServiceLevel.Outage, s.Status.Level);
        Assert.True(s.Status.IsOutage);
        Assert.False(s.Status.IsDegraded || s.Status.IsOperational);
        Assert.Equal("Claude's service status: Claude has an outage", s.Status.AccessibleName);
        // The fake clock's local time is UTC.
        Assert.Equal("""
            Claude has an outage
            Claude Code: Partial outage
            Claude API: Degraded performance
            claude.ai: Partial outage
            Latest incident: Elevated errors on claude.ai, Claude Code, Claude Cowork and the Claude API (investigating)
            as of 12:00
            Click to open status.claude.com
            """.ReplaceLineEndings("\n"), s.Status.Tooltip);

        s.Status.OpenStatusPageCommand.Execute(null);
        Assert.Equal(["https://status.claude.com"], s.H.Platform.OpenedUrls);
    }

    [Fact]
    public async Task It_checks_again_every_5_minutes()
    {
        await using var s = Create();
        s.Service.Start();
        await s.CheckedAsync(1);
        Assert.True(s.Status.IsOperational);
        Assert.StartsWith("Claude is operational\nClaude Code: Operational\n", s.Status.Tooltip);

        s.H.Time.Advance(ServiceStatusService.CheckInterval - TimeSpan.FromSeconds(1));
        Assert.Equal(1, s.Requests);
        s.H.Time.Advance(TimeSpan.FromSeconds(1));
        await s.CheckedAsync(2);

        s.Answer(Summary(web: "degraded_performance"));
        s.H.Time.Advance(ServiceStatusService.CheckInterval);
        await s.CheckedAsync(3);
        Assert.True(s.Status.IsDegraded);
        Assert.Equal("Claude is having problems", s.Status.LevelText);
    }

    [Fact]
    public async Task A_failed_check_is_status_unknown_and_backs_off_up_to_30_minutes()
    {
        await using var s = Create();
        s.Fail();
        s.Service.Start();
        await s.CheckedAsync(1);

        Assert.Equal(ServiceLevel.Unknown, s.Status.Level);
        Assert.False(s.Status.IsOperational || s.Status.IsDegraded || s.Status.IsOutage);
        Assert.Equal("Claude's service status: Status unknown", s.Status.AccessibleName);
        Assert.Equal("Status unknown\nstatus.claude.com answered 502 Bad Gateway.\nas of 12:00\nClick to open status.claude.com", s.Status.Tooltip);
        Assert.False(s.Status.ShowBanner);

        // 5 minutes, then 10, 20, and 30 from then on.
        var expected = new[] { 5, 10, 20, 30, 30 };
        for (var i = 0; i < expected.Length; i++)
        {
            var now = s.H.Time.GetUtcNow();
            Assert.Equal(now + TimeSpan.FromMinutes(expected[i]), s.Service.NextCheck);
            s.H.Time.Advance(TimeSpan.FromMinutes(expected[i]) - TimeSpan.FromSeconds(1));
            Assert.Equal(i + 1, s.Requests);
            s.H.Time.Advance(TimeSpan.FromSeconds(1));
            await s.CheckedAsync(i + 2);
        }

        // Back: every 5 minutes again.
        s.Answer(Summary());
        s.H.Time.Advance(TimeSpan.FromMinutes(30));
        await s.CheckedAsync(expected.Length + 2);
        Assert.True(s.Status.IsOperational);
        Assert.Equal(s.H.Time.GetUtcNow() + ServiceStatusService.CheckInterval, s.Service.NextCheck);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 30)]
    [InlineData(40, 30)]
    public void The_backoff_doubles_to_30_minutes(int failures, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), ServiceStatusService.Backoff(failures));

    [Fact]
    public async Task An_API_error_checks_straight_away_but_at_most_once_a_minute()
    {
        await using var s = Create();
        s.Service.Start();
        await s.CheckedAsync(1);

        // Just after a check: the next one is brought forward to a minute after it, not sooner.
        s.H.Time.Advance(TimeSpan.FromSeconds(20));
        s.Service.OnApiTrouble();
        Assert.Equal(1, s.Requests);
        Assert.Equal(s.H.Time.GetUtcNow() + TimeSpan.FromSeconds(40), s.Service.NextCheck);
        s.H.Time.Advance(TimeSpan.FromSeconds(40));
        await s.CheckedAsync(2);
        Assert.Equal(s.H.Time.GetUtcNow() + ServiceStatusService.CheckInterval, s.Service.NextCheck);

        // More than a minute since: straight away.
        s.H.Time.Advance(TimeSpan.FromMinutes(2));
        s.Service.OnApiTrouble();
        await s.CheckedAsync(3);
        s.Service.OnApiTrouble();
        s.Service.OnApiTrouble();
        Assert.Equal(3, s.Requests);
    }

    [Fact]
    public async Task A_tab_retrying_an_overloaded_request_asks_for_a_check()
    {
        await using var s = Create();
        s.Service.Start();
        await s.CheckedAsync(1);
        await s.H.OpenTabAsync();
        s.H.Time.Advance(TimeSpan.FromMinutes(2));

        // A rate limit is the user's own: no check.
        s.H.Transport.Emit(new JsonObject { ["type"] = "system", ["subtype"] = "api_retry", ["attempt"] = 1, ["error_status"] = 429, ["error"] = "rate_limit", ["session_id"] = "s1" });
        s.H.Transport.EmitTurn("pong");
        await TabTestHarness.Eventually(() => s.H.Shell.SelectedTab!.Items.OfType<Conversation.TurnSummaryItem>().Any(), "the turn");
        Assert.Equal(1, s.Requests);

        s.H.Transport.Emit(new JsonObject { ["type"] = "system", ["subtype"] = "api_retry", ["attempt"] = 1, ["error_status"] = 529, ["error"] = "overloaded", ["session_id"] = "s1" });
        await s.CheckedAsync(2);
    }

    [Fact]
    public async Task The_banner_shows_an_incident_until_dismissed_and_comes_back_for_a_new_one()
    {
        await using var s = Create(Incident);
        s.Service.Start();
        await s.CheckedAsync(1);

        Assert.True(s.Status.ShowBanner);
        Assert.False(s.Status.IsInfoBanner);
        Assert.Equal("Claude is having problems:", s.Status.BannerTitle);
        Assert.Equal("Elevated errors on claude.ai, Claude Code, Claude Cowork and the Claude API (investigating)", s.Status.BannerText);
        s.Status.OpenBannerLinkCommand.Execute(null);
        Assert.Equal(["https://stspg.io/br61xzj05pp5"], s.H.Platform.OpenedUrls);

        s.Status.DismissBannerCommand.Execute(null);
        Assert.False(s.Status.ShowBanner);
        Assert.Equal(["4xvtc2gnq73l"], s.H.Services.State.DismissedServiceStatus?.IncidentIds);
        // The dot still says what's wrong.
        Assert.True(s.Status.IsOutage);

        // The same incident, later: still dismissed.
        s.H.Time.Advance(ServiceStatusService.CheckInterval);
        await s.CheckedAsync(2);
        Assert.False(s.Status.ShowBanner);

        // A different incident shows again.
        s.Answer(Summary(code: "degraded_performance", incidents: IncidentJson("second")));
        s.H.Time.Advance(ServiceStatusService.CheckInterval);
        await s.CheckedAsync(3);
        Assert.True(s.Status.ShowBanner);
        Assert.Equal("Elevated errors on Claude Code (investigating)", s.Status.BannerText);
        Assert.True(s.Status.IsDegraded);
    }

    [Fact]
    public async Task A_dismissed_banner_comes_back_when_things_get_worse()
    {
        await using var s = Create(Summary(code: "degraded_performance", incidents: IncidentJson("one")));
        s.Service.Start();
        await s.CheckedAsync(1);
        s.Status.DismissBannerCommand.Execute(null);
        Assert.False(s.Status.ShowBanner);

        s.Answer(Summary(code: "major_outage", incidents: IncidentJson("one", status: "identified")));
        s.H.Time.Advance(ServiceStatusService.CheckInterval);
        await s.CheckedAsync(2);

        Assert.True(s.Status.ShowBanner);
        Assert.Equal("Elevated errors on Claude Code (identified)", s.Status.BannerText);
    }

    [Fact]
    public async Task The_banner_goes_by_itself_when_everything_is_operational_and_the_dismissal_is_forgotten()
    {
        await using var s = Create(Summary(web: "partial_outage"));
        s.Service.Start();
        await s.CheckedAsync(1);
        // No incident yet: the services that aren't well.
        Assert.True(s.Status.ShowBanner);
        Assert.Equal("claude.ai (partial outage)", s.Status.BannerText);
        s.Status.DismissBannerCommand.Execute(null);
        Assert.NotNull(s.H.Services.State.DismissedServiceStatus);

        s.Answer(Summary());
        s.H.Time.Advance(ServiceStatusService.CheckInterval);
        await s.CheckedAsync(2);

        Assert.False(s.Status.ShowBanner);
        Assert.True(s.Status.IsOperational);
        Assert.Null(s.H.Services.State.DismissedServiceStatus);

        // So the same trouble again shows.
        s.Answer(Summary(web: "partial_outage"));
        s.H.Time.Advance(ServiceStatusService.CheckInterval);
        await s.CheckedAsync(3);
        Assert.True(s.Status.ShowBanner);
    }

    [Fact]
    public async Task Maintenance_gets_the_quieter_banner()
    {
        const string maintenance = """
            { "id": "m1", "name": "Scheduled database maintenance", "status": "in_progress", "shortlink": "https://stspg.io/m1",
              "components": [ { "id": "api", "name": "Claude API (api.anthropic.com)", "status": "under_maintenance" } ] }
            """;
        await using var s = Create(Summary(api: "under_maintenance", maintenances: maintenance));
        s.Service.Start();
        await s.CheckedAsync(1);

        Assert.True(s.Status.ShowBanner);
        Assert.True(s.Status.IsInfoBanner);
        Assert.True(s.Status.IsDegraded);
        Assert.Equal("Claude maintenance in progress:", s.Status.BannerTitle);
        Assert.Equal("Scheduled database maintenance", s.Status.BannerText);
        Assert.Contains("\nMaintenance: Scheduled database maintenance\n", s.Status.Tooltip);
        s.Status.OpenBannerLinkCommand.Execute(null);
        Assert.Equal(["https://stspg.io/m1"], s.H.Platform.OpenedUrls);
    }

    [Fact]
    public async Task Turning_the_setting_off_hides_the_dot_and_banner_and_stops_the_requests()
    {
        await using var s = Create(Incident);
        s.Service.Start();
        await s.CheckedAsync(1);
        Assert.True(s.Status.ShowBanner);

        using var settings = new SettingsViewModel(s.H.Services, null) { ShowServiceStatus = false };
        Assert.False(s.H.Services.Settings.General.ShowServiceStatus);
        Assert.False(s.Status.IsVisible);
        Assert.False(s.Status.ShowBanner);

        s.H.Time.Advance(TimeSpan.FromHours(2));
        s.Service.OnApiTrouble();
        await s.Service.CheckNowAsync();
        Assert.Equal(1, s.Requests);

        // On again: a check straight away.
        settings.ShowServiceStatus = true;
        await s.CheckedAsync(2);
        Assert.True(s.Status.IsVisible);
        Assert.True(s.Status.ShowBanner);
    }

    [Fact]
    public async Task Off_at_launch_never_asks()
    {
        await using var s = Create(configure: settings => settings.General.ShowServiceStatus = false);

        s.Service.Start();
        s.H.Time.Advance(TimeSpan.FromHours(1));
        s.Service.OnApiTrouble();

        Assert.Equal(0, s.Requests);
        Assert.False(s.Status.IsVisible);
    }

    [Fact]
    public async Task The_setting_is_on_by_default_in_General_and_Reset_to_defaults_turns_it_back_on()
    {
        await using var s = Create();
        using var settings = new SettingsViewModel(s.H.Services, null);
        Assert.True(settings.ShowServiceStatus);
        settings.ShowServiceStatus = false;

        settings.ResetGeneralCommand.Execute(null);

        Assert.True(settings.ShowServiceStatus);
        Assert.True(s.H.Services.Settings.General.ShowServiceStatus);
        settings.SearchText = "service status";
        Assert.Equal([new SettingsSearchResult("General", "Show Claude's service status")], settings.SearchResults);
    }
}
