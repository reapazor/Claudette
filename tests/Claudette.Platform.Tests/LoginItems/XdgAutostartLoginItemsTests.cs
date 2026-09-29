using Claudette.Core.LoginItems;
using Claudette.Core.Updates;
using Claudette.Platform.LoginItems.Linux;
using Claudette.Platform.Tests.Support;

namespace Claudette.Platform.Tests.LoginItems;

/// <summary>
/// The XDG autostart file that starts Claudette at login on Linux (DESIGN.md §9, "Starting at login"), written to a
/// temporary folder. Only file work, so it runs on every OS.
/// </summary>
public sealed class XdgAutostartLoginItemsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("claudette-autostart-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Autostart => Path.Combine(_root, "autostart");

    [Fact]
    public void The_file_runs_the_build_with_login_and_is_removed_when_turned_off()
    {
        var build = Directory.CreateDirectory(Path.Combine(_root, "My Apps", "Claudette")).FullName;
        File.WriteAllText(Path.Combine(build, "Claudette"), "");
        var items = new XdgAutostartLoginItems(new FakeLauncher(), Autostart);
        Assert.Equal(LoginEntryState.Missing, items.ReadEntry());

        items.WriteEntry(new ClaudetteCopy(AppInstallKind.Other, build, "0.3.0"));

        var lines = File.ReadAllLines(items.FilePath);
        Assert.Equal("[Desktop Entry]", lines[0]);
        Assert.Contains("Type=Application", lines);
        Assert.Contains($"Exec={XdgAutostartLoginItems.ExecArgument(Path.Combine(build, "Claudette"))} --login", lines);
        Assert.Equal(LoginEntryState.Enabled, items.ReadEntry());

        items.DeleteEntry();

        Assert.False(File.Exists(items.FilePath));
        Assert.Equal(LoginEntryState.Missing, items.ReadEntry());
    }

    [Theory]
    [InlineData("Hidden=true")]
    [InlineData("X-GNOME-Autostart-enabled=false")]
    public void A_file_the_desktop_turned_off_is_off(string line)
    {
        var items = new XdgAutostartLoginItems(new FakeLauncher(), Autostart);
        Directory.CreateDirectory(Autostart);
        File.WriteAllText(items.FilePath, $"[Desktop Entry]\nType=Application\nExec=claudette --login\n{line}\n");

        Assert.Equal(LoginEntryState.DisabledByUser, items.ReadEntry());
    }

    [Theory]
    [InlineData("--login", "--login")]
    [InlineData("/opt/claudette/Claudette", "/opt/claudette/Claudette")]
    [InlineData("/home/me/My Apps/Claudette", "\"/home/me/My Apps/Claudette\"")]
    [InlineData("/home/me/50%/Claudette", "/home/me/50%%/Claudette")]
    [InlineData("/home/me/it's/Claudette", "\"/home/me/it's/Claudette\"")]
    [InlineData("/home/me/$HOME/Claudette", "\"/home/me/\\\\$HOME/Claudette\"")]
    [InlineData("a\\b", "\"a\\\\\\\\b\"")]
    [InlineData("", "\"\"")]
    public void Exec_arguments_are_quoted_as_the_Desktop_Entry_spec_says(string argument, string expected)
    {
        Assert.Equal(expected, XdgAutostartLoginItems.ExecArgument(argument));
    }

    [Fact]
    public void Handing_over_at_login_starts_the_copy_detached()
    {
        var launcher = new FakeLauncher();
        var items = new XdgAutostartLoginItems(launcher, Autostart);

        items.Start(new ClaudetteCopy(AppInstallKind.Other, _root, "0.3.0"));

        var spec = Assert.Single(launcher.Specs);
        Assert.True(spec.Detached);
        Assert.Equal([Path.Combine(_root, "Claudette.dll"), "--login"], spec.Arguments);
    }
}
