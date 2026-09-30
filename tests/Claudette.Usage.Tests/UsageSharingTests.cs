using System.Text.Json.Nodes;

namespace Claudette.Usage.Tests;

/// <summary>The file a machine shares its plan usage in (DESIGN.md §6, "Sharing across machines").</summary>
public class UsageSharingTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 28, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_shared_file_reads_back_as_it_was_written()
    {
        UsageSample[] samples =
        [
            new(At, 62, At.AddHours(2), 38, At.AddDays(3), [new ModelSample("Fable", 20, At.AddDays(3))]),
            new(At.AddMinutes(5), 64, At.AddHours(2), null, null, []),
        ];

        var shared = UsageSharing.Read(UsageSharing.Write("DESKTOP-01", "a1b2c3", At.AddMinutes(6), samples));

        Assert.NotNull(shared);
        Assert.Equal(("DESKTOP-01", "a1b2c3", At.AddMinutes(6)), (shared.MachineName, shared.Account, shared.Published));
        Assert.Equal(samples.Length, shared.Samples.Count);
        Assert.Equal(samples[0] with { Models = [] }, shared.Samples[0] with { Models = [] });
        Assert.Equal(samples[0].Models, shared.Samples[0].Models);
        Assert.Equal(samples[1] with { Models = [] }, shared.Samples[1] with { Models = [] });
    }

    [Fact]
    public void The_account_is_named_by_a_hash_that_ignores_the_emails_case()
    {
        var key = UsageSharing.AccountKey("Me@Example.com", "Acme");

        Assert.NotNull(key);
        Assert.DoesNotContain("example", key, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(key, UsageSharing.AccountKey("me@example.com ", "Acme"));
        Assert.NotEqual(key, UsageSharing.AccountKey("me@example.com", "Other org"));
        Assert.NotEqual(key, UsageSharing.AccountKey("you@example.com", "Acme"));
        Assert.Null(UsageSharing.AccountKey(null, "Acme"));
    }

    [Fact]
    public void A_file_that_isnt_one_is_ignored_and_a_sample_without_a_time_is_skipped()
    {
        Assert.Null(UsageSharing.Read("not json"));
        Assert.Null(UsageSharing.Read("""{"version":1,"samples":[]}"""));
        Assert.Null(UsageSharing.Read("""["a list"]"""));

        var shared = UsageSharing.Read(new JsonObject
        {
            ["version"] = 2,
            ["account"] = "a1b2c3",
            ["somethingNew"] = true,
            ["samples"] = new JsonArray(
                new JsonObject { ["session"] = 10 },
                new JsonObject { ["at"] = At.ToUnixTimeMilliseconds(), ["session"] = 12, ["alsoNew"] = "x" }),
        }.ToJsonString());

        Assert.Equal([12.0], shared?.Samples.Select(s => s.SessionPercent));
    }
}
