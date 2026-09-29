using Claudette.Platform.Processes;

namespace Claudette.Platform.Tests.Processes;

public sealed class ProcessSummaryTests
{
    private const long MB = 1024L * 1024;
    private const long GB = 1024L * MB;

    [Fact]
    public void Counts_everything_but_claude_and_sums_cpu_and_memory_of_the_whole_tree()
    {
        var summary = ProcessSummary.From([
            Process(1, root: true, cpu: 10, memory: 300 * MB),
            Process(2, cpu: 20.25, memory: 500 * MB),
            Process(3, cpu: 12, memory: 326 * MB),
            Process(4, cpu: null, memory: 0),
        ]);

        Assert.Equal(3, summary.Count);
        Assert.Equal(42.25, summary.CpuPercent, precision: 6);
        Assert.Equal(1126 * MB, summary.MemoryBytes);
        Assert.Equal("3 procs · 42% CPU · 1.1 GB", summary.ToString());
    }

    [Fact]
    public void One_process_is_singular()
    {
        var summary = ProcessSummary.From([Process(1, root: true), Process(2, cpu: 0.4, memory: 12 * MB)]);

        Assert.Equal("1 proc · 0% CPU · 12 MB", summary.ToString());
    }

    [Fact]
    public void Just_claude_is_zero_processes()
    {
        var summary = ProcessSummary.From([Process(1, root: true, cpu: 3, memory: 200 * MB)]);

        Assert.Equal("0 procs · 3% CPU · 200 MB", summary.ToString());
    }

    [Fact]
    public void Nothing_sampled_is_empty()
    {
        Assert.Equal("0 procs · 0% CPU · 0 MB", ProcessSummary.From([]).ToString());
        Assert.Equal(ProcessSummary.Empty, ProcessSummary.From([]));
    }

    [Fact]
    public void Several_tabs_add_up_for_the_header()
    {
        var total = ProcessSummary.Sum([new ProcessSummary(3, 20.25, 600 * MB), new ProcessSummary(0, 1.5, 526 * MB), ProcessSummary.Empty]);

        Assert.Equal(new ProcessSummary(3, 21.75, 1126 * MB), total);
        Assert.Equal("22% CPU · 1.1 GB", total.UsageText);
        Assert.Equal(ProcessSummary.Empty, ProcessSummary.Sum([]));
    }

    [Theory]
    [InlineData(41.5, "42%")]
    [InlineData(41.49, "41%")]
    [InlineData(250.7, "251%")]
    public void Cpu_is_rounded_to_a_whole_number(double cpu, string expected)
    {
        Assert.Contains($" {expected} CPU", new ProcessSummary(2, cpu, 0).ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0L, "0 MB")]
    [InlineData(400 * 1024L, "0 MB")]
    [InlineData(600 * 1024L, "1 MB")]
    [InlineData(512 * MB, "512 MB")]
    [InlineData(1023 * MB, "1023 MB")]
    [InlineData(1023 * MB + 900 * 1024, "1.0 GB")]
    [InlineData(GB, "1.0 GB")]
    [InlineData(GB + GB / 10, "1.1 GB")]
    [InlineData(12 * GB + GB / 2, "12.5 GB")]
    public void Memory_is_megabytes_under_a_gigabyte_then_gigabytes_with_one_decimal(long bytes, string expected)
    {
        Assert.Equal(expected, ProcessSummary.FormatMemory(bytes));
    }

    private static ProcessSnapshot Process(int pid, bool root = false, double? cpu = null, long memory = 0) => new()
    {
        Pid = pid,
        ParentPid = root ? 0 : 1,
        Name = "p",
        IsRoot = root,
        CpuPercent = cpu,
        MemoryBytes = memory,
    };
}
