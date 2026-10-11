using Claudette.App.Mascot;
using Claudette.App.Tests.Support;

namespace Claudette.App.Tests;

/// <summary>
/// Claudette on the composer (DESIGN.md §5): what she does as the tab works, plans, compacts and fails, when other tabs
/// wait, when the user sends, attaches, pastes, types her name, pokes her, drags something over the composer or picks
/// her up, and what she wears.
/// </summary>
public class MascotReactionsTests
{
    private static readonly MascotRoom Room = MascotStage.DefaultRoom;

    private static IEnumerable<string> PropNames(IEnumerable<MascotFrame> frames) => frames.SelectMany(f => f.Props).Select(p => p.Name);

    [Theory]
    [InlineData(MascotTool.Reading, 0, false, "magnifier")]
    [InlineData(MascotTool.Editing, 0, false, "hammer-up")]
    [InlineData(MascotTool.Running, 0, false, "terminal-0")]
    [InlineData(MascotTool.Web, 0, false, "globe-1")]
    [InlineData(MascotTool.None, 0, false, "laptop")]
    [InlineData(MascotTool.None, 0, true, "clipboard")]
    [InlineData(MascotTool.Agents, 2, false, "ball")]
    public void While_Claude_works_she_works_with_what_suits_its_tool(MascotTool tool, int agents, bool planning, string prop)
    {
        using var stage = new MascotStage();
        stage.ShowStanding();
        var from = stage.Frames.Count;

        stage.Director.SetSituation(new MascotSituation { Mood = MascotMood.Working, Tool = tool, Agents = agents, Planning = planning });
        stage.Run(TimeSpan.FromSeconds(4));

        Assert.Contains(prop, PropNames(stage.Since(from)));
        if (agents > 0)
        {
            // A ball for each subagent running.
            Assert.All(stage.Since(from), f => Assert.Equal(agents, f.Props.Count(p => p.Name == "ball")));
        }
    }

    [Fact]
    public void A_new_tool_shows_from_her_next_burst_of_work_rather_than_mid_way()
    {
        using var stage = new MascotStage();
        stage.ShowStanding();
        var working = new MascotSituation { Mood = MascotMood.Working, Tool = MascotTool.Running };
        stage.Director.SetSituation(working);
        stage.Run(TimeSpan.FromSeconds(1));

        var from = stage.Frames.Count;
        stage.Director.SetSituation(working with { Tool = MascotTool.Reading });
        stage.Run(TimeSpan.FromMilliseconds(500));
        Assert.DoesNotContain("magnifier", PropNames(stage.Since(from)));
        stage.Run(TimeSpan.FromSeconds(10));
        Assert.Contains("magnifier", PropNames(stage.Since(from)));
    }

    [Fact]
    public void Through_a_long_turn_she_fetches_a_coffee_which_stays_beside_her_till_the_turn_ends()
    {
        using var stage = new MascotStage();
        stage.ShowStanding();
        stage.Director.SetMood(MascotMood.Working);
        stage.Run(MascotDirector.CoffeeAfter - TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(50));
        Assert.DoesNotContain(PropNames(stage.Since(0)), name => name.StartsWith("mug", StringComparison.Ordinal));

        var from = stage.Frames.Count;
        stage.Run(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50));
        var fetching = stage.Since(from);
        // Down behind the box for it, and back up with it.
        var behind = fetching.ToList().FindIndex(f => f.IsBehind);
        Assert.True(behind >= 0);
        Assert.Contains(fetching.Skip(behind), f => f.Props.Any(p => p.Name.StartsWith("mug", StringComparison.Ordinal)));
        from = stage.Frames.Count;
        stage.Run(TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(50));
        Assert.All(stage.Since(from).Where(f => f.Props.Any(p => p.Name == "laptop")), f => Assert.Contains(f.Props, p => p.Name.StartsWith("mug", StringComparison.Ordinal)));

        from = stage.Frames.Count;
        stage.Director.SetMood(MascotMood.Idle);
        stage.Run(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(PropNames(stage.Since(from)), name => name.StartsWith("mug", StringComparison.Ordinal));
    }

    [Fact]
    public void With_the_context_nearly_full_she_wipes_her_brow_as_she_works()
    {
        using var stage = new MascotStage();
        stage.ShowStanding();
        var from = stage.Frames.Count;

        stage.Director.SetSituation(new MascotSituation { Mood = MascotMood.Working, ContextFull = true });
        stage.Run(MascotDirector.SweatEvery * 2);

        Assert.True(PropNames(stage.Since(from)).Count(name => name == "sweat") >= 2 * 4, "at least two wipes of the brow");
    }

    [Fact]
    public void While_Claude_Code_compacts_the_conversation_she_sweeps_up()
    {
        using var stage = new MascotStage();
        stage.ShowStanding();
        var from = stage.Frames.Count;

        stage.Director.SetSituation(new MascotSituation { Mood = MascotMood.Working, Compacting = true });
        stage.Run(TimeSpan.FromSeconds(3));
        Assert.All(stage.Since(from).Skip(1), f => Assert.Contains(f.Props, p => p.Name == "broom"));

        from = stage.Frames.Count;
        stage.Director.SetSituation(new MascotSituation { Mood = MascotMood.Working });
        stage.Run(TimeSpan.FromSeconds(3));
        Assert.DoesNotContain("broom", PropNames(stage.Since(from)));
    }

    [Fact]
    public void In_plan_mode_she_holds_a_clipboard_while_she_stands_about()
    {
        using var stage = new MascotStage();
        stage.ShowStanding();
        var from = stage.Frames.Count;

        stage.Director.SetSituation(new MascotSituation { Planning = true });
        stage.Run(TimeSpan.FromSeconds(4));

        Assert.All(stage.Since(from).Where(f => f.Pose is "stand" or "blink"), f => Assert.Contains(f.Props, p => p.Name == "clipboard"));
    }

    [Fact]
    public void She_points_out_other_tabs_waiting_toward_the_sidebar_and_says_so()
    {
        var lines = new FakeMascotLines();
        using var stage = new MascotStage(lines: lines);
        stage.ShowStanding();
        var from = stage.Frames.Count;

        stage.Director.SetSituation(new MascotSituation { OthersWaiting = 2 });

        Assert.Equal(("waveLookLeft", "2 waiting"), (stage.Frame.Pose, stage.Frame.Say));
        Assert.Contains(stage.Frame.Props, p => p.Name == "arrow");
        stage.Run(MascotDirector.PointEvery + TimeSpan.FromSeconds(4));
        // Again a little later, while the tab still waits.
        Assert.True(stage.Since(from).Count(f => f.Pose == "waveLookLeft") >= 6);
        Assert.Contains(stage.Since(from), f => f.Say is null);

        // Not while this tab is waiting itself: she waves at the user instead.
        from = stage.Frames.Count;
        stage.Director.SetSituation(new MascotSituation { Mood = MascotMood.Waiting, OthersWaiting = 2 });
        stage.Run(MascotDirector.PointEvery * 2);
        Assert.DoesNotContain(stage.Since(from), f => f.Pose == "waveLookLeft");
    }

    [Fact]
    public void With_motion_reduced_she_holds_still_whatever_suits_the_tab()
    {
        using var stage = new MascotStage(lines: new FakeMascotLines());
        stage.Director.SetStill(true);
        stage.Show();

        stage.Director.SetSituation(new MascotSituation { Mood = MascotMood.Working, Tool = MascotTool.Editing });
        Assert.Equal("hammer-up", Assert.Single(stage.Frame.Props).Name);
        stage.Director.SetSituation(new MascotSituation { Compacting = true });
        Assert.Equal("broom", Assert.Single(stage.Frame.Props).Name);
        stage.Director.SetSituation(new MascotSituation { OthersWaiting = 1 });
        Assert.Equal(("waveLookLeft", "1 waiting", Room.HomeX), (stage.Frame.Pose, stage.Frame.Say, stage.Frame.X));

        var count = stage.Frames.Count;
        stage.Director.TurnFailed();
        stage.Director.MessageSent();
        stage.Run(TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(100));
        Assert.Equal(count, stage.Frames.Count);
    }

    [Fact]
    public void A_failed_turn_leaves_her_dizzy_with_stars_going_round_her_head()
    {
        using var stage = new MascotStage();
        stage.ShowStanding();
        var from = stage.Frames.Count;

        stage.Director.TurnFailed();
        stage.Run(TimeSpan.FromSeconds(3));

        var dizzy = stage.Since(from);
        Assert.Contains(dizzy, f => f.Props.Count(p => p.Name == "star") == 3);
        // They go round: no two frames in a row have them in the same places.
        var starry = dizzy.Where(f => f.Props.Any(p => p.Name == "star")).ToList();
        Assert.All(starry.Zip(starry.Skip(1)), pair => Assert.NotEqual(pair.First.Props, pair.Second.Props));
        Assert.Equal(("stand", 0.0), (stage.Frame.Pose, stage.Frame.Drop));
    }

    [Fact]
    public void Every_task_done_brings_confetti_and_every_file_reviewed_a_stamp_and_its_check_mark()
    {
        using var stage = new MascotStage();
        stage.ShowStanding();
        var from = stage.Frames.Count;

        stage.Director.AllTasksDone();
        stage.Run(TimeSpan.FromSeconds(2));
        var confetti = PropNames(stage.Since(from)).Where(name => name.StartsWith("confetti", StringComparison.Ordinal)).Distinct();
        Assert.True(confetti.Count() >= 4, "confetti of several colours");

        from = stage.Frames.Count;
        stage.Director.AllReviewed();
        stage.Run(TimeSpan.FromSeconds(3));
        var stamping = PropNames(stage.Since(from)).ToList();
        Assert.True(stamping.IndexOf("stamp") < stamping.IndexOf("check"));
    }

    [Fact]
    public void A_message_sent_flies_off_as_a_paper_plane_even_as_Claude_starts_working()
    {
        using var stage = new MascotStage();
        stage.ShowStanding();
        var from = stage.Frames.Count;

        stage.Director.MessageSent();
        stage.Director.SetMood(MascotMood.Working);
        stage.Run(TimeSpan.FromSeconds(2));

        var flight = stage.Since(from).SelectMany(f => f.Props).Where(p => p.Name == "plane").ToList();
        Assert.True(flight.Count >= 7);
        // Up and away to her left, over the conversation.
        Assert.All(flight.Zip(flight.Skip(1)), pair => Assert.True(pair.Second.X < pair.First.X && pair.Second.Bottom > pair.First.Bottom));
        Assert.Contains("laptop", PropNames(stage.Since(from)));
    }

    [Fact]
    public void A_file_attached_drops_into_her_hands_and_she_puts_it_in_the_box()
    {
        using var stage = new MascotStage();
        stage.ShowStanding();
        var from = stage.Frames.Count;

        stage.Director.Caught();
        stage.Run(TimeSpan.FromSeconds(2));

        var file = stage.Since(from).SelectMany(f => f.Props).Where(p => p.Name == "file").Select(p => p.Bottom).ToList();
        Assert.True(file[0] > MascotArt.Height, "it comes from above her");
        Assert.True(file[^1] < 0, "and goes down into the box");
    }

    [Fact]
    public void A_huge_paste_has_her_stagger_under_a_crate_and_wipe_her_brow()
    {
        using var stage = new MascotStage();
        stage.ShowStanding();
        var x = stage.Frame.X;
        var from = stage.Frames.Count;

        stage.Director.HeavyPaste();
        stage.Run(TimeSpan.FromSeconds(4));

        var carrying = stage.Since(from).Where(f => f.Props.Any(p => p.Name == "crate" && p.Bottom > 10)).ToList();
        Assert.Contains(carrying, f => f.X != x);
        Assert.Contains("sweat", PropNames(stage.Since(from)));
    }

    [Fact]
    public void Her_name_typed_she_waves_back_and_blows_a_heart()
    {
        using var stage = new MascotStage();
        stage.ShowStanding();
        var from = stage.Frames.Count;

        stage.Director.Greeted();
        stage.Run(TimeSpan.FromSeconds(3));

        Assert.Equal("wave", stage.Since(from)[0].Pose);
        Assert.Contains("heart", PropNames(stage.Since(from)));
    }

    [Fact]
    public void Poked_ten_times_in_a_minute_she_turns_her_back_and_pokes_do_nothing_till_she_comes_round()
    {
        using var stage = new MascotStage();
        stage.ShowStanding();
        for (var i = 0; i < MascotDirector.GrumpyPokes - 1; i++)
        {
            stage.Director.Poke();
            stage.Run(MascotDirector.DoublePoke + TimeSpan.FromSeconds(0.5));
        }
        Assert.NotEqual("back", stage.Frame.Pose);

        stage.Director.Poke();
        Assert.Equal("back", stage.Frame.Pose);
        Assert.Contains(stage.Frame.Props, p => p.Name == "cloud");
        stage.Run(TimeSpan.FromSeconds(1));
        stage.Director.Poke();
        Assert.Equal("back", stage.Frame.Pose);

        stage.Run(TimeSpan.FromSeconds(8));
        Assert.NotEqual("back", stage.Frame.Pose);
        stage.Director.Poke();
        Assert.Equal("armsUp", stage.Frame.Pose);
    }

    [Fact]
    public void A_file_dragged_over_the_composer_has_her_ready_to_catch_it()
    {
        using var stage = new MascotStage();
        stage.ShowStanding();

        stage.Director.SetDragHover(true);
        stage.Run(TimeSpan.FromSeconds(1));
        Assert.Equal("armsUp", stage.Frame.Pose);

        stage.Director.SetDragHover(false);
        stage.Run(TimeSpan.FromSeconds(1));
        Assert.NotEqual("armsUp", stage.Frame.Pose);
    }

    [Fact]
    public void Standing_about_she_looks_toward_the_pointer_but_not_while_she_walks()
    {
        using var stage = new MascotStage();
        stage.ShowStanding();
        Assert.Equal("stand", stage.Frame.Pose);

        stage.Director.SetGaze(-1);
        Assert.Equal("lookLeft", stage.Frame.Pose);
        stage.Director.SetGaze(1);
        Assert.Equal("lookRight", stage.Frame.Pose);
        stage.Director.SetGaze(0);
        Assert.Equal("stand", stage.Frame.Pose);

        stage.Director.SetGaze(1);
        stage.Director.Perform(MascotAntics.Walk(-5));
        Assert.Equal("walkLeft-0", stage.Frame.Pose);
    }

    [Fact]
    public void Picked_up_and_set_down_on_the_edge_she_falls_to_it_by_gravity_and_walks_home()
    {
        using var stage = new MascotStage();
        stage.ShowStanding();

        stage.Director.BeginCarry();
        Assert.True(stage.Director.IsCarried);
        Assert.Equal("carried", stage.Frame.Pose);
        stage.Director.Carry(Room.EdgeLeft + 1, -6);
        Assert.Equal((Room.EdgeLeft + 1, -6.0), (stage.Frame.X, stage.Frame.Drop));
        stage.Run(TimeSpan.FromSeconds(10));
        Assert.Equal((Room.EdgeLeft + 1, -6.0), (stage.Frame.X, stage.Frame.Drop));

        var from = stage.Frames.Count;
        stage.Director.Release();
        stage.Run(TimeSpan.FromSeconds(1));
        var falling = stage.Since(from).Where(f => f.Pose == "carried").Select(f => f.Drop).ToList();
        Assert.True(falling.Count > 3);
        // Faster and faster.
        var steps = falling.Zip(falling.Skip(1), (a, b) => b - a).ToList();
        Assert.All(steps.Zip(steps.Skip(1)).SkipLast(1), pair => Assert.True(pair.Second > pair.First));
        Assert.Equal(Room.EdgeLeft + 1, stage.Since(from).First(f => f.Drop == 0).X);

        stage.Run(TimeSpan.FromSeconds(15));
        Assert.Equal(Room.HomeX, stage.Frame.X);
    }

    [Fact]
    public void Set_down_below_the_edge_she_falls_behind_the_box_and_climbs_back_up()
    {
        using var stage = new MascotStage();
        stage.ShowStanding();

        stage.Director.BeginCarry();
        stage.Director.Carry(Room.HomeX - 3, 6);
        stage.Director.Release();
        stage.Run(TimeSpan.FromSeconds(2));
        Assert.True(stage.Frame.IsBehind);

        stage.Run(TimeSpan.FromSeconds(6));
        Assert.Equal(("stand", 0.0), (stage.Frame.Pose, stage.Frame.Drop));
    }

    [Fact]
    public void Reactions_wait_while_she_is_behind_the_box()
    {
        using var stage = new MascotStage();
        stage.ShowStanding();
        stage.Director.SetRoom(null);
        stage.Run(TimeSpan.FromSeconds(1));

        stage.Director.Caught();
        stage.Run(TimeSpan.FromSeconds(1));
        Assert.True(stage.Frame.IsBehind);

        var from = stage.Frames.Count;
        stage.Director.SetRoom(Room);
        stage.Run(TimeSpan.FromSeconds(7));
        var after = stage.Since(from);
        var climbed = after.ToList().FindIndex(f => f is { Pose: "stand", Drop: 0 });
        Assert.Contains(after.Skip(climbed), f => f.Props.Any(p => p.Name == "file"));
    }

    [Fact]
    public void Falling_off_goes_by_gravity()
    {
        var drops = MascotAntics.Fall("armsUp", 0, MascotDirector.Behind).Select(s => s.Drop).ToList();

        Assert.Equal(MascotDirector.Behind, drops[^1]);
        var steps = drops.Zip(drops.Skip(1), (a, b) => b - a).ToList();
        Assert.All(steps.Zip(steps.Skip(1)).SkipLast(1), pair => Assert.True(pair.Second > pair.First));
        // A jump comes back down where it started.
        var jump = MascotAntics.Jump("armsUp", 2).Select(s => s.Drop).ToList();
        Assert.InRange(jump.Min(), -2.1, -1.5);
        Assert.InRange(jump[^1], -0.6, 0);
    }

    [Theory]
    [InlineData("2026-10-28T15:00:00Z", "witch")]
    [InlineData("2026-12-10T09:00:00Z", "santa")]
    [InlineData("2026-12-27T02:00:00Z", "nightcap")]
    [InlineData("2026-06-14T23:30:00Z", "nightcap")]
    [InlineData("2026-06-14T12:00:00Z", null)]
    public void She_wears_a_hat_by_the_date_and_the_hour(string at, string? hat)
    {
        Assert.Equal(hat, MascotCalendar.HatAt(DateTimeOffset.Parse(at)));

        using var stage = new MascotStage(at: DateTimeOffset.Parse(at));
        stage.ShowStanding();
        Assert.Equal(hat, stage.Frame.Hat);
        if (hat is not null)
        {
            Assert.True(MascotArt.Hats.ContainsKey(hat));
        }
    }

    [Fact]
    public void Calm_she_waits_thirty_to_ninety_seconds_between_the_things_she_does()
    {
        using var stage = new MascotStage(seed: 3);
        stage.Director.SetSpell(MascotSpell.Calm);
        stage.ShowStanding();
        var antics = new List<TimeSpan>();
        var wasCalm = true;
        TimeSpan? calmSince = stage.Frames[^1].At;
        for (var minute = 0; minute < 40; minute++)
        {
            stage.Director.Nudge();
            stage.Run(TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(50));
        }
        foreach (var (at, frame) in stage.Frames.Skip(stage.Frames.FindIndex(f => f.Frame is { Pose: "stand", Drop: 0 })))
        {
            var calm = frame.Pose is "stand" or "blink" && frame.Drop == 0 && frame.Props.Count == 0;
            if (calm && !wasCalm)
            {
                calmSince = at;
            }
            else if (!calm && wasCalm && calmSince is { } since && at - since > TimeSpan.FromSeconds(2))
            {
                antics.Add(at - since);
            }
            wasCalm = calm;
        }

        Assert.True(antics.Count > 15);
        Assert.All(antics, spell => Assert.InRange(spell, MascotSpell.Calm.Shortest - TimeSpan.FromSeconds(1), MascotSpell.Calm.Longest + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Tips_come_at_most_every_ten_minutes_never_while_the_user_types_and_not_at_all_turned_off()
    {
        var lines = new FakeMascotLines { NextTip = "Ctrl+J goes to the next tab" };
        using var stage = new MascotStage(seed: 9, lines: lines);
        stage.ShowStanding();
        var tips = new List<TimeSpan>();
        for (var minute = 0; minute < 120; minute++)
        {
            stage.Director.Nudge();
            stage.Run(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50));
            // A quiet half minute, then the user does something: she wakes from any nap.
            stage.Run(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50));
        }
        var shown = stage.Frames.Where(f => f.Frame.Say == lines.NextTip).Select(f => f.At).ToList();
        foreach (var at in shown)
        {
            if (tips.Count == 0 || at - tips[^1] > TimeSpan.FromSeconds(10))
            {
                tips.Add(at);
            }
        }
        Assert.NotEmpty(tips);
        Assert.All(tips.Zip(tips.Skip(1)), pair => Assert.True(pair.Second - pair.First >= MascotDirector.TipsAtMostEvery));

        // Typing all the time: none.
        var from = stage.Frames.Count;
        for (var i = 0; i < 4 * 60; i++)
        {
            stage.Director.Nudge();
            stage.Run(TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(50));
        }
        Assert.DoesNotContain(stage.Since(from), f => f.Say == lines.NextTip);

        // Turned off: none.
        stage.Director.SetTips(false);
        from = stage.Frames.Count;
        for (var minute = 0; minute < 60; minute++)
        {
            stage.Run(TimeSpan.FromSeconds(45), TimeSpan.FromMilliseconds(50));
            stage.Director.Nudge();
            stage.Run(TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(50));
        }
        Assert.DoesNotContain(stage.Since(from), f => f.Say == lines.NextTip);
    }

    [Fact]
    public void Sitting_on_the_edge_her_legs_swing_over_the_front_of_the_box()
    {
        var sitting = MascotAntics.Sit(new Random(1)).Where(s => s.Drop == 2).ToList();

        Assert.NotEmpty(sitting);
        Assert.All(sitting, step => Assert.Equal((true, -2), (Assert.Single(step.Props!).Front, step.Props![0].Bottom)));
        Assert.True(sitting.Select(s => s.Props![0].Name).Distinct().Count() >= 3, "her legs swing");
    }
}
