using Claudette.Core.Updates;

namespace Claudette.Core.Tests.Updates;

/// <summary>Reading and ordering Claudette's release versions (DESIGN.md §2, "Updating Claudette").</summary>
public class AppVersionTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("1.2.3.0", "1.2.3")]
    [InlineData("0.7.0+2b8ce618c24d", "0.7.0")]
    [InlineData("1.3.0-beta.2", "1.3.0-beta.2")]
    [InlineData(" V2.0.0-rc.1+abc ", "2.0.0-rc.1")]
    public void Versions_are_read_from_tags_and_build_metadata_is_dropped(string text, string expected) =>
        Assert.Equal(expected, AppVersion.TryParse(text)?.ToString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.2.x")]
    [InlineData("1..2")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-beta..1")]
    [InlineData("1.2.3.4")]
    public void Anything_else_is_not_a_version(string? text) => Assert.Null(AppVersion.TryParse(text));

    [Theory]
    [InlineData("1.2.3", "1.2.4")]
    [InlineData("1.2.9", "1.10.0")]
    [InlineData("1.3.0-beta.1", "1.3.0")]
    [InlineData("1.3.0-beta.2", "1.3.0-beta.10")]
    [InlineData("1.3.0-alpha", "1.3.0-beta")]
    [InlineData("1.3.0-beta", "1.3.0-beta.1")]
    [InlineData("1.3.0-2", "1.3.0-beta")]
    public void Versions_order_as_semantic_versioning_says(string lower, string higher)
    {
        var a = AppVersion.TryParse(lower)!;
        var b = AppVersion.TryParse(higher)!;
        Assert.True(a < b);
        Assert.True(b > a);
        Assert.Equal(-1, a.CompareTo(b));
    }

    [Fact]
    public void Equal_versions_compare_equal_whatever_their_spelling()
    {
        Assert.Equal(AppVersion.TryParse("v1.2.0"), AppVersion.TryParse("1.2"));
        Assert.Equal(0, AppVersion.TryParse("1.2.3")!.CompareTo(AppVersion.TryParse("1.2.3.0")));
    }

    [Fact]
    public void An_MSIX_package_version_has_four_parts_and_no_prerelease_label() =>
        Assert.Equal("1.3.0.0", AppVersion.TryParse("1.3.0-beta.2")!.ToPackageVersion());
}
