using Claudette.Core.Files;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.Files;

/// <summary>Paths through symbolic links name the folder itself, as git does (DESIGN.md §4, "Cleaning up worktrees").</summary>
public sealed class RealPathTests : IDisposable
{
    private readonly TempFolder _temp = new("claudette-realpath");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void A_link_on_the_way_is_followed_to_the_folder_itself()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A symbolic link to a folder needs Developer Mode on Windows.");
        var real = _temp.CreateFolder("real/inner");
        var link = _temp.Combine("link");
        // The link's target as a relative path, resolved against the link's own folder.
        Directory.CreateSymbolicLink(link, "real");
        var linked = Path.Combine(link, "inner");

        Assert.Equal(RealPath.Resolve(real), RealPath.Resolve(linked));
        Assert.True(RealPath.Same(linked, real));
        Assert.True(RealPath.Same(linked + Path.DirectorySeparatorChar, real));
        Assert.False(RealPath.Same(linked, _temp.Combine("real")));
        // Under the link, a folder that isn't there yet has its real path too.
        Assert.Equal(Path.Combine(RealPath.Resolve(real), "later"), RealPath.Resolve(Path.Combine(linked, "later")));
    }

    [Fact]
    public void A_path_without_links_keeps_its_parts_as_spelled()
    {
        var plain = _temp.CreateFolder("plain");
        var root = RealPath.Resolve(_temp.Path);

        Assert.Equal(Path.Combine(root, "plain"), RealPath.Resolve(plain + Path.DirectorySeparatorChar));
        Assert.Equal(Path.Combine(root, "missing", "deeper"), RealPath.Resolve(_temp.Combine("missing", "deeper")));
        Assert.True(RealPath.Same(plain, Path.Combine(_temp.Path, ".", "plain")));
        Assert.False(RealPath.Same(plain, _temp.Combine("other")));
    }
}
