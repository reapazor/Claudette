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
        Assert.Null(waiting.ContextPercent);
        Assert.Equal(ContextLevel.None, waiting.ContextLevel);
        Assert.False(waiting.ShowContextRing);
        Assert.Null(waiting.ContextTip);
        // The started one has its context from get_context_usage.
        Assert.True(selected.ShowContextRing);
    }

    [Fact]
    public async Task The_ring_is_muted_then_amber_near_auto_compact_then_red_when_nearly_full()
    {
        await using var h = new TabTestHarness();
        var usage = new JsonObject { ["totalTokens"] = 40000, ["maxTokens"] = 200000, ["percentage"] = 20 };
        h.Transport.Answers["get_context_usage"] = _ => usage.DeepClone().AsObject();
        var tab = await h.OpenTabAsync();

        Assert.Equal(20, tab.ContextPercent);
        Assert.Equal(ContextLevel.Normal, tab.ContextLevel);
        Assert.Equal(72, tab.ContextSweep);
        Assert.True(tab.ShowContextRing);
        Assert.Equal("Context 20% (40,000 of 200,000 tokens)", tab.ContextTip);

        // Near the point where Claude Code compacts by itself.
        usage = new JsonObject
        {
            ["totalTokens"] = 150000, ["maxTokens"] = 200000, ["percentage"] = 75,
            ["autoCompactThreshold"] = 160000, ["isAutoCompactEnabled"] = true,
        };
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => tab.ContextPercent == 75, "the next context usage");
        Assert.True(tab.IsContextHigh);
        Assert.Equal(ContextLevel.High, tab.ContextLevel);
        Assert.False(tab.IsContextCritical);
        Assert.Equal("Context 75% (150,000 of 200,000 tokens · auto-compacts at 160,000)", tab.ContextTip);

        // Nearly full: with auto-compact off, say.
        usage = new JsonObject { ["totalTokens"] = 192000, ["maxTokens"] = 200000, ["percentage"] = 96 };
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => tab.ContextPercent == 96, "the last context usage");
        Assert.Equal(ContextLevel.Critical, tab.ContextLevel);
        Assert.True(tab.IsContextCritical);
        Assert.Equal(96 * 3.6, tab.ContextSweep, precision: 6);
    }

    [Fact]
    public async Task The_estimate_fills_the_ring_when_get_context_usage_isnt_there()
    {
        await using var h = new TabTestHarness();
        h.Transport.Answers["get_context_usage"] = _ => throw new InvalidOperationException("Unknown control request: get_context_usage");
        var tab = await h.OpenTabAsync();
        Assert.False(tab.ShowContextRing);

        tab.ComposerText = "go";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit("""{"type":"assistant","message":{"id":"m1","model":"claude-opus-5-5","content":[{"type":"text","text":"a"}],"usage":{"input_tokens":3000,"output_tokens":100,"cache_read_input_tokens":46900}}}""");
        h.Transport.Emit("""{"type":"result","subtype":"success","is_error":false,"session_id":"s1","modelUsage":{"claude-opus-5-5":{"inputTokens":49900,"outputTokens":100,"contextWindow":200000}}}""");

        await TabTestHarness.Eventually(() => tab.ContextPercent is not null, "the estimate");
        Assert.Equal(25, tab.ContextPercent!.Value, precision: 6);
        Assert.Equal(ContextLevel.Normal, tab.ContextLevel);
        Assert.Equal("Context 25% (about 50,000 of 200,000 tokens, estimated from the last call)", tab.ContextTip);
    }

    [Fact]
    public async Task Settings_can_turn_the_ring_off()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var settings = new SettingsViewModel(h.Services, null);
        Assert.True(settings.ShowContextOnTabs);
        Assert.True(tab.ShowContextRing);
        var changed = new List<string?>();
        tab.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        settings.ShowContextOnTabs = false;

        Assert.False(h.Services.Settings.Appearance.ShowContextOnTabs);
        Assert.False(tab.ShowContextRing);
        Assert.Contains(nameof(TabViewModel.ShowContextRing), changed);
        // The composer bar's indicator stays.
        Assert.NotNull(tab.ContextText);
    }
}
