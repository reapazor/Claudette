using Claudette.App.Mascot;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;

namespace Claudette.App.Tests;

/// <summary>Claudette on the composer (DESIGN.md §5): what she does, and when.</summary>
public class MascotTests
{
    private static readonly MascotRoom Room = MascotStage.DefaultRoom;

    [Fact]
    public void Her_poses_and_props_come_from_the_icon_scripts_sprite()
    {
        Assert.Equal(MascotArt.Width, MascotArt.Poses["stand"].Width);
        Assert.Equal(MascotArt.Height, MascotArt.Poses["stand"].Height);
        Assert.All(MascotArt.Poses.Values, pose => Assert.All(pose.Rows, row => Assert.Equal(MascotArt.Width, row.Length)));
        var letters = MascotArt.Poses.Values.Concat(MascotArt.Props.Values).SelectMany(s => s.Rows).SelectMany(r => r).Distinct();
        Assert.All(letters, letter => Assert.True(letter is '.' or MascotArt.ThemeColor || MascotArt.Palette.ContainsKey(letter), $"'{letter}' has no colour"));
    }

    [Fact]
    public void Every_step_she_takes_uses_a_pose_and_props_that_exist()
    {
        var random = new Random(3);
        IEnumerable<MascotStep>[] all =
        [
            MascotAntics.LookAround(random), MascotAntics.Walk(-5), MascotAntics.Walk(5), MascotAntics.Lean(random),
            MascotAntics.Stretch(), MascotAntics.Wave(2), MascotAntics.Hop(), MascotAntics.Startled(),
            MascotAntics.TopplesOff(random), MascotAntics.ClimbUp(), MascotAntics.Duck(), MascotAntics.Typing(random),
            MascotAntics.Doze(random, hourglass: true, settle: true), MascotAntics.WakeUp(), MascotAntics.StandAbout(TimeSpan.FromSeconds(1)),
            MascotAntics.Dance(), MascotAntics.Twirl(), MascotAntics.Yawn(), MascotAntics.TapFoot(), MascotAntics.TossBall(),
            MascotAntics.Puzzled(), MascotAntics.BlowHeart(), MascotAntics.Sneeze(),
        ];
        foreach (var step in all.SelectMany(s => s))
        {
            Assert.True(MascotArt.Poses.ContainsKey(step.Pose), step.Pose);
            Assert.All(step.Props ?? [], prop => Assert.True(MascotArt.Props.ContainsKey(prop.Name), prop.Name));
        }
    }

    [Fact]
    public void She_climbs_up_from_behind_the_box_when_first_shown_and_then_stands_on_its_edge()
    {
        using var stage = new MascotStage();
        Assert.False(stage.Frame.IsVisible);

        stage.Show();

        // First her fingers reach the edge: all of her is still behind the box, but they show.
        Assert.Equal(("climb", MascotArt.Height), (stage.Frame.Pose, stage.Frame.Drop));
        Assert.True(stage.Frame.IsBehind);
        Assert.True(stage.Frame.IsVisible);
        Assert.Equal(new MascotProp("hands", 0, -1), Assert.Single(stage.Frame.Props));
        // She comes up at home.
        Assert.Equal(Room.HomeX, stage.Frame.X);
        stage.Run(TimeSpan.FromSeconds(2.5));
        Assert.Contains(stage.Frames, f => f.Frame.Pose == "climbLookLeft" && f.Frame.Drop == 6);
        stage.Run(TimeSpan.FromSeconds(3));
        Assert.Equal(("stand", 0), (stage.Frame.Pose, stage.Frame.Drop));
        Assert.InRange(stage.Frame.X, Room.Left, Room.MaxX);

        // She pulls herself up between her hands, which stay gripping the edge where they are.
        var pulling = stage.Frames.Select(f => f.Frame).Where(f => f.Props.Any(p => p.Name == "hands")).Skip(1).ToList();
        Assert.All(pulling, f => Assert.Equal(new MascotProp("hands", 0, 0), Assert.Single(f.Props)));
        Assert.Equal(pulling.Select(f => f.Drop).OrderDescending(), pulling.Select(f => f.Drop));
        Assert.Equal((MascotArt.Height, 4), (pulling[0].Drop, pulling[^1].Drop));
        Assert.Equal(1, pulling.Zip(pulling.Skip(1)).Max(p => p.First.Drop - p.Second.Drop));
    }

    [Fact]
    public void She_stays_in_her_room_tends_to_go_back_home_and_waits_ten_to_thirty_seconds_between_the_things_she_does()
    {
        using var stage = new MascotStage(seed: 7);
        stage.Show();
        var idle = new HashSet<string> { "stand", "blink" };
        TimeSpan? calmSince = null;
        int? calmX = null;
        var calmSpells = new List<TimeSpan>();
        var calmAt = new List<int>();
        var poses = new HashSet<string>();

        // Two hours, with the user doing something every minute so she doesn't nap.
        for (var minute = 0; minute < 120; minute++)
        {
            stage.Director.Nudge();
            stage.Run(TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(50), each: () => Assert.InRange(stage.Frame.X, Room.Left, Room.MaxX));
        }
        foreach (var (at, frame) in stage.Frames)
        {
            poses.Add(frame.Pose);
            var calm = idle.Contains(frame.Pose) && frame.Drop == 0 && frame.Props.Count == 0;
            if (calm)
            {
                calmSince ??= at;
                calmX ??= frame.X;
            }
            else if (calmSince is { } since)
            {
                // A moment standing in the middle of something (between looking left and right, say) isn't a spell.
                if (at - since > TimeSpan.FromSeconds(2))
                {
                    calmSpells.Add(at - since);
                    calmAt.Add(calmX!.Value);
                }
                calmSince = null;
                calmX = null;
            }
        }

        Assert.True(calmSpells.Count > 60, $"only {calmSpells.Count} things in two hours");
        Assert.All(calmSpells, spell => Assert.InRange(spell, MascotSpell.Lively.Shortest - TimeSpan.FromSeconds(1), MascotSpell.Lively.Longest + TimeSpan.FromSeconds(1)));
        Assert.Superset(new HashSet<string> { "lookLeft", "lean", "stretch", "wave", "armsUp", "lookDown", "climb", "waveRight", "back", "yawn", "tap", "achoo" }, poses);
        Assert.Superset(new HashSet<string> { "ball", "heart", "question", "puff" }, stage.Frames.SelectMany(f => f.Frame.Props).Select(p => p.Name).ToHashSet());
        Assert.Contains(poses, p => p.StartsWith("walk", StringComparison.Ordinal));
        // She wanders, but more often than not she's back at home.
        Assert.Contains(calmAt, x => x != Room.HomeX);
        Assert.True(calmAt.Count(x => x == Room.HomeX) > calmAt.Count / 2, $"home {calmAt.Count(x => x == Room.HomeX)} times of {calmAt.Count}");
    }

    [Fact]
    public void Falling_off_on_her_own_happens_at_most_every_three_minutes()
    {
        using var stage = new MascotStage(seed: 11);
        stage.Show();
        for (var minute = 0; minute < 120; minute++)
        {
            stage.Director.Nudge();
            stage.Run(TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(50));
        }

        var falls = stage.Frames.Where(f => f.Frame.Pose == "lookDown").Select(f => f.At).ToList();
        Assert.NotEmpty(falls);
        Assert.All(falls.Zip(falls.Skip(1)), pair => Assert.True(pair.Second - pair.First >= MascotDirector.FallsAtMostEvery));
        // Each fall drops her out of sight, and she always climbs back.
        Assert.False(stage.Frame.IsBehind && stage.Frame.Pose != "climb");
    }

    [Fact]
    public void With_no_room_on_the_edge_she_ducks_behind_the_box_until_there_is()
    {
        using var stage = new MascotStage();
        stage.Show();
        stage.Run(TimeSpan.FromSeconds(5));
        var x = stage.Frame.X;

        stage.Director.SetRoom(null);
        stage.Run(TimeSpan.FromSeconds(1));
        Assert.True(stage.Frame.IsBehind);
        stage.Director.SetMood(MascotMood.Working);
        stage.Run(TimeSpan.FromSeconds(30));
        Assert.True(stage.Frame.IsBehind);

        stage.Director.SetRoom(Room);
        Assert.Equal(("climb", MascotArt.Height, x), (stage.Frame.Pose, stage.Frame.Drop, stage.Frame.X));
        stage.Run(TimeSpan.FromSeconds(5));
        // Back up, she does what the tab's doing.
        Assert.Equal("laptop", Assert.Single(stage.Frame.Props).Name);
    }

    [Fact]
    public void Too_narrow_a_room_is_no_room_and_a_smaller_one_moves_her_into_it()
    {
        using var stage = new MascotStage();
        stage.Show(new MascotRoom(60, 100, Home: 80));
        stage.Run(TimeSpan.FromSeconds(5));

        stage.Director.SetRoom(new MascotRoom(0, 40, Home: 80));
        Assert.Equal(40 - MascotArt.Width, stage.Frame.X);

        stage.Director.SetRoom(new MascotRoom(0, MascotDirector.LeastRoom - 1, Home: 80));
        stage.Run(TimeSpan.FromSeconds(1));
        Assert.True(stage.Frame.IsBehind);
    }

    [Fact]
    public void A_poke_startles_her_and_a_second_one_straight_after_tips_her_off_the_edge_to_climb_back_up()
    {
        using var stage = new MascotStage();
        stage.Show();
        stage.Run(TimeSpan.FromSeconds(5));

        stage.Director.Poke();
        Assert.Equal("armsUp", stage.Frame.Pose);
        Assert.True(stage.Frame.Drop < 0);
        Assert.Equal("bang", Assert.Single(stage.Frame.Props).Name);
        stage.Run(TimeSpan.FromSeconds(0.5));
        var from = stage.Frames.Count;
        stage.Director.Poke();
        stage.Run(TimeSpan.FromSeconds(3));
        Assert.True(stage.Frame.IsBehind);
        // Pokes while she's out of sight do nothing.
        stage.Director.Poke();
        stage.Run(TimeSpan.FromSeconds(8));

        var after = stage.Frames.Skip(from).Select(f => f.Frame).ToList();
        Assert.Equal("lookDown", after[0].Pose);
        Assert.Contains(after, f => f is { Pose: "climb", Drop: MascotArt.Height, IsVisible: true });
        Assert.Equal(("stand", 0), (stage.Frame.Pose, stage.Frame.Drop));
    }

    [Fact]
    public void She_types_while_Claude_works_waves_while_a_prompt_waits_dozes_at_a_limit_and_hops_when_a_turn_finishes()
    {
        using var stage = new MascotStage();
        stage.Show();
        stage.Run(TimeSpan.FromSeconds(5));

        var from = stage.Frames.Count;
        stage.Director.SetMood(MascotMood.Working);
        stage.Run(TimeSpan.FromSeconds(10));
        var working = stage.Frames.Skip(from).Select(f => f.Frame).ToList();
        Assert.Contains(working, f => f.Pose == "typeLeft");
        Assert.Contains(working, f => f.Pose == "typeRight");
        Assert.All(working, f => Assert.Equal("laptop", Assert.Single(f.Props).Name));

        from = stage.Frames.Count;
        stage.Director.SetMood(MascotMood.Waiting);
        stage.Run(TimeSpan.FromSeconds(20));
        var waiting = stage.Frames.Skip(from).Select(f => f.Frame).ToList();
        Assert.Equal("wave", waiting[0].Pose);
        Assert.Contains(waiting, f => f.Pose == "waveHigh");
        Assert.True(waiting.Count(f => f.Pose == "wave") >= 8, "she waves straight away and again 15 seconds on");

        from = stage.Frames.Count;
        stage.Director.SetMood(MascotMood.Resting);
        stage.Run(TimeSpan.FromSeconds(10));
        Assert.Contains(stage.Frames.Skip(from), f => f.Frame.Props.Any(p => p.Name.StartsWith("hourglass", StringComparison.Ordinal)));
        Assert.Contains(stage.Frames.Skip(from), f => f.Frame.Props.Any(p => p.Name == "bigZ"));

        stage.Director.SetMood(MascotMood.Working);
        stage.Run(TimeSpan.FromSeconds(2));
        // The turn's result comes before the tab goes idle: she hops once it has.
        stage.Director.TurnFinished();
        stage.Run(TimeSpan.FromSeconds(2));
        from = stage.Frames.Count;
        stage.Director.SetMood(MascotMood.Idle);
        stage.Run(TimeSpan.FromSeconds(2));
        Assert.Contains(stage.Frames.Skip(from), f => f.Frame is { Pose: "armsUp", Drop: < -1.5 });
    }

    [Fact]
    public void After_five_quiet_minutes_she_naps_and_typing_wakes_her()
    {
        using var stage = new MascotStage(seed: 5);
        stage.Show();
        stage.Run(MascotDirector.NapAfter + TimeSpan.FromMinutes(2), TimeSpan.FromMilliseconds(50));
        Assert.Equal("leanBlink", stage.Frame.Pose);
        Assert.Contains(stage.Frames, f => f.Frame.Props.Any(p => p.Name == "z"));

        stage.Director.Nudge();
        stage.Run(TimeSpan.FromSeconds(2));
        Assert.Equal(("stand", 0), (stage.Frame.Pose, stage.Frame.Drop));
        Assert.Empty(stage.Frame.Props);
    }

    [Fact]
    public void With_motion_reduced_she_stands_still_in_the_pose_for_the_tabs_mood()
    {
        using var stage = new MascotStage();
        stage.Director.SetStill(true);
        stage.Show();

        // No climbing up: she's there, at home.
        Assert.Equal(new MascotFrame("stand", Room.HomeX, 0, []), stage.Frame);
        var count = stage.Frames.Count;
        stage.Run(TimeSpan.FromMinutes(10), TimeSpan.FromMilliseconds(100));
        Assert.Equal(count, stage.Frames.Count);
        stage.Director.Poke();
        Assert.Equal(count, stage.Frames.Count);

        stage.Director.SetMood(MascotMood.Working);
        Assert.Equal(("typeLeft", "laptop"), (stage.Frame.Pose, Assert.Single(stage.Frame.Props).Name));
        stage.Run(TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(100));
        Assert.Equal(count + 1, stage.Frames.Count);

        stage.Director.SetRoom(null);
        Assert.True(stage.Frame.IsBehind);
        stage.Director.SetRoom(Room);
        Assert.Equal(("typeLeft", 0), (stage.Frame.Pose, stage.Frame.Drop));

        // Motion back on, she carries on from where she stands.
        stage.Director.SetStill(false);
        stage.Run(TimeSpan.FromSeconds(1));
        Assert.Contains(stage.Frames, f => f.Frame.Pose == "typeRight");
    }

    [Fact]
    public void Out_of_sight_nothing_ticks_and_she_moves_while_any_view_shows_her()
    {
        using var stage = new MascotStage();
        var other = new object();
        stage.Show();
        stage.Director.SetShown(other, true);
        stage.Run(TimeSpan.FromSeconds(5));

        // The tab she was on is hidden as the next is shown, in either order.
        stage.Director.SetShown(stage.View, false);
        Assert.True(stage.Director.IsShown);
        stage.Director.SetShown(other, false);
        Assert.False(stage.Director.IsShown);
        var count = stage.Frames.Count;
        stage.Run(TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(100));
        Assert.Equal(count, stage.Frames.Count);

        stage.Director.SetShown(other, true);
        stage.Run(TimeSpan.FromSeconds(10));
        Assert.True(stage.Frames.Count > count);
    }

    [Fact]
    public async Task She_is_on_by_default_her_menu_takes_her_away_and_the_setting_brings_her_back()
    {
        await using var h = new TabTestHarness();
        Assert.True(h.Services.Settings.Appearance.ShowClaudette);
        Assert.NotNull(h.Services.Mascot.Director);

        h.Services.Mascot.HideCommand.Execute(null);
        Assert.False(h.Services.Settings.Appearance.ShowClaudette);
        Assert.Null(h.Services.Mascot.Director);

        h.Services.Settings.Appearance.ShowClaudette = true;
        h.Services.SaveSettings();
        Assert.NotNull(h.Services.Mascot.Director);
    }

    [Fact]
    public async Task She_follows_what_the_selected_tab_is_doing()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var director = h.Services.Mascot.Director!;
        Assert.True(tab.IsSelected);
        Assert.Equal(MascotMood.Idle, director.Mood);

        tab.ComposerText = "Refactor the parser";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit("""{"type":"system","subtype":"init","session_id":"s1","model":"claude-opus-5-5","permissionMode":"default"}""");
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Working);
        Assert.Equal(MascotMood.Working, director.Mood);

        h.Transport.Emit("""{"type":"result","subtype":"success","is_error":false,"session_id":"s1","result":"Done.","duration_ms":10,"num_turns":1}""");
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Idle);
        Assert.Equal(MascotMood.Idle, director.Mood);
    }
}
