using Claudette.Platform.Processes.Unix;

namespace Claudette.Platform.Tests.Processes;

public sealed class PsOutputTests
{
    // As printed by macOS `ps -axww -o pid=,ppid=,rss=,time=,lstart=,args=` with LC_ALL=C.
    private const string Output =
        "    1     0  14336   2:31.07 Mon Sep 28 08:59:12 2026     /sbin/launchd\n" +
        "  612     1 204800  12:04.50 Mon Sep 28 09:01:44 2026     /Applications/Claudette.app/Contents/MacOS/Claudette\n" +
        "  701   612  98304   0:03.21 Mon Sep 28 10:15:30 2026     /usr/local/bin/node /usr/local/bin/claude --output-format stream-json\n" +
        "  702   701   1024   0:00.00 Tue Sep  1 08:05:03 2026     sleep 30\n" +
        "  703   701      0   0:00.00 Mon Sep 28 10:15:31 2026     <defunct>\n" +
        "\n" +
        "not a process line\n";

    [Fact]
    public void Every_process_line_is_parsed()
    {
        var entries = PsOutput.Parse(Output);

        Assert.Equal([1, 612, 701, 702, 703], entries.Select(e => e.Pid));
    }

    [Fact]
    public void Fields_are_read()
    {
        var claude = PsOutput.Parse(Output).Single(e => e.Pid == 701);

        Assert.Equal(612, claude.ParentPid);
        Assert.Equal(98304, claude.RssKilobytes);
        Assert.Equal(TimeSpan.FromSeconds(3.21), claude.CpuTime);
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 9, 28, 10, 15, 30, DateTimeKind.Local)), claude.StartTime);
        Assert.Equal(claude.StartTime!.Value.ToUnixTimeSeconds(), claude.StartKey);
        Assert.Equal("/usr/local/bin/node /usr/local/bin/claude --output-format stream-json", claude.Args);
        Assert.Equal("/usr/local/bin/node", claude.Program);
    }

    [Fact]
    public void A_space_padded_day_is_read()
    {
        var sleep = PsOutput.Parse(Output).Single(e => e.Pid == 702);

        Assert.Equal(new DateTimeOffset(new DateTime(2026, 9, 1, 8, 5, 3, DateTimeKind.Local)), sleep.StartTime);
        Assert.Equal("sleep", sleep.Program);
        Assert.Equal("sleep 30", sleep.Args);
    }

    [Fact]
    public void Minutes_can_pass_an_hour()
    {
        var app = PsOutput.Parse(Output).Single(e => e.Pid == 612);

        Assert.Equal(TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(4.5), app.CpuTime);
    }

    [Theory]
    [InlineData("0:00.02", 0.02)]
    [InlineData("75:10.50", (75 * 60) + 10.5)]
    [InlineData("01:02:03", 3723)]
    [InlineData("2-03:04:05", (2 * 86400) + (3 * 3600) + (4 * 60) + 5)]
    [InlineData("00:00", 0)]
    public void Cpu_time_formats(string text, double seconds)
    {
        Assert.Equal(seconds, PsOutput.ParseCpuTime(text)!.Value.TotalSeconds, precision: 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12")]
    [InlineData("a:b")]
    [InlineData("1:2:3:4")]
    [InlineData("x-01:02:03")]
    public void Bad_cpu_times_are_rejected(string text)
    {
        Assert.Null(PsOutput.ParseCpuTime(text));
    }

    [Fact]
    public void A_bad_start_time_still_gives_the_process()
    {
        var entry = PsOutput.ParseLine("  9  1  100  0:00.01 Mon Foo 28 10:15:30 2026 /bin/zsh -l");

        Assert.NotNull(entry);
        Assert.Null(entry.StartTime);
        Assert.Equal(0, entry.StartKey);
        Assert.Equal("/bin/zsh -l", entry.Args);
    }

    [Theory]
    [InlineData("  PID  PPID   RSS      TIME STARTED                      ARGS")]
    [InlineData("  9  1  100")]
    [InlineData("  9  1  lots  0:00.01 Mon Sep 28 10:15:30 2026 /bin/zsh")]
    public void Lines_that_dont_fit_are_skipped(string line)
    {
        Assert.Null(PsOutput.ParseLine(line));
    }
}
