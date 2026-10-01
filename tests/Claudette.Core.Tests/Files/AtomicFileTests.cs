using Claudette.Core.Files;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.Files;

public sealed class AtomicFileTests : IDisposable
{
    private readonly TempFolder _temp = new("claudette-atomic");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Writes_replace_the_file_and_leave_no_temporary_files()
    {
        var path = _temp.Combine("sub", "file.json");

        AtomicFile.WriteAllText(path, "one");
        await AtomicFile.WriteAllTextAsync(path, "two ✓", TestContext.Current.CancellationToken);

        Assert.Equal("two ✓", AtomicFile.ReadAllText(path));
        Assert.Equal(["file.json"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
        // No byte order mark.
        Assert.Equal((byte)'t', File.ReadAllBytes(path)[0]);
    }

    [Fact]
    public void Temporary_names_are_unique_and_recognized()
    {
        var a = AtomicFile.TempPath("x.json");
        var b = AtomicFile.TempPath("x.json");

        Assert.NotEqual(a, b);
        Assert.True(AtomicFile.IsTemp(a));
        Assert.False(AtomicFile.IsTemp("x.json"));
    }

    [Fact]
    public void A_missing_file_isnt_waited_for()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Throws<FileNotFoundException>(() => AtomicFile.ReadAllText(_temp.Combine("missing.json")));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_rename_waits_out_a_file_held_open_for_a_moment()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to rename over an open file.");
        var path = _temp.Combine("held.json");
        File.WriteAllText(path, "old");
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () =>
        {
            await Task.Delay(150, TestContext.Current.CancellationToken);
            await held.DisposeAsync();
        }, TestContext.Current.CancellationToken);

        AtomicFile.WriteAllText(path, "new");
        await release;

        Assert.Equal("new", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(typeof(IOException), true)]
    [InlineData(typeof(UnauthorizedAccessException), true)]
    [InlineData(typeof(FileNotFoundException), false)]
    [InlineData(typeof(DirectoryNotFoundException), false)]
    [InlineData(typeof(InvalidOperationException), false)]
    public void Only_a_file_in_use_is_tried_again(Type type, bool inUse)
    {
        Assert.Equal(inUse, FileRetry.IsInUse((Exception)Activator.CreateInstance(type)!));
    }
}
