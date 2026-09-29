using Claudette.Core.ProjectTools;

namespace Claudette.Core.Tests.ProjectTools;

/// <summary>Quoting for cmd.exe, bash and the user's shell (DESIGN.md §18, "Running .bat files on Windows").</summary>
public sealed class CommandLineTests
{
    [Theory]
    [InlineData("NightOwlEditor", "NightOwlEditor")]
    [InlineData("-WaitMutex", "-WaitMutex")]
    [InlineData(@"C:\Games\NightOwl.uproject", @"C:\Games\NightOwl.uproject")]
    [InlineData(@"C:\My Games\NightOwl.uproject", @"""C:\My Games\NightOwl.uproject""")]
    [InlineData(@"-Project=C:\Games\NightOwl.uproject", @"-Project=""C:\Games\NightOwl.uproject""")]
    [InlineData(@"-project=C:\My Games\A&B\NightOwl.uproject", @"-project=""C:\My Games\A&B\NightOwl.uproject""")]
    [InlineData(@"C:\R&D\x", @"""C:\R&D\x""")]
    [InlineData("(1)", @"""(1)""")]
    [InlineData("", @"""""")]
    [InlineData("a=b c", @"""a=b c""")]
    public void A_batch_argument_is_quoted_when_cmd_would_read_it_otherwise(string argument, string quoted)
    {
        Assert.Equal(quoted, CommandLines.QuoteForBatch(argument));
    }

    [Fact]
    public void A_batch_file_runs_through_cmd_with_its_whole_command_in_one_pair_of_quotes()
    {
        var spec = CommandLines.BatchFile(@"C:\Program Files\UE\Build.bat", ["Game", @"-Project=C:\A B\Game.uproject"], @"C:\A B");

        Assert.Equal("cmd.exe", spec.FileName);
        Assert.Equal(["/d", "/s", "/c", @"""C:\Program Files\UE\Build.bat"" Game -Project=""C:\A B\Game.uproject"""], spec.Arguments);
        Assert.Equal(@"/d /s /c """"C:\Program Files\UE\Build.bat"" Game -Project=""C:\A B\Game.uproject""""", spec.CommandLine);
        Assert.Equal(@"C:\A B", spec.WorkingDirectory);
    }

    [Theory]
    [InlineData("say \"hi\"")]
    [InlineData("two\nlines")]
    public void A_quote_or_line_break_is_refused_rather_than_passed_to_cmd(string argument)
    {
        Assert.Throws<ArgumentException>(() => CommandLines.BatchFile(@"C:\Build.bat", [argument]));
        Assert.Throws<ArgumentException>(() => CommandLines.BatchFile($@"C:\{argument}.bat", []));
    }

    [Fact]
    public void A_users_command_runs_through_cmd_on_Windows_and_their_shell_elsewhere()
    {
        var windows = CommandLines.ShellCommand("dotnet test \"My Game.sln\" && echo done", ToolOS.Windows, shell: "/bin/zsh", @"C:\Game");
        Assert.Equal("cmd.exe", windows.FileName);
        Assert.Equal("/d /s /c \"dotnet test \"My Game.sln\" && echo done\"", windows.CommandLine);
        Assert.Equal(@"C:\Game", windows.WorkingDirectory);

        var mac = CommandLines.ShellCommand("./run.sh 'a b'", ToolOS.MacOS, shell: "/bin/zsh", "/game");
        Assert.Equal("/bin/zsh", mac.FileName);
        Assert.Equal(["-c", "./run.sh 'a b'"], mac.Arguments);
        Assert.Null(mac.CommandLine);

        Assert.Equal("/bin/sh", CommandLines.ShellCommand("make", ToolOS.Linux, shell: null).FileName);
        Assert.Throws<ArgumentException>(() => CommandLines.ShellCommand("a\nb", ToolOS.Windows, null));
    }

    [Fact]
    public void Commands_display_as_they_would_be_typed()
    {
        Assert.Equal("dotnet test", CommandLines.Display(CommandLines.ShellCommand("dotnet test", ToolOS.Windows, null)));
        Assert.Equal("make -j8", CommandLines.Display(CommandLines.ShellCommand("make -j8", ToolOS.Linux, "/bin/bash")));
        Assert.Equal("'/Users/Shared/Epic Games/Build.sh' -x", CommandLines.Display(CommandLines.ShellScript("/Users/Shared/Epic Games/Build.sh", ["-x"])));
        Assert.Equal("/opt/editor '/My Game/a.uproject' -debug", CommandLines.Display(new Processes.ProcessStartSpec("/opt/editor", ["/My Game/a.uproject", "-debug"])));
        Assert.Equal(@"""C:\Program Files\UE\UnrealEditor.exe"" ""D:\My Game\a.uproject""",
            CommandLines.Display(new Processes.ProcessStartSpec(@"C:\Program Files\UE\UnrealEditor.exe", [@"D:\My Game\a.uproject"])));
    }
}
