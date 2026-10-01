using Claudette.Core.Processes;
using Claudette.Platform.Accessibility;
using Claudette.Platform.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Platform.Tests.Accessibility;

/// <summary>GNOME's reduce-motion setting, read with <c>gsettings</c> (DESIGN.md §3, "Accessibility").</summary>
public class SystemMotionTests
{
    [Theory]
    [InlineData("false", 0, true)]
    [InlineData("true", 0, false)]
    [InlineData("", 1, false)]
    public async Task Animations_turned_off_in_GNOME_ask_for_less_motion(string output, int exitCode, bool reduce)
    {
        var launcher = new FakeLauncher();
        var motion = new GnomeMotion(launcher, new FakeTimeProvider());

        var reading = motion.PrefersReducedMotionAsync(TestContext.Current.CancellationToken);
        var spec = Assert.Single(launcher.Specs);
        launcher.Started[0].WriteOutput(output);
        launcher.Started[0].Exit(exitCode);

        Assert.Equal(reduce, await reading);
        Assert.Equal("gsettings", spec.FileName);
        Assert.Equal(["get", "org.gnome.desktop.interface", "enable-animations"], spec.Arguments);
    }

    [Fact]
    public async Task No_gsettings_is_no_preference()
    {
        var motion = new GnomeMotion(new MissingLauncher(), new FakeTimeProvider());

        Assert.False(await motion.PrefersReducedMotionAsync(TestContext.Current.CancellationToken));
    }

    private sealed class MissingLauncher : IProcessLauncher
    {
        public IRunningProcess Start(ProcessStartSpec spec) => throw new System.ComponentModel.Win32Exception(2, "No such file or directory");
    }
}
