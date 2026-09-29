using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Platform.Notifications;

namespace Claudette.App.Tests;

/// <summary>The Dock icon and taskbar button while tabs work or wait (DESIGN.md §10): the animation and the flash.</summary>
public class AppIconTests
{
    private const string BashRequest = """
        {"type":"control_request","request_id":"p1","request":{"subtype":"can_use_tool","tool_name":"Bash","tool_use_id":"t1","input":{"command":"npm test"}}}
        """;

    private static async Task<TabViewModel> StartWorkingAsync(TabTestHarness h)
    {
        var tab = await h.OpenTabAsync();
        tab.ComposerText = "long job";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit("""{"type":"system","subtype":"init","session_id":"s1","model":"claude-opus-5-5"}""");
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Working, "working");
        return tab;
    }

    private static async Task NeedInputAsync(TabViewModel tab, TabTestHarness h)
    {
        h.Transport.Emit(BashRequest);
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.NeedsInput, "needs input");
    }

    private static async Task AnswerAsync(TabViewModel tab)
    {
        tab.Items.OfType<PermissionItem>().Single().AllowCommand.Execute(null);
        await TabTestHarness.Eventually(() => tab.Status != TabStatus.NeedsInput, "the answer");
    }

    [Fact]
    public void Every_animation_has_its_frames()
    {
        foreach (var animation in new[] { AppIconAnimations.Spark, AppIconAnimations.Typing, AppIconAnimations.Waving })
        {
            Assert.True(animation.Frames.Count > 1, animation.Name);
            Assert.All(animation.Frames, frame => Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], frame.Png[..4]));
        }
    }

    [Fact]
    public async Task While_a_tab_works_the_taskbar_overlay_pulses_the_spark()
    {
        await using var h = new TabTestHarness();
        h.Notifier.Surface = AppIconSurface.Overlay;
        var tab = await StartWorkingAsync(h);

        var spark = AppIconAnimations.Spark.Frames;
        Assert.Same(spark[0].Png, h.Notifier.Frame);
        Assert.Equal("Claude is working", h.Notifier.FrameDescription);
        h.Time.Advance(spark[0].Duration);
        Assert.Same(spark[1].Png, h.Notifier.Frame);
        foreach (var frame in spark.Skip(1))
        {
            h.Time.Advance(frame.Duration);
        }
        Assert.Same(spark[0].Png, h.Notifier.Frame);

        h.Transport.EmitTurn("done");
        await TabTestHarness.Eventually(() => tab.Status != TabStatus.Working, "the turn to end");
        Assert.Null(h.Notifier.Frame);
        h.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(h.Notifier.Frame);
    }

    [Fact]
    public async Task On_the_taskbar_the_count_of_tabs_needing_input_takes_the_sparks_place()
    {
        await using var h = new TabTestHarness();
        h.Notifier.Surface = AppIconSurface.Overlay;
        var tab = await StartWorkingAsync(h);

        await NeedInputAsync(tab, h);
        Assert.Equal(1, h.Notifier.Badge);
        Assert.Null(h.Notifier.Frame);

        await AnswerAsync(tab);
        Assert.Equal(TabStatus.Working, tab.Status);
        Assert.Equal(0, h.Notifier.Badge);
        Assert.Same(AppIconAnimations.Spark.Frames[0].Png, h.Notifier.Frame);
    }

    [Fact]
    public async Task With_the_badge_off_the_spark_keeps_going_while_another_tab_needs_input()
    {
        await using var h = new TabTestHarness(settings => settings.Notifications.Badge = false);
        h.Notifier.Surface = AppIconSurface.Overlay;

        h.Services.Notifications.SetTabActivity(needingInput: 1, working: 1);

        Assert.Equal(0, h.Notifier.Badge);
        Assert.Same(AppIconAnimations.Spark.Frames[0].Png, h.Notifier.Frame);
    }

    [Fact]
    public async Task The_Dock_icon_types_while_tabs_work_and_waves_while_one_needs_input()
    {
        await using var h = new TabTestHarness();
        h.Notifier.Surface = AppIconSurface.Icon;
        var tab = await StartWorkingAsync(h);

        var typing = AppIconAnimations.Typing.Frames;
        Assert.Same(typing[0].Png, h.Notifier.Frame);
        h.Time.Advance(typing[0].Duration);
        Assert.Same(typing[1].Png, h.Notifier.Frame);

        await NeedInputAsync(tab, h);
        Assert.Same(AppIconAnimations.Waving.Frames[0].Png, h.Notifier.Frame);
        Assert.Equal("A tab needs your input", h.Notifier.FrameDescription);
        Assert.Equal(1, h.Notifier.Badge);

        await AnswerAsync(tab);
        Assert.Same(typing[0].Png, h.Notifier.Frame);

        h.Transport.EmitTurn("done");
        await TabTestHarness.Eventually(() => h.Notifier.Frame is null, "Claudette's own icon");
    }

    [Fact]
    public async Task The_animation_can_be_turned_off()
    {
        await using var h = new TabTestHarness();
        h.Notifier.Surface = AppIconSurface.Icon;
        await StartWorkingAsync(h);
        Assert.NotNull(h.Notifier.Frame);

        h.Services.Settings.Notifications.AnimateIcon = false;
        h.Services.SaveSettings();

        Assert.Null(h.Notifier.Frame);
        h.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(h.Notifier.Frame);
    }

    [Fact]
    public async Task The_taskbar_button_flashes_while_a_tab_needs_input_and_Claudette_is_in_the_background()
    {
        await using var h = new TabTestHarness();
        var tab = await StartWorkingAsync(h);

        await NeedInputAsync(tab, h);
        Assert.True(h.Notifier.Flashing);

        await AnswerAsync(tab);
        Assert.False(h.Notifier.Flashing);
    }

    [Fact]
    public async Task Coming_to_the_front_stops_the_flash()
    {
        await using var h = new TabTestHarness();
        var tab = await StartWorkingAsync(h);
        await NeedInputAsync(tab, h);

        h.Services.Notifications.SetAppActive(true);

        Assert.False(h.Notifier.Flashing);
        Assert.Equal(TabStatus.NeedsInput, tab.Status);
    }

    [Fact]
    public async Task No_flash_while_Claudette_is_in_front()
    {
        await using var h = new TabTestHarness();
        var tab = await StartWorkingAsync(h);
        h.Services.Notifications.SetAppActive(true);

        await NeedInputAsync(tab, h);

        Assert.Equal(0, h.Notifier.Flashes);
    }

    [Fact]
    public async Task No_flash_with_the_needs_input_notification_off()
    {
        await using var h = new TabTestHarness(settings => settings.Notifications.NeedsInput = false);
        var tab = await StartWorkingAsync(h);

        await NeedInputAsync(tab, h);

        Assert.Equal(0, h.Notifier.Flashes);
    }
}
