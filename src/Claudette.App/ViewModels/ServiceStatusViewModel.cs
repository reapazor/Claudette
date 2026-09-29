using System.Globalization;
using System.Text;
using Claudette.App.Services;
using Claudette.Core.Status;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// Claude's service status in the main window (DESIGN.md §18, "Service status"): the dot at the header's right, with
/// each watched service in its tooltip, and the banner across the top while Claude has an incident or maintenance.
/// </summary>
public sealed partial class ServiceStatusViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly ServiceStatusService _status;

    public ServiceStatusViewModel(AppServices services)
    {
        _services = services;
        _status = services.ServiceStatus;
        _status.Changed += Refresh;
    }

    /// <summary>The dot shows while Settings → General → Show Claude's service status is on.</summary>
    public bool IsVisible => _status.IsActive;

    private ServiceStatusReport? Report => _status.Report;

    public ServiceLevel Level => Report?.Level ?? ServiceLevel.Unknown;

    /// <summary>Green.</summary>
    public bool IsOperational => Level == ServiceLevel.Operational;

    /// <summary>Amber: degraded, or maintenance.</summary>
    public bool IsDegraded => Level is ServiceLevel.Degraded or ServiceLevel.Maintenance;

    /// <summary>Red. Grey when none of the three: unknown, or not checked yet.</summary>
    public bool IsOutage => Level == ServiceLevel.Outage;

    public string LevelText => Report is null
        ? "Checking Claude's status…"
        : Level switch
        {
            ServiceLevel.Operational => "Claude is operational",
            ServiceLevel.Maintenance => "Claude is under maintenance",
            ServiceLevel.Degraded => "Claude is having problems",
            ServiceLevel.Outage => "Claude has an outage",
            _ => "Status unknown",
        };

    public string AccessibleName => $"Claude's service status: {LevelText}";

    /// <summary>The level, each watched service, the latest incident, when it was read, and where a click goes.</summary>
    public string Tooltip
    {
        get
        {
            var text = new StringBuilder(LevelText);
            if (Report is { } report)
            {
                if (report.Level == ServiceLevel.Unknown && _status.Error is { } error)
                {
                    text.Append('\n').Append(error);
                }
                else
                {
                    foreach (var service in report.Services)
                    {
                        text.Append('\n').Append(service.Name).Append(": ").Append(service.Status.Describe());
                    }
                    if (report.LatestIncident is { } incident)
                    {
                        text.Append("\nLatest incident: ").Append(IncidentText(incident));
                    }
                    foreach (var maintenance in report.Maintenances)
                    {
                        text.Append("\nMaintenance: ").Append(maintenance.Name);
                    }
                }
                text.Append("\nas of ").Append(LocalTime(report.AsOf));
            }
            return text.Append("\nClick to open status.claude.com").ToString();
        }
    }

    // ---- The banner ----------------------------------------------------------------------------------------------

    /// <summary>A watched service isn't operational, or an open incident concerns one, and it isn't dismissed.</summary>
    public bool ShowBanner => _status.ShowsBanner;

    /// <summary>Only maintenance under way: the quieter banner.</summary>
    public bool IsInfoBanner => Report is { IsMaintenance: true };

    public string BannerTitle => IsInfoBanner ? "Claude maintenance in progress:" : "Claude is having problems:";

    /// <summary>The latest incident and where it's at, else the services that aren't operational.</summary>
    public string BannerText
    {
        get
        {
            if (Report is not { } report)
            {
                return "";
            }
            if (report.IsMaintenance)
            {
                return report.Maintenances.Count > 0
                    ? string.Join("; ", report.Maintenances.Select(m => m.Name))
                    : Troubled(report) ?? "see the status page.";
            }
            if (report.LatestIncident is { } incident)
            {
                var others = report.Incidents.Count - 1;
                return IncidentText(incident) + (others > 0 ? $", and {others} more incident{(others == 1 ? "" : "s")}" : "");
            }
            return Troubled(report) ?? "see the status page.";
        }
    }

    [RelayCommand]
    private Task OpenStatusPageAsync() => _services.Platform.OpenUrlAsync(StatusFeed.StatusPage);

    /// <summary><b>Status page</b> on the banner: the incident's own page, else the status page.</summary>
    [RelayCommand]
    private Task OpenBannerLinkAsync()
    {
        var link = Report is { IsMaintenance: true } maintenance
            ? maintenance.Maintenances.FirstOrDefault()?.Shortlink
            : Report?.LatestIncident?.Shortlink;
        return _services.Platform.OpenUrlAsync(link is { Length: > 0 } ? link : StatusFeed.StatusPage);
    }

    [RelayCommand]
    private void DismissBanner() => _status.Dismiss();

    private static string IncidentText(StatusIncident incident) =>
        incident.Status.Describe() is { } status ? $"{incident.Name} ({status})" : incident.Name;

    /// <summary>"Claude Code (partial outage), Claude API (degraded performance)", or null when all are fine.</summary>
    private static string? Troubled(ServiceStatusReport report)
    {
        var troubled = report.Services
            .Where(s => s.Status is not (ComponentStatus.Operational or ComponentStatus.Unknown))
            .Select(s => $"{s.Name} ({s.Status.Describe().ToLowerInvariant()})")
            .ToList();
        return troubled.Count > 0 ? string.Join(", ", troubled) : null;
    }

    private string LocalTime(DateTimeOffset time) =>
        TimeZoneInfo.ConvertTime(time, _services.Time.LocalTimeZone).ToString("HH:mm", CultureInfo.InvariantCulture);

    private void Refresh() => OnPropertyChanged(string.Empty);

    public void Dispose() => _status.Changed -= Refresh;
}
