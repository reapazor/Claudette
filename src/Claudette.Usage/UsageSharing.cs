using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Claudette.Usage;

/// <summary>Plan usage one machine shared through the session library, as another machine reads it.</summary>
/// <param name="MachineName">The machine's name when it wrote the file, for the log.</param>
/// <param name="Account">Which account it was signed in to: an <see cref="UsageSharing.AccountKey"/>.</param>
public sealed record SharedUsage(string MachineName, string Account, DateTimeOffset Published, IReadOnlyList<UsageSample> Samples);

/// <summary>
/// Plan usage shared between machines through the session library (DESIGN.md §6, "Sharing across machines"). Each
/// machine that shares writes its own samples of the last <see cref="SharedPeriod"/> to a file of its own, and imports
/// the others', from machines signed in to the same account. Only plan usage readings: never token records or tab names.
/// </summary>
public static class UsageSharing
{
    public const int FileVersion = 1;

    /// <summary>How far back a machine's file goes: a week's window, and a day to spare.</summary>
    public static readonly TimeSpan SharedPeriod = TimeSpan.FromDays(8);

    /// <summary>
    /// Which account a file belongs to, without naming it in the library: a hash of the email and organization Claude
    /// Code reports. Null without an email (an API key has no plan limits to share).
    /// </summary>
    public static string? AccountKey(string? email, string? organization)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }
        var identity = $"{email.Trim().ToLowerInvariant()}\n{organization?.Trim() ?? ""}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
    }

    /// <summary>The file this machine shares.</summary>
    public static string Write(string machineName, string account, DateTimeOffset published, IEnumerable<UsageSample> samples)
    {
        var array = new JsonArray();
        foreach (var sample in samples)
        {
            var entry = new JsonObject { ["at"] = sample.Timestamp.ToUnixTimeMilliseconds() };
            if (sample.SessionPercent is { } session)
            {
                entry["session"] = session;
                entry["sessionResetsAt"] = sample.SessionResetsAt?.ToUnixTimeMilliseconds();
            }
            if (sample.WeeklyPercent is { } weekly)
            {
                entry["weekly"] = weekly;
                entry["weeklyResetsAt"] = sample.WeeklyResetsAt?.ToUnixTimeMilliseconds();
            }
            if (sample.Models.Count > 0)
            {
                entry["models"] = new JsonArray([.. sample.Models.Select(m => (JsonNode)new JsonObject
                {
                    ["label"] = m.Label,
                    ["percent"] = m.Percent,
                    ["resetsAt"] = m.ResetsAt?.ToUnixTimeMilliseconds(),
                })]);
            }
            array.Add(entry);
        }
        return new JsonObject
        {
            ["version"] = FileVersion,
            ["machine"] = machineName,
            ["account"] = account,
            ["published"] = published.ToUnixTimeMilliseconds(),
            ["samples"] = array,
        }.ToJsonString();
    }

    /// <summary>
    /// Reads a file another machine shared. Null for one that isn't a shared usage file; a sample without a time is
    /// skipped, and fields a later version adds are ignored.
    /// </summary>
    public static SharedUsage? Read(string json)
    {
        JsonObject? file;
        try
        {
            file = JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception)
        {
            return null;
        }
        if (file is null || file.GetDouble("version") is null || file.GetString("account") is not { } account || file.GetArray("samples") is not { } array)
        {
            return null;
        }
        var samples = new List<UsageSample>();
        foreach (var entry in array.OfType<JsonObject>())
        {
            if (Time(entry, "at") is not { } at)
            {
                continue;
            }
            var models = (entry.GetArray("models") ?? []).OfType<JsonObject>()
                .Where(m => m.GetString("label") is not null && m.GetDouble("percent") is not null)
                .Select(m => new ModelSample(m.GetString("label")!, m.GetDouble("percent")!.Value, Time(m, "resetsAt")))
                .ToList();
            samples.Add(new UsageSample(at, entry.GetDouble("session"), Time(entry, "sessionResetsAt"), entry.GetDouble("weekly"),
                Time(entry, "weeklyResetsAt"), models));
        }
        return new SharedUsage(file.GetString("machine") ?? "", account, Time(file, "published") ?? DateTimeOffset.MinValue, samples);
    }

    private static DateTimeOffset? Time(JsonObject obj, string name) =>
        obj.GetDouble(name) is { } milliseconds ? DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds) : null;
}
