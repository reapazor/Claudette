using System.Text.Json.Nodes;
using Claudette.Core.Library;
using Claudette.Core.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Core.Tests.Library;

public sealed class LeaseManagerTests : IDisposable
{
    private const string Session = "s1";

    private readonly TempFolder _root = new("claudette-lease");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly LeaseManager _desktop;
    private readonly LeaseManager _laptop;
    private readonly string _folder;

    public LeaseManagerTests()
    {
        _desktop = new LeaseManager("DESKTOP-01", _time);
        _laptop = new LeaseManager("LAPTOP", _time);
        _folder = _root.CreateFolder($"sessions/{Session}");
    }

    public void Dispose()
    {
        _desktop.Dispose();
        _laptop.Dispose();
        _root.Dispose();
    }

    private string LeasePath => Path.Combine(_folder, LeaseManager.FileName);

    private JsonObject LeaseFile() => JsonNode.Parse(File.ReadAllText(LeasePath))!.AsObject();

    [Fact]
    public void Free_when_there_is_no_lease_file()
    {
        Assert.Equal(new LeaseStatus.Free(), _desktop.Check(_folder));
    }

    [Fact]
    public void Acquired_is_mine_here_and_held_by_other_elsewhere()
    {
        _desktop.TakeOver(Session, _folder);

        Assert.Equal(new LeaseStatus.Mine(), _desktop.Check(_folder));
        Assert.Equal(new LeaseStatus.HeldByOther("DESKTOP-01", _time.GetUtcNow()), _laptop.Check(_folder));
        Assert.Equal([Session], _desktop.HeldSessions);

        var lease = LeaseFile();
        Assert.Equal("DESKTOP-01", lease["machine"]!.GetValue<string>());
        Assert.Equal(_desktop.OwnerId, lease["owner"]!.GetValue<string>());
        Assert.NotNull(lease["updatedAt"]);
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public void Two_runs_on_the_same_machine_are_told_apart()
    {
        using var secondRun = new LeaseManager("DESKTOP-01", _time);
        _desktop.TakeOver(Session, _folder);

        Assert.IsType<LeaseStatus.HeldByOther>(secondRun.Check(_folder));
    }

    [Fact]
    public void A_lease_that_is_not_refreshed_goes_stale_after_10_minutes()
    {
        var updatedAt = _time.GetUtcNow();
        File.WriteAllText(LeasePath, $"{{\"machine\":\"CRASHED\",\"updatedAt\":\"{updatedAt:O}\",\"owner\":\"gone\"}}");

        _time.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        Assert.Equal(new LeaseStatus.HeldByOther("CRASHED", updatedAt), _desktop.Check(_folder));

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new LeaseStatus.Stale("CRASHED", updatedAt), _desktop.Check(_folder));
    }

    [Fact]
    public void Held_leases_are_refreshed_every_minute()
    {
        var start = _time.GetUtcNow();
        _desktop.TakeOver(Session, _folder);

        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(start, _laptop.Check(_folder) is LeaseStatus.HeldByOther h ? h.UpdatedAt : default);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(start.AddMinutes(1), _laptop.Check(_folder) is LeaseStatus.HeldByOther h2 ? h2.UpdatedAt : default);

        // Kept fresh, so it never goes stale while held.
        _time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(new LeaseStatus.HeldByOther("DESKTOP-01", start.AddMinutes(31)), _laptop.Check(_folder));
    }

    [Fact]
    public void A_refresh_recreates_a_missing_lease_file()
    {
        _desktop.TakeOver(Session, _folder);
        File.Delete(LeasePath);

        _time.Advance(LeaseManager.RefreshInterval);

        Assert.Equal(new LeaseStatus.Mine(), _desktop.Check(_folder));
    }

    [Fact]
    public void Taking_over_raises_lease_lost_on_the_other_machine_and_it_stops_refreshing()
    {
        var lost = new List<(string Session, string Machine)>();
        _desktop.LeaseLost += (session, machine) => lost.Add((session, machine));
        _desktop.TakeOver(Session, _folder);

        _laptop.TakeOver(Session, _folder);
        _time.Advance(LeaseManager.RefreshInterval);

        Assert.Equal([(Session, "LAPTOP")], lost);
        Assert.Empty(_desktop.HeldSessions);
        Assert.Equal(new LeaseStatus.Mine(), _laptop.Check(_folder));
        Assert.Equal(_laptop.OwnerId, LeaseFile()["owner"]!.GetValue<string>());

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Single(lost);
        Assert.Equal(_laptop.OwnerId, LeaseFile()["owner"]!.GetValue<string>());
    }

    [Fact]
    public void Release_deletes_the_file_only_while_it_is_ours()
    {
        _desktop.TakeOver(Session, _folder);
        _laptop.TakeOver(Session, _folder);

        _desktop.Release(Session);
        Assert.True(File.Exists(LeasePath));

        _laptop.Release(Session);
        Assert.False(File.Exists(LeasePath));
        Assert.Equal(new LeaseStatus.Free(), _desktop.Check(_folder));
    }

    [Fact]
    public void Dispose_releases_held_leases()
    {
        var run = new LeaseManager("DESKTOP-01", _time);
        run.TakeOver(Session, _folder);

        run.Dispose();

        Assert.False(File.Exists(LeasePath));
        Assert.Throws<ObjectDisposedException>(() => run.TakeOver(Session, _folder));
    }

    [Fact]
    public void An_unreadable_lease_file_isnt_free()
    {
        File.WriteAllText(LeasePath, "{ half");

        Assert.Equal(new LeaseStatus.Unreadable(), _desktop.Check(_folder));
    }

    [Fact]
    public void A_refresh_doesnt_write_over_a_lease_it_cant_read()
    {
        // Perhaps another machine's take-over, half synced.
        _desktop.TakeOver(Session, _folder);
        File.WriteAllText(LeasePath, "{ half");

        _time.Advance(LeaseManager.RefreshInterval);

        Assert.Equal("{ half", File.ReadAllText(LeasePath));
        Assert.Equal([Session], _desktop.HeldSessions);
    }

    [Fact]
    public void Renew_takes_a_free_lease_and_refreshes_its_own()
    {
        Assert.True(_desktop.Renew(Session, _folder));
        Assert.Equal(new LeaseStatus.Mine(), _desktop.Check(_folder));

        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(_desktop.Renew(Session, _folder));
        Assert.Equal(new LeaseStatus.HeldByOther("DESKTOP-01", _time.GetUtcNow()), _laptop.Check(_folder));
    }

    [Fact]
    public void Renew_after_a_take_over_refuses_and_reports_the_session_lost()
    {
        // The copy after a turn on the desktop, between the laptop's take-over and the desktop's next refresh.
        var lost = new List<(string Session, string Machine)>();
        _desktop.LeaseLost += (session, machine) => lost.Add((session, machine));
        _desktop.TakeOver(Session, _folder);
        _laptop.TakeOver(Session, _folder);

        Assert.False(_desktop.Renew(Session, _folder));

        Assert.Equal([(Session, "LAPTOP")], lost);
        Assert.Empty(_desktop.HeldSessions);
        Assert.Equal(_laptop.OwnerId, LeaseFile()["owner"]!.GetValue<string>());
        Assert.Equal(new LeaseStatus.Mine(), _laptop.Check(_folder));
    }

    [Fact]
    public void Renew_never_takes_a_live_lease_it_didnt_hold()
    {
        var lost = new List<string>();
        _desktop.LeaseLost += (session, _) => lost.Add(session);
        _laptop.TakeOver(Session, _folder);

        Assert.False(_desktop.Renew(Session, _folder));

        Assert.Empty(lost);
        Assert.Equal(_laptop.OwnerId, LeaseFile()["owner"]!.GetValue<string>());
    }

    [Fact]
    public void Renew_takes_a_stale_lease_it_didnt_hold()
    {
        File.WriteAllText(LeasePath, $"{{\"machine\":\"CRASHED\",\"updatedAt\":\"{_time.GetUtcNow():O}\",\"owner\":\"gone\"}}");
        _time.Advance(LeaseManager.StaleAfter);

        Assert.True(_desktop.Renew(Session, _folder));

        Assert.Equal(new LeaseStatus.Mine(), _desktop.Check(_folder));
    }

    [Fact]
    public void Renew_doesnt_write_over_a_lease_it_cant_read()
    {
        File.WriteAllText(LeasePath, "{ half");

        Assert.False(_desktop.MayWrite(Session, _folder));
        Assert.False(_desktop.Renew(Session, _folder));

        Assert.Equal("{ half", File.ReadAllText(LeasePath));
    }

    [Fact]
    public void May_write_asks_the_same_question_without_writing()
    {
        var lost = new List<string>();
        _desktop.LeaseLost += (session, _) => lost.Add(session);

        Assert.True(_desktop.MayWrite(Session, _folder));
        Assert.False(File.Exists(LeasePath));
        Assert.Empty(_desktop.HeldSessions);

        _desktop.TakeOver(Session, _folder);
        _laptop.TakeOver(Session, _folder);
        Assert.False(_desktop.MayWrite(Session, _folder));
        Assert.Equal([Session], lost);
        Assert.Equal(_laptop.OwnerId, LeaseFile()["owner"]!.GetValue<string>());
    }
}
