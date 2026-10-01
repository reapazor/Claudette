using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Settings;
using Claudette.Platform.Notifications;

namespace Claudette.App.Tests;

/// <summary>Accessibility (DESIGN.md §3, "Accessibility"): what screen readers are told, reduced motion and zoom.</summary>
public class AccessibilityTests
{
    [Fact]
    public async Task Prompts_finished_turns_and_errors_are_announced()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();

        h.Transport.Emit("""{"type":"control_request","request_id":"p1","request":{"subtype":"can_use_tool","tool_name":"Bash","tool_use_id":"t1","input":{"command":"npm test"}}}""");
        await TabTestHarness.Eventually(() => h.Shell.Announcement.Length > 0, "the prompt");
        Assert.Equal("work: Allow this command? npm test", h.Shell.Announcement);

        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => h.Shell.Announcement == "work: Claude finished.", "the turn");

        // The same words again still change the live region, so they're read again.
        h.Shell.Announce("work: Claude finished.");
        Assert.NotEqual("work: Claude finished.", h.Shell.Announcement);
        Assert.StartsWith("work: Claude finished.", h.Shell.Announcement, StringComparison.Ordinal);
    }

    // ---- Reduced motion ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Following_the_system_motion_is_reduced_when_the_OS_or_Claude_Code_asks_for_it()
    {
        await using var h = new TabTestHarness();
        var changes = 0;
        h.Services.MotionChanged += (_, _) => changes++;
        Assert.False(h.Services.ReduceMotion);

        h.SystemMotion.PrefersReduced = true;
        await h.Services.ReadMotionPreferencesAsync();
        Assert.True(h.Services.ReduceMotion);
        Assert.Equal(1, changes);

        // Claude Code's documented prefersReducedMotion, in its user settings, counts too.
        h.SystemMotion.PrefersReduced = false;
        var config = Path.Combine(h.Root, "claude-config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "settings.json"), """{ "prefersReducedMotion": true }""");
        h.Services.ClaudeConfigDirectory = config;
        await h.Services.ReadMotionPreferencesAsync();
        Assert.True(h.Services.ReduceMotion);

        // The setting overrides both.
        h.Services.Settings.Appearance.Motion = MotionSetting.Full;
        h.Services.SaveSettings();
        Assert.False(h.Services.ReduceMotion);
        h.Services.Settings.Appearance.Motion = MotionSetting.Reduce;
        h.Services.SaveSettings();
        Assert.True(h.Services.ReduceMotion);
        Assert.Equal(3, changes);
    }

    [Fact]
    public async Task A_read_asked_for_during_another_runs_once_that_ends()
    {
        await using var h = new TabTestHarness();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.SystemMotion.Gate = gate;
        var first = h.Services.ReadMotionPreferencesAsync();

        // Sign-in tells Claudette where Claude Code's settings are while the OS is still being asked.
        var config = Path.Combine(h.Root, "claude-config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "settings.json"), """{ "prefersReducedMotion": true }""");
        h.Services.ClaudeConfigDirectory = config;
        await h.Services.ReadMotionPreferencesAsync();
        Assert.False(h.Services.ReduceMotion);

        gate.SetResult();
        await first;
        Assert.Equal(2, h.SystemMotion.Reads);
        Assert.True(h.Services.ReduceMotion);
    }

    [Fact]
    public async Task Coming_back_to_the_front_reads_the_OS_setting_again_at_most_once_a_minute()
    {
        await using var h = new TabTestHarness();
        await h.Services.ReadMotionPreferencesAsync();
        Assert.Equal(1, h.SystemMotion.Reads);

        await h.Services.ReadMotionPreferencesAsync(onlyIfStale: true);
        Assert.Equal(1, h.SystemMotion.Reads);

        h.SystemMotion.PrefersReduced = true;
        h.Time.Advance(AppServices.MotionRecheckInterval);
        await h.Services.ReadMotionPreferencesAsync(onlyIfStale: true);
        Assert.Equal(2, h.SystemMotion.Reads);
        Assert.True(h.Services.ReduceMotion);
    }

    [Fact]
    public async Task With_motion_reduced_the_working_glyph_and_the_icon_hold_still()
    {
        await using var h = new TabTestHarness();
        h.Notifier.Surface = AppIconSurface.Icon;
        var tab = await h.OpenTabAsync();
        tab.ComposerText = "long job";
        await tab.SendCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Working, "working");
        var typing = AppIconAnimations.Typing.Frames;
        Assert.Same(typing[0].Png, h.Notifier.Frame);

        h.Services.Settings.Appearance.Motion = MotionSetting.Reduce;
        h.Services.SaveSettings();
        for (var i = 0; i < 5; i++)
        {
            h.Time.Advance(WorkingLine.FrameInterval + typing[0].Duration);
            Assert.Equal(WorkingLine.StillGlyph, tab.Working.Glyph);
            // Still, but still saying the tab is working.
            Assert.Same(typing[0].Png, h.Notifier.Frame);
        }

        h.Services.Settings.Appearance.Motion = MotionSetting.Full;
        h.Services.SaveSettings();
        h.Time.Advance(WorkingLine.FrameInterval);
        Assert.NotEqual(WorkingLine.StillGlyph, tab.Working.Glyph);
        h.Time.Advance(typing[0].Duration);
        Assert.Same(typing[1].Png, h.Notifier.Frame);
    }

    // ---- Zoom ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Zoom_goes_up_and_down_in_steps_and_back_to_100_percent()
    {
        await using var h = new TabTestHarness();
        var appearance = h.Services.Settings.Appearance;

        h.Shell.ZoomInCommand.Execute(null);
        Assert.Equal(110, appearance.Zoom);
        Assert.Equal("Zoom 110%", h.Shell.Announcement);
        h.Shell.ZoomOutCommand.Execute(null);
        h.Shell.ZoomOutCommand.Execute(null);
        Assert.Equal(90, appearance.Zoom);
        h.Shell.ResetZoomCommand.Execute(null);
        Assert.Equal(100, appearance.Zoom);

        // It stops at either end.
        appearance.Zoom = 200;
        h.Shell.ZoomInCommand.Execute(null);
        Assert.Equal(200, appearance.Zoom);
        appearance.Zoom = 80;
        h.Shell.ZoomOutCommand.Execute(null);
        Assert.Equal(80, appearance.Zoom);
    }

    [Fact]
    public async Task The_palette_offers_zoom_with_its_shortcuts()
    {
        await using var h = new TabTestHarness();
        var entries = h.Shell.PaletteEntries();
        Assert.Contains(entries, e => e.Label == "Zoom in" && e.Detail == "100%" && e.HasShortcut);
        Assert.DoesNotContain(entries, e => e.Label == "Reset zoom");

        h.Shell.ZoomInCommand.Execute(null);
        Assert.Contains(h.Shell.PaletteEntries(), e => e.Label == "Reset zoom" && e.Detail == "110%");
    }
}
