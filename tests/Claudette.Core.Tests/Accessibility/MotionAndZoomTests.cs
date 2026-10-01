using Claudette.Core.Accessibility;
using Claudette.Core.Settings;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.Accessibility;

/// <summary>Reduced motion and zoom (DESIGN.md §3, "Accessibility").</summary>
public class MotionAndZoomTests
{
    [Theory]
    [InlineData(MotionSetting.System, false, false, false)]
    [InlineData(MotionSetting.System, true, false, true)]
    [InlineData(MotionSetting.System, false, true, true)]
    [InlineData(MotionSetting.Reduce, false, false, true)]
    [InlineData(MotionSetting.Full, true, true, false)]
    public void The_setting_decides_or_follows_the_OS_and_Claude_Code(MotionSetting setting, bool system, bool claudeCode, bool reduce) =>
        Assert.Equal(reduce, Motion.Reduce(setting, system, claudeCode));

    [Theory]
    [InlineData("""{ "prefersReducedMotion": true }""", true)]
    [InlineData("""{ "prefersReducedMotion": false }""", false)]
    [InlineData("""{ "prefersReducedMotion": "yes" }""", false)]
    [InlineData("""{ "theme": "dark" }""", false)]
    [InlineData("not json", false)]
    public void Claude_Code_asks_for_less_motion_in_its_user_settings(string json, bool reduce)
    {
        using var folder = new TempFolder();
        folder.Write("settings.json", json);

        Assert.Equal(reduce, Motion.ClaudeCodePrefersReduced(folder.Path));
    }

    [Fact]
    public void No_config_folder_or_settings_file_is_no_preference()
    {
        using var folder = new TempFolder();

        Assert.False(Motion.ClaudeCodePrefersReduced(null));
        Assert.False(Motion.ClaudeCodePrefersReduced(folder.Path));
        Assert.False(Motion.ClaudeCodePrefersReduced(folder.Combine("missing")));
    }

    [Fact]
    public void Zoom_steps_in_and_out_and_stops_at_either_end()
    {
        Assert.Equal(110, Zoom.In(Zoom.Default));
        Assert.Equal(90, Zoom.Out(Zoom.Default));
        Assert.Equal(200, Zoom.In(200));
        Assert.Equal(80, Zoom.Out(80));
        // A value edited by hand goes to the next step either way.
        Assert.Equal(125, Zoom.In(115));
        Assert.Equal(110, Zoom.Out(115));
        Assert.Equal(200, Zoom.Clamp(400));
        Assert.Equal(80, Zoom.Clamp(10));
    }
}
