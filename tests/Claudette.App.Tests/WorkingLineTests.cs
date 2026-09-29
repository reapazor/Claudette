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
        Assert.Equal("0s · Esc to stop", line.Detail);

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

        await TabTestHarness.Eventually(() => tab.Working.Detail == "0s · 3.1k tokens · Esc to stop", "the tokens");
    }

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

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(59, "59s")]
    [InlineData(65, "1m 05s")]
    [InlineData(3720, "1h 02m")]
    public void Elapsed_time_reads_like_a_stopwatch(int seconds, string expected) =>
        Assert.Equal(expected, WorkingLine.Elapsed(TimeSpan.FromSeconds(seconds)));
}
