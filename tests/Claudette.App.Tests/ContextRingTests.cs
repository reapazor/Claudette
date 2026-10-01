using System.Text.Json.Nodes;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>The context ring on a tab's row (DESIGN.md §4, "Sidebar"; §6, "Per-tab context").</summary>
public class ContextRingTests
{
    [Fact]
    public async Task A_tab_that_hasnt_started_has_no_ring()
    {
        await using var h = new TabTestHarness();
        h.Services.State.Tabs =
        [
            new TabState { Folder = h.WorkFolder, IsPinned = true },
            new TabState { Folder = h.WorkFolder, IsPinned = true },
        ];
        h.Shell.Restore(null);
        var selected = h.Shell.SelectedTab!;
        var waiting = h.Shell.AllTabs.Single(t => t != selected);
        await TabTestHarness.Eventually(() => selected.Status == TabStatus.Idle && selected.IsSettled, "the selected tab to start");

        Assert.Equal(TabStatus.NotStarted, waiting.Status);
        Assert.Null(waiting.Context.Percent);
        Assert.Equal(ContextLevel.None, waiting.Context.Level);
        Assert.False(waiting.Context.ShowRing);
        Assert.Null(waiting.Context.Tip);
        // The started one has its context from get_context_usage.
        Assert.True(selected.Context.ShowRing);
    }

    [Fact]
    public async Task The_ring_is_muted_then_amber_near_auto_compact_then_red_when_nearly_full()
    {
        await using var h = new TabTestHarness();
        var usage = new JsonObject { ["totalTokens"] = 40000, ["maxTokens"] = 200000, ["percentage"] = 20 };
        h.Transport.Answers["get_context_usage"] = _ => usage.DeepClone().AsObject();
        var tab = await h.OpenTabAsync();

        Assert.Equal(20, tab.Context.Percent);
        Assert.Equal(ContextLevel.Normal, tab.Context.Level);
        Assert.Equal(72, tab.Context.Sweep);
        Assert.True(tab.Context.ShowRing);
        Assert.Equal("Context 20% (40,000 of 200,000 tokens)", tab.Context.Tip);

        // Near the point where Claude Code compacts by itself.
        usage = new JsonObject
        {
            ["totalTokens"] = 150000, ["maxTokens"] = 200000, ["percentage"] = 75,
            ["autoCompactThreshold"] = 160000, ["isAutoCompactEnabled"] = true,
        };
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => tab.Context.Percent == 75, "the next context usage");
        Assert.True(tab.Context.IsHigh);
        Assert.Equal(ContextLevel.High, tab.Context.Level);
        Assert.False(tab.Context.IsCritical);
        Assert.Equal("Context 75% (150,000 of 200,000 tokens · auto-compacts at 160,000)", tab.Context.Tip);

        // Nearly full: with auto-compact off, say.
        usage = new JsonObject { ["totalTokens"] = 192000, ["maxTokens"] = 200000, ["percentage"] = 96 };
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => tab.Context.Percent == 96, "the last context usage");
        Assert.Equal(ContextLevel.Critical, tab.Context.Level);
        Assert.True(tab.Context.IsCritical);
        Assert.Equal(96 * 3.6, tab.Context.Sweep, precision: 6);
    }

    [Fact]
    public async Task The_estimate_fills_the_ring_when_get_context_usage_isnt_there()
    {
        await using var h = new TabTestHarness();
        h.Transport.Answers["get_context_usage"] = _ => throw new InvalidOperationException("Unknown control request: get_context_usage");
        var tab = await h.OpenTabAsync();
        Assert.False(tab.Context.ShowRing);

        tab.ComposerText = "go";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit("""{"type":"assistant","message":{"id":"m1","model":"claude-opus-5-5","content":[{"type":"text","text":"a"}],"usage":{"input_tokens":3000,"output_tokens":100,"cache_read_input_tokens":46900}}}""");
        h.Transport.Emit("""{"type":"result","subtype":"success","is_error":false,"session_id":"s1","modelUsage":{"claude-opus-5-5":{"inputTokens":49900,"outputTokens":100,"contextWindow":200000}}}""");

        await TabTestHarness.Eventually(() => tab.Context.Percent is not null, "the estimate");
        Assert.Equal(25, tab.Context.Percent!.Value, precision: 6);
        Assert.Equal(ContextLevel.Normal, tab.Context.Level);
        Assert.Equal("Context 25% (about 50,000 of 200,000 tokens, estimated from the last call)", tab.Context.Tip);
    }

    [Fact]
    public async Task Settings_can_turn_the_ring_off()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var settings = new SettingsViewModel(h.Services, null);
        Assert.True(settings.ShowContextOnTabs);
        Assert.True(tab.Context.ShowRing);
        var changed = new List<string?>();
        tab.Context.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        settings.ShowContextOnTabs = false;

        Assert.False(h.Services.Settings.Appearance.ShowContextOnTabs);
        Assert.False(tab.Context.ShowRing);
        Assert.Contains(nameof(ContextViewModel.ShowRing), changed);
        // The composer bar's indicator stays.
        Assert.NotNull(tab.Context.Text);
    }

    [Fact]
    public void The_indicator_reads_the_same_from_Claude_Code_and_from_the_estimate()
    {
        var reported = ContextIndicator.From(150_000, 200_000, 75, autoCompactAt: 160_000, estimated: false);
        Assert.Equal("Context 75%", reported.Text);
        Assert.Equal("150,000 of 200,000 tokens · auto-compacts at 160,000", reported.Detail);
        // Within a tenth of the point where Claude Code compacts by itself.
        Assert.True(reported.IsHigh);

        var estimated = ContextIndicator.From(50_000, 200_000, 25, autoCompactAt: null, estimated: true);
        Assert.Equal("about 50,000 of 200,000 tokens, estimated from the last call", estimated.Detail);
        Assert.False(estimated.IsHigh);

        // Without auto-compact, 80% is high.
        Assert.True(ContextIndicator.From(160_000, 200_000, 80, autoCompactAt: null, estimated: false).IsHigh);
        Assert.False(ContextIndicator.From(100_000, 200_000, 50, autoCompactAt: 160_000, estimated: true).IsHigh);
    }
}
