using System.Net.Http.Headers;

namespace Claudette.Core.Status;

/// <summary>Why the status page couldn't be read, in words to show. The status is unknown then; it's never an error dialog.</summary>
public sealed class StatusFeedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Claude's public status page (DESIGN.md §18, "Service status"): an Atlassian Statuspage whose v2 summary lists every
/// component, the open incidents and the scheduled maintenances. Unauthenticated; the request carries nothing but
/// Claudette's User-Agent.
/// </summary>
public sealed class StatusFeed(HttpClient http, string userAgent, string summaryUrl = StatusFeed.SummaryUrl)
{
    /// <summary>The summary. status.claude.ai redirects to this host.</summary>
    public const string SummaryUrl = "https://status.claude.com/api/v2/summary.json";

    /// <summary>The page itself, for people.</summary>
    public const string StatusPage = "https://status.claude.com";

    /// <summary>Asks for the summary. Throws <see cref="StatusFeedException"/> when it can't be had or read.</summary>
    public async Task<StatusSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, summaryUrl);
        request.Headers.UserAgent.ParseAdd(userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new StatusFeedException($"Couldn't reach status.claude.com: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StatusFeedException("status.claude.com didn't answer in time.", ex);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new StatusFeedException($"status.claude.com answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
            string json;
            try
            {
                json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new StatusFeedException($"Couldn't read status.claude.com's answer: {ex.Message}", ex);
            }
            return StatusSummary.TryParse(json) ?? throw new StatusFeedException("status.claude.com's answer couldn't be read.");
        }
    }
}
