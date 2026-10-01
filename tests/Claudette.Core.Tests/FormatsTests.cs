namespace Claudette.Core.Tests;

/// <summary>Times, durations and names written the same everywhere they show.</summary>
public class FormatsTests
{
    [Theory]
    [InlineData(30, "just now")]
    [InlineData(5 * 60, "5 min ago")]
    [InlineData(2 * 3600 + 59, "2 h ago")]
    [InlineData(30 * 3600, "yesterday")]
    [InlineData(3 * 86400 + 5, "3 days ago")]
    public void Ago(int seconds, string text) => Assert.Equal(text, Formats.Ago(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(-3, "0s")]
    [InlineData(8, "8s")]
    [InlineData(65, "1m 05s")]
    [InlineData(3720, "1h 02m")]
    public void Elapsed_ticks_with_seconds_until_an_hour(int seconds, string text) => Assert.Equal(text, Formats.Elapsed(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(8, "8s")]
    [InlineData(185, "3m")]
    [InlineData(3720, "1h 02m")]
    [InlineData(2 * 86400 + 3 * 3600 + 7, "2d 3h")]
    public void Duration_is_in_round_figures(int seconds, string text) => Assert.Equal(text, Formats.Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void A_folders_name_ignores_a_separator_at_its_end()
    {
        var folder = Path.Combine(Path.GetTempPath(), "api");
        Assert.Equal("api", Formats.FolderName(folder));
        Assert.Equal("api", Formats.FolderName(folder + Path.DirectorySeparatorChar));
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Equal(root, Formats.FolderName(root));
    }

    [Fact]
    public void Capitalize() => Assert.Equal(("High", ""), (Formats.Capitalize("high"), Formats.Capitalize("")));
}
