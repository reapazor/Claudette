using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Claudette.Core.Json;

namespace Claudette.Usage;

/// <summary>Plan usage one machine shared through the session library, as another machine reads it.</summary>
/// <param name="Version">The file's format: <see cref="UsageSharing.FileVersion"/> when this Claudette can read it.</param>
/// <param name="MachineName">The machine's name when it wrote the file, for the log.</param>
/// <param name="WrittenBy">The version of Claudette that wrote it, for the log. Null in a file written before it was kept.</param>
/// <param name="Account">Which account it was signed in to: an <see cref="UsageSharing.AccountKey"/>.</param>
/// <param name="Samples">Empty when the file's format is newer than this Claudette reads.</param>
public sealed record SharedUsage(int Version, string MachineName, string? WrittenBy, string Account, DateTimeOffset Published, IReadOnlyList<UsageSample> Samples)
{
    /// <summary>A later Claudette wrote the file in a format this one can't read, so its samples were left out.</summary>
    public bool IsNewerFormat => Version > UsageSharing.FileVersion;
}

/// <summary>
/// Plan usage shared between machines through the session library (DESIGN.md §6, "Sharing across machines"). Each
/// machine that shares writes its own samples of the last <see cref="SharedPeriod"/> to a file of its own, and imports
/// the others', from machines signed in to the same account. Only plan usage readings: never token records or tab names.
/// </summary>
public static class UsageSharing
{
    /// <summary>
    /// The file's format. A change that only adds fields keeps it, since readers ignore fields they don't know; one that
    /// removes a field or changes what one means takes the next number, and a Claudette that reads an older format keeps
    /// reading it.
    /// </summary>
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
    /// <param name="writtenBy">This Claudette's version.</param>
    public static string Write(string machineName, string writtenBy, string account, DateTimeOffset published, IEnumerable<UsageSample> samples)
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
            ["claudette"] = writtenBy,
            ["account"] = account,
            ["published"] = published.ToUnixTimeMilliseconds(),
            ["samples"] = array,
        }.ToJsonString();
    }

    /// <summary>
    /// Reads a file another machine shared. Null for one that isn't a shared usage file. One in a newer format than
    /// <see cref="FileVersion"/> reads without its samples (<see cref="SharedUsage.IsNewerFormat"/>), since what they mean
    /// may have changed. A sample without a time is skipped, and fields a later version adds are ignored.
    /// </summary>
    public static SharedUsage? Read(string json)
    {
        JsonObject? file;
        try
        {
            file = JsonTree.ParseObject(json);
        }
        catch (Exception)
        {
            return null;
        }
        if (file is null || file.GetDouble("version") is not { } number || file.GetString("account") is not { } account || file.GetArray("samples") is not { } array)
        {
            return null;
        }
        var version = (int)number;
        var machine = file.GetString("machine") ?? "";
        var writtenBy = file.GetString("claudette");
        var published = Time(file, "published") ?? DateTimeOffset.MinValue;
        if (version > FileVersion)
        {
            return new SharedUsage(version, machine, writtenBy, account, published, []);
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
        return new SharedUsage(version, machine, writtenBy, account, published, samples);
    }

    private static DateTimeOffset? Time(JsonObject obj, string name) =>
        obj.GetDouble(name) is { } milliseconds ? DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds) : null;
}
