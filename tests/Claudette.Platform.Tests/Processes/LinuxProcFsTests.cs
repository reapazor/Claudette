using System.Text;
using Claudette.Platform.Processes.Unix;

namespace Claudette.Platform.Tests.Processes;

public sealed class LinuxProcFsTests
{
    [Fact]
    public void Stat_fields_are_read()
    {
        const string stat = "4321 (sleep) S 4300 4300 1200 34816 4300 4194304 97 0 0 0 5 3 0 0 20 0 1 0 987654 5439488 130 18446744073709551615 1 1 0 0 0 0 0 0 0 0 0 0 17 2 0 0 0 0 0\n";

        var parsed = LinuxProcFs.ParseStat(stat);

        Assert.Equal(new LinuxStat(4321, "sleep", 'S', 4300, UserTicks: 5, SystemTicks: 3, StartTicks: 987654), parsed);
        Assert.Equal(987654, parsed!.Value.StartKey);
        Assert.False(parsed.Value.IsZombie);
    }

    [Theory]
    [InlineData("12 (tmux: server) S 1 12 12 0 -1 0 0 0 0 0 7 9 0 0 20 0 1 0 555 0 0", "tmux: server")]
    [InlineData("12 (a) (b)) R 1 12 12 0 -1 0 0 0 0 0 7 9 0 0 20 0 1 0 555 0 0", "a) (b)")]
    [InlineData("12 () S 1 12 12 0 -1 0 0 0 0 0 7 9 0 0 20 0 1 0 555 0 0", "")]
    public void The_name_can_hold_spaces_and_parentheses(string stat, string name)
    {
        var parsed = LinuxProcFs.ParseStat(stat);

        Assert.NotNull(parsed);
        Assert.Equal(name, parsed.Value.Comm);
        Assert.Equal(1, parsed.Value.ParentPid);
        Assert.Equal(7, parsed.Value.UserTicks);
        Assert.Equal(9, parsed.Value.SystemTicks);
        Assert.Equal(555, parsed.Value.StartTicks);
    }

    [Fact]
    public void Zombies_are_recognized()
    {
        var parsed = LinuxProcFs.ParseStat("77 (defunct) Z 1 77 77 0 -1 0 0 0 0 0 0 0 0 0 20 0 1 0 900 0 0");

        Assert.True(parsed!.Value.IsZombie);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("12 (short) S 1 2 3")]
    [InlineData("x (bad pid) S 1 12 12 0 -1 0 0 0 0 0 7 9 0 0 20 0 1 0 555 0 0")]
    [InlineData("12 (bad number) S 1 12 12 0 -1 0 0 0 0 0 seven 9 0 0 20 0 1 0 555 0 0")]
    public void Malformed_stat_is_rejected(string stat)
    {
        Assert.Null(LinuxProcFs.ParseStat(stat));
    }

    [Fact]
    public void Resident_size_comes_from_VmRSS()
    {
        const string status = "Name:\tnode\nUmask:\t0022\nState:\tS (sleeping)\nVmPeak:\t 1203448 kB\nVmRSS:\t   51236 kB\nRssAnon:\t   20000 kB\n";

        Assert.Equal(51236L * 1024, LinuxProcFs.ParseVmRssBytes(status));
    }

    [Fact]
    public void Kernel_threads_have_no_VmRSS()
    {
        Assert.Null(LinuxProcFs.ParseVmRssBytes("Name:\tkthreadd\nState:\tS (sleeping)\nThreads:\t1\n"));
    }

    [Fact]
    public void Command_line_arguments_are_joined_with_spaces()
    {
        var bytes = Encoding.UTF8.GetBytes("sh\0-c\0sleep 30 & sleep 31 & wait\0");

        Assert.Equal("sh -c sleep 30 & sleep 31 & wait", LinuxProcFs.ParseCmdline(bytes));
    }

    [Fact]
    public void An_empty_command_line_is_null()
    {
        Assert.Null(LinuxProcFs.ParseCmdline([]));
        Assert.Null(LinuxProcFs.ParseCmdline([0]));
    }

    [Fact]
    public void Non_ascii_command_lines_are_decoded()
    {
        Assert.Equal("echo héllo", LinuxProcFs.ParseCmdline(Encoding.UTF8.GetBytes("echo\0héllo\0")));
    }

    [Fact]
    public void Boot_time_is_read_from_proc_stat()
    {
        const string stat = "cpu  1 2 3 4\ncpu0 1 2 3 4\nintr 12345\nctxt 999\nbtime 1790000000\nprocesses 4242\n";

        Assert.Equal(1790000000, LinuxProcFs.ParseBootTime(stat));
        Assert.Null(LinuxProcFs.ParseBootTime("cpu 1 2 3\n"));
    }
}
