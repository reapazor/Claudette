using Claudette.Core.Perforce;

namespace Claudette.Core.Tests.Perforce;

/// <summary>Finding <c>p4</c> in Bash commands, and the changelist in the tab title (DESIGN.md §18).</summary>
public class ChangelistTrackerTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-28T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private readonly List<TrackedChangelist> _saved = [];
    private int _minute;

    private ChangelistTracker Tracker => new(_saved);

    private bool Run(string command, string output = "", bool isError = false) =>
        Tracker.Observe(command, output, isError, Start.AddMinutes(++_minute));

    [Theory]
    [InlineData("p4 info", "info")]
    [InlineData("cd src && p4 edit -c 12345 a.cpp", "edit")]
    [InlineData("P4CLIENT=other p4 -c other -u matt sync //depot/...", "sync")]
    [InlineData("/usr/local/bin/p4 opened", "opened")]
    [InlineData(@"""C:\Tools\p4.exe"" changes -m 5", "changes")]
    [InlineData("ls; p4 -ztag describe 12", "describe")]
    [InlineData("echo 'a;b' | p4 -x - add", "add")]
    public void P4_commands_are_found_in_a_command_line(string command, string subcommand)
    {
        Assert.Contains(PerforceCommands.Find(command), i => i.Subcommand == subcommand);
        Assert.True(PerforceCommands.RunsP4(command));
    }

    [Theory]
    [InlineData("ls -la")]
    [InlineData("echo p4 edit")]
    [InlineData("git p4 sync")]
    [InlineData("grep -r 'p4 edit' .")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_commands_are_not_p4(string? command) => Assert.False(PerforceCommands.RunsP4(command));

    [Fact]
    public void Quotes_and_escapes_are_undone()
    {
        var invocation = Assert.Single(PerforceCommands.Find("p4 change -o \"12345\" && echo \"p4 edit\\\" x\""));

        Assert.Equal("change", invocation.Subcommand);
        Assert.Equal(["-o", "12345"], invocation.Arguments);
    }

    [Theory]
    [InlineData("p4 edit -c 12345 a.cpp")]
    [InlineData("p4 add -c 12345 new.cpp")]
    [InlineData("p4 reopen -c 12345 //depot/a.cpp")]
    [InlineData("p4 shelve -c 12345")]
    [InlineData("p4 change -o 12345")]
    [InlineData("p4 submit -c 12345")]
    [InlineData("p4 edit -c12345 a.cpp")]
    [InlineData("p4 -c matt-ws -u matt delete -c 12345 old.cpp")]
    [InlineData("cd src && p4 -ztag edit -c 12345 a.cpp 2>&1")]
    public void Commands_that_name_a_changelist_set_it(string command)
    {
        Assert.True(Run(command, "//depot/a.cpp#3 - opened for edit"));

        Assert.Equal(12345, Tracker.Current?.Number);
        Assert.Equal(ChangelistState.Pending, Tracker.Current?.State);
    }

    [Theory]
    [InlineData("p4 edit a.cpp")]
    [InlineData("p4 edit -c default a.cpp")]
    [InlineData("p4 -c 12345 edit a.cpp")]
    [InlineData("p4 describe 12345")]
    [InlineData("p4 change -o")]
    [InlineData("echo p4 edit -c 12345")]
    public void The_default_changelist_and_other_commands_are_ignored(string command)
    {
        Assert.False(Run(command));

        Assert.Null(Tracker.Current);
    }

    [Fact]
    public void Output_that_creates_one_sets_it()
    {
        Assert.True(Run("p4 change -i < spec.txt", "Change 12346 created with 2 open file(s)."));

        Assert.Equal(12346, Tracker.Current?.Number);
    }

    [Fact]
    public void The_most_recently_used_one_wins_and_all_are_kept()
    {
        Run("p4 edit -c 100 a.cpp");
        Run("p4 edit -c 200 b.cpp");
        Assert.Equal(200, Tracker.Current?.Number);

        Run("p4 add -c 100 c.cpp");

        Assert.Equal(100, Tracker.Current?.Number);
        Assert.Equal([100, 200], Tracker.All.Select(c => c.Number));
    }

    [Fact]
    public void At_the_same_moment_the_later_one_wins()
    {
        var tracker = Tracker;
        tracker.Observe("p4 edit -c 100 a.cpp", "", false, Start);
        tracker.Observe("p4 edit -c 200 b.cpp", "", false, Start);
        Assert.Equal(200, tracker.Current?.Number);

        tracker.Observe("p4 edit -c 100 c.cpp", "", false, Start);
        Assert.Equal(100, tracker.Current?.Number);
        Assert.Equal([100, 200], tracker.All.Select(c => c.Number));
    }

    [Fact]
    public void Submitting_marks_it_submitted()
    {
        Run("p4 edit -c 12345 a.cpp");

        Assert.True(Run("p4 submit -c 12345", "Submitting change 12345.\nLocking 1 files ...\nedit //depot/a.cpp#4\nChange 12345 submitted."));

        Assert.Equal(12345, Tracker.Current?.Number);
        Assert.Equal(ChangelistState.Submitted, Tracker.Current?.State);
    }

    [Fact]
    public void A_renumbered_submit_keeps_the_new_number()
    {
        Run("p4 edit -c 100 a.cpp");

        Run("p4 submit -c 100", "Change 100 renamed change 105 and submitted.");

        Assert.Equal(105, Tracker.Current?.Number);
        Assert.Equal(ChangelistState.Submitted, Tracker.Current?.State);
        Assert.Single(Tracker.All);
    }

    [Fact]
    public void Deleting_it_takes_the_badge_away()
    {
        Run("p4 edit -c 100 a.cpp");
        Run("p4 edit -c 200 b.cpp");

        Assert.True(Run("p4 change -d 200", "Change 200 deleted."));

        Assert.Null(Tracker.Current);
        Assert.Equal([100], Tracker.All.Select(c => c.Number));
    }

    [Fact]
    public void A_failed_command_doesnt_set_the_changelist_it_named()
    {
        Assert.False(Run("p4 edit -c 999 a.cpp", "Change 999 unknown.", isError: true));

        Assert.Null(Tracker.Current);
    }

    [Fact]
    public void Output_counts_only_for_p4_commands()
    {
        Assert.False(Run("cat notes.txt", "Change 12345 created."));

        Assert.Null(Tracker.Current);
    }

    [Fact]
    public void The_saved_list_is_what_it_works_on()
    {
        _saved.Add(new TrackedChangelist { Number = 777, State = ChangelistState.Pending, LastUsed = Start });

        Assert.Equal(777, Tracker.Current?.Number);

        Run("p4 shelve -c 778");
        Assert.Equal(2, _saved.Count);
    }
}
