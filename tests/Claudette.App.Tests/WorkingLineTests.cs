using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Claude;

namespace Claudette.App.Tests;

/// <summary>The line above the composer while a turn runs (DESIGN.md §5, "Working line").</summary>
public class WorkingLineTests
{
    private static async Task<(TabTestHarness H, TabViewModel Tab)> WorkingTabAsync(string[]? verbs = null, Action<TabTestHarness>? before = null)
    {
        var h = new TabTestHarness();
        h.Services.Random = new Random(7);
        before?.Invoke(h);
        var tab = await h.OpenTabAsync();
        if (verbs is not null)
        {
            var settings = Path.Combine(h.WorkFolder, ".claude", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            File.WriteAllText(settings, $$"""{ "spinnerVerbs": { "mode": "replace", "verbs": [{{string.Join(", ", verbs.Select(v => $"\"{v}\""))}}] } }""");
            await tab.LoadSpinnerVerbsAsync();
        }
        tab.ComposerText = "go";
        await tab.SendCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Working, "the turn");
        return (h, tab);
    }

    [Fact]
    public async Task A_turn_shows_a_twinkling_verb_its_time_and_how_to_stop_it()
    {
        var (h, tab) = await WorkingTabAsync(["Noodling"]);
        await using var _ = h;
        var line = tab.Working;

        Assert.True(tab.IsWorkingLineShown);
        Assert.True(line.IsActive);
        Assert.Equal("Noodling…", line.Verb);
        Assert.Equal("·", line.Glyph);
        // The Stop shortcut as it reads on this OS: "Esc", or "⎋" on macOS.
        Assert.Equal($"0s · {Stop(h)} to stop", line.Detail);

        h.Time.Advance(WorkingLine.FrameInterval);
        Assert.Equal("✢", line.Glyph);
        h.Time.Advance(TimeSpan.FromSeconds(65));
        Assert.StartsWith("1m 05s · ", line.Detail, StringComparison.Ordinal);
        Assert.Contains(line.Glyph, WorkingLine.Frames);

        h.Transport.Emit("""{"type":"result","subtype":"success","is_error":false,"session_id":"s1"}""");
        await TabTestHarness.Eventually(() => tab.Status != TabStatus.Working, "the end of the turn");
        Assert.False(tab.IsWorkingLineShown);
        Assert.False(line.IsActive);
    }

    [Fact]
    public async Task Tokens_show_as_the_turn_spends_them()
    {
        var (h, tab) = await WorkingTabAsync(["Noodling"]);
        await using var _ = h;

        h.Transport.Emit("""{"type":"assistant","message":{"id":"m1","model":"claude-opus-5-5","content":[{"type":"text","text":"a"}],"usage":{"input_tokens":3000,"output_tokens":100}}}""");

        await TabTestHarness.Eventually(() => tab.Working.Detail == $"0s · 3.1k tokens · {Stop(h)} to stop", "the tokens");
    }

    [Fact]
    public async Task Closing_a_working_tab_stops_its_line()
    {
        var (h, tab) = await WorkingTabAsync(["Noodling"]);
        await using var _ = h;
        var line = tab.Working;

        await tab.CloseAsync(killProcesses: false);
        var glyph = line.Glyph;
        h.Time.Advance(WorkingLine.FrameInterval * 5);

        Assert.False(line.IsActive);
        Assert.Equal(glyph, line.Glyph);
    }

    [Fact]
    public async Task A_tab_working_in_the_background_doesnt_tick_until_its_shown()
    {
        var (h, tab) = await WorkingTabAsync(["Noodling"]);
        await using var _ = h;
        var line = tab.Working;
        // Another tab was selected.
        tab.IsSelected = false;

        var glyph = line.Glyph;
        h.Time.Advance(TimeSpan.FromSeconds(65));
        Assert.Equal(glyph, line.Glyph);
        Assert.True(line.IsActive);

        tab.IsSelected = true;
        // It caught up: the turn's time ran on while hidden.
        Assert.StartsWith("1m 05s · ", line.Detail, StringComparison.Ordinal);
        h.Time.Advance(WorkingLine.FrameInterval);
        Assert.NotEqual(glyph, line.Glyph);
    }

    private static string Stop(TabTestHarness h) => h.Services.Tips.Text(Core.Settings.KeyboardShortcuts.Stop)!;

    [Fact]
    public async Task The_verb_changes_every_few_seconds()
    {
        var (h, tab) = await WorkingTabAsync(["Noodling", "Pondering", "Wibbling"]);
        await using var _ = h;
        var first = tab.Working.Verb;

        h.Time.Advance(WorkingLine.VerbInterval - WorkingLine.FrameInterval);
        Assert.Equal(first, tab.Working.Verb);
        h.Time.Advance(WorkingLine.FrameInterval);

        Assert.NotEqual(first, tab.Working.Verb);
        Assert.Contains(tab.Working.Verb, new[] { "Noodling…", "Pondering…", "Wibbling…" });
    }

    [Fact]
    public async Task Without_spinnerVerbs_the_built_in_verbs_are_used()
    {
        var (h, tab) = await WorkingTabAsync();
        await using var _ = h;

        Assert.Contains(tab.Working.Verb.TrimEnd('…'), SpinnerVerbs.BuiltIn);
    }

    [Fact]
    public async Task With_the_fun_off_it_says_Working_and_keeps_still()
    {
        var (h, tab) = await WorkingTabAsync(["Noodling"], h => h.Services.Settings.Appearance.FunWorkingWords = false);
        await using var _ = h;

        Assert.Equal("Working…", tab.Working.Verb);
        h.Time.Advance(WorkingLine.FrameInterval * 3);
        Assert.Equal(WorkingLine.StillGlyph, tab.Working.Glyph);

        // Turned back on mid-turn: the fun comes back with the next tick.
        h.Services.Settings.Appearance.FunWorkingWords = true;
        h.Time.Advance(WorkingLine.FrameInterval);
        Assert.Equal("Noodling…", tab.Working.Verb);
    }

    [Fact]
    public async Task A_waiting_prompt_hides_the_line_but_the_turns_time_runs_on()
    {
        var (h, tab) = await WorkingTabAsync(["Noodling"]);
        await using var _ = h;

        tab.Status = TabStatus.NeedsInput;
        h.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(tab.IsWorkingLineShown);
        Assert.True(tab.Working.IsActive);

        tab.Status = TabStatus.Working;
        Assert.True(tab.IsWorkingLineShown);
        h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.StartsWith("31s", tab.Working.Detail, StringComparison.Ordinal);
    }

    private static string ToolUse(string id, string name, string input, string? parent = null) => new System.Text.Json.Nodes.JsonObject
    {
        ["type"] = "assistant",
        ["parent_tool_use_id"] = parent,
        ["message"] = new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = $"m-{id}",
            ["content"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = id,
                ["name"] = name,
                ["input"] = System.Text.Json.Nodes.JsonNode.Parse(input),
            }),
        },
    }.ToJsonString();

    private static string ToolResult(string id, string? parent = null) => new System.Text.Json.Nodes.JsonObject
    {
        ["type"] = "user",
        ["parent_tool_use_id"] = parent,
        ["message"] = new System.Text.Json.Nodes.JsonObject
        {
            ["role"] = "user",
            ["content"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = "ok" }),
        },
    }.ToJsonString();

    [Fact]
    public async Task While_a_tool_runs_the_line_says_what_it_is_doing()
    {
        var (h, tab) = await WorkingTabAsync(["Noodling"]);
        await using var _ = h;

        h.Transport.Emit(ToolUse("t1", "Bash", """{"command":"dotnet test"}"""));
        await TabTestHarness.Eventually(() => tab.Working.Verb == "Running dotnet test…", "the tool");
        Assert.Equal("Bash: dotnet test", tab.Working.ActivityDetail);

        // A subagent's own calls show on the agent map, not here.
        h.Transport.Emit(ToolUse("s1", "Read", """{"file_path":"/x/secret.cs"}""", parent: "t0"));
        h.Transport.Emit(ToolResult("t1"));

        await TabTestHarness.Eventually(() => tab.Working.Verb == "Noodling…", "the verb again");
        Assert.Null(tab.Working.ActivityDetail);
    }

    [Fact]
    public async Task Several_agents_at_once_are_counted()
    {
        var (h, tab) = await WorkingTabAsync(["Noodling"]);
        await using var _ = h;

        foreach (var id in new[] { "a1", "a2", "a3" })
        {
            h.Transport.Emit(ToolUse(id, "Agent", """{"description":"Look around"}"""));
        }

        await TabTestHarness.Eventually(() => tab.Working.Verb == "Running 3 agents…", "the agents");
        h.Transport.Emit("""{"type":"result","subtype":"success","is_error":false,"session_id":"s1"}""");
        await TabTestHarness.Eventually(() => !tab.Working.IsActive, "the end of the turn");

        // The next turn starts with a clean slate.
        tab.ComposerText = "again";
        await tab.SendCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => tab.Working.IsActive, "the next turn");
        Assert.Equal("Noodling…", tab.Working.Verb);
    }

    [Fact]
    public async Task With_the_tool_bit_off_the_verb_stays()
    {
        var (h, tab) = await WorkingTabAsync(["Noodling"], h => h.Services.Settings.Appearance.ShowToolInWorkingLine = false);
        await using var _ = h;

        h.Transport.Emit(ToolUse("t1", "Bash", """{"command":"dotnet test"}"""));
        h.Transport.Emit("""{"type":"assistant","message":{"id":"m9","content":[{"type":"text","text":"still here"}],"usage":{"input_tokens":10,"output_tokens":1}}}""");

        await TabTestHarness.Eventually(() => tab.Working.Detail.Contains("tokens", StringComparison.Ordinal), "the next event");
        Assert.Equal("Noodling…", tab.Working.Verb);
    }
}
