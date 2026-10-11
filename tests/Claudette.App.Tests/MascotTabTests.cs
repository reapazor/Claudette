using Avalonia.Media;
using Claudette.App.Mascot;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>Claudette on the composer (DESIGN.md §5): what the selected tab tells her, and her lines.</summary>
public class MascotTabTests
{
    private const string Init = """{"type":"system","subtype":"init","session_id":"s1","model":"claude-opus-5-5","permissionMode":"default"}""";

    private const string Done = """{"type":"result","subtype":"success","is_error":false,"session_id":"s1","result":"Done.","duration_ms":10,"num_turns":1}""";

    [Theory]
    [InlineData("Read", MascotTool.Reading)]
    [InlineData("Grep", MascotTool.Reading)]
    [InlineData("Glob", MascotTool.Reading)]
    [InlineData("Edit", MascotTool.Editing)]
    [InlineData("Write", MascotTool.Editing)]
    [InlineData("NotebookEdit", MascotTool.Editing)]
    [InlineData("Bash", MascotTool.Running)]
    [InlineData("PowerShell", MascotTool.Running)]
    [InlineData("WebFetch", MascotTool.Web)]
    [InlineData("WebSearch", MascotTool.Web)]
    [InlineData("Agent", MascotTool.Agents)]
    [InlineData("Task", MascotTool.Agents)]
    [InlineData("mcp__github__create_issue", MascotTool.None)]
    [InlineData("TodoWrite", MascotTool.None)]
    public void Claude_Codes_tools_each_have_what_she_works_with(string name, MascotTool tool) => Assert.Equal(tool, MascotTools.For(name));

    [Fact]
    public async Task She_works_with_what_suits_the_tool_Claude_is_running_and_plans_in_plan_mode()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var director = h.Services.Mascot.Director!;
        await StartTurnAsync(h, tab);
        Assert.Equal(new MascotSituation { Mood = MascotMood.Working }, director.Situation);

        h.Transport.Emit("""{"type":"assistant","message":{"id":"m1","role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Read","input":{"file_path":"a.cs"}}]},"parent_tool_use_id":null,"session_id":"s1"}""");
        await TabTestHarness.Eventually(() => director.Situation.Tool == MascotTool.Reading, "the tool");
        h.Transport.Emit("""{"type":"assistant","message":{"id":"m2","role":"assistant","content":[{"type":"tool_use","id":"t2","name":"Bash","input":{"command":"dotnet test"}}]},"parent_tool_use_id":null,"session_id":"s1"}""");
        await TabTestHarness.Eventually(() => director.Situation.Tool == MascotTool.Running, "the newest tool");

        h.Transport.Emit("""{"type":"system","subtype":"status","permissionMode":"plan"}""");
        await TabTestHarness.Eventually(() => director.Situation.Planning, "plan mode");
    }

    [Fact]
    public async Task While_Claude_Code_compacts_she_sweeps_and_stops_when_its_done()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var director = h.Services.Mascot.Director!;
        await StartTurnAsync(h, tab);

        h.Transport.Emit("""{"type":"system","subtype":"status","status":"compacting","session_id":"s1"}""");
        await TabTestHarness.Eventually(() => director.Situation.Compacting, "compacting");
        h.Transport.Emit("""{"type":"system","subtype":"status","status":null,"session_id":"s1"}""");
        await TabTestHarness.Eventually(() => !director.Situation.Compacting, "compacted");

        h.Transport.Emit("""{"type":"system","subtype":"status","status":"compacting","session_id":"s1"}""");
        await TabTestHarness.Eventually(() => director.Situation.Compacting, "compacting again");
        h.Transport.Emit(Done);
        await TabTestHarness.Eventually(() => !director.Situation.Compacting && director.Mood == MascotMood.Idle, "the turn's end");
    }

    [Fact]
    public async Task She_knows_how_many_other_tabs_wait_on_the_user()
    {
        await using var h = new TabTestHarness();
        h.Factory.ProcessPerSession = true;
        h.Services.Settings.Sessions.RestoreUnpinnedTabs = true;
        h.Services.State.Tabs = [new TabState { Folder = h.WorkFolder, IsPinned = true }, new TabState { Folder = h.WorkFolder }, new TabState { Folder = h.WorkFolder }];
        h.Shell.Restore(null);
        var group = h.Shell.Groups.Single();
        var (a, b, c) = (group.Tabs[0], group.Tabs[1], group.Tabs[2]);
        var director = h.Services.Mascot.Director!;

        c.Status = TabStatus.NeedsInput;
        Assert.Equal(1, director.Situation.OthersWaiting);
        b.Status = TabStatus.NeedsInput;
        Assert.Equal(2, director.Situation.OthersWaiting);

        // The selected tab waiting too: she waves at the user, and still knows of the other two.
        Assert.Same(a, h.Shell.SelectedTab);
        a.Status = TabStatus.NeedsInput;
        Assert.Equal((MascotMood.Waiting, 2), (director.Mood, director.Situation.OthersWaiting));
    }

    [Fact]
    public async Task Her_hair_tie_takes_the_tab_groups_colour()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var group = h.Shell.Groups.Single();
        Assert.Equal(group.Color, tab.GroupColor);

        group.Color = Color.Parse("#25B8B8");
        Assert.Equal(Color.Parse("#25B8B8"), tab.GroupColor);
    }

    [Fact]
    public async Task What_happens_in_the_selected_tab_reaches_her()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var director = h.Services.Mascot.Director!;
        director.SetShown(new object(), true);
        director.SetRoom(MascotStage.DefaultRoom);
        await TabTestHarness.Eventually(() => Advance(h) && director.Frame is { Pose: "stand", Drop: 0 }, "her to stand up");

        // Sending: a paper plane.
        tab.ComposerText = "Refactor the parser";
        await tab.SendCommand.ExecuteAsync(null);
        Assert.Contains(director.Frame.Props, p => p.Name == "plane");
        h.Transport.Emit(Init);
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Working, "the turn");

        // A turn that fails: dizzy.
        h.Transport.Emit("""{"type":"result","subtype":"error_during_execution","is_error":true,"session_id":"s1","result":"Something broke"}""");
        await TabTestHarness.Eventually(() => director.Frame.Props.Any(p => p.Name == "star"), "her to be dizzy");

        // Her name typed: she waves back, once.
        await TabTestHarness.Eventually(() => Advance(h) && director.Frame is { Pose: "stand", Drop: 0 }, "her to recover");
        tab.ComposerText = "hello claudette";
        Assert.Equal("wave", director.Frame.Pose);

        // A huge paste: the crate.
        await TabTestHarness.Eventually(() => Advance(h) && director.Frame is { Pose: "stand", Drop: 0 } && director.Frame.Props.Count == 0, "her to finish waving");
        tab.AddPastedText(string.Join("\n", Enumerable.Repeat("a long line of pasted text", 400)));
        Assert.Contains(director.Frame.Props, p => p.Name == "crate");
    }

    [Fact]
    public async Task Her_lines_name_the_shortcuts_as_bound_now()
    {
        await using var h = new TabTestHarness();
        var lines = h.Services.Mascot;
        var key = h.Services.Tips.Text(KeyboardShortcuts.NextTabNeedingInput)!;

        Assert.Equal($"Another tab needs you · {key}", lines.OthersWaiting(1));
        Assert.Equal($"3 tabs need you · {key}", lines.OthersWaiting(3));
        var tips = Enumerable.Range(0, 200).Select(i => lines.Tip(new Random(i))).Distinct().ToList();
        Assert.Contains($"{key} goes to the next tab waiting for you", tips);
        Assert.All(tips, tip => Assert.DoesNotContain("{0}", tip!));
    }

    [Fact]
    public async Task Her_size_how_often_and_tips_come_from_Settings()
    {
        await using var h = new TabTestHarness();
        Assert.Equal(3, h.Services.Mascot.PixelsPerCell);

        h.Services.Settings.Appearance.ClaudetteSize = ClaudetteSize.Large;
        h.Services.SaveSettings();
        Assert.Equal(4, h.Services.Mascot.PixelsPerCell);
        h.Services.Settings.Appearance.ClaudetteSize = ClaudetteSize.Small;
        h.Services.SaveSettings();
        Assert.Equal(2, h.Services.Mascot.PixelsPerCell);
    }

    private static async Task StartTurnAsync(TabTestHarness h, TabViewModel tab)
    {
        tab.ComposerText = "Go";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit(Init);
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Working, "the turn");
    }

    /// <summary>Moves her clock on a little, for a wait that goes by it.</summary>
    private static bool Advance(TabTestHarness h)
    {
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        return true;
    }
}
