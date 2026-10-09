using Claudette.Platform.Processes;

namespace Claudette.Platform.Tests.Processes;

/// <summary>A process's arguments cut short for its row (DESIGN.md §4, "Process monitor").</summary>
public sealed class ProcessArgumentsTests
{
    [Theory]
    [InlineData(@"node C:\app\node_modules\vite\bin\vite.js --port 5173", null, "vite.js --port 5173")]
    [InlineData(@"""C:\Program Files\nodejs\node.exe"" ""C:\My App\server.js"" --watch", null, "server.js --watch")]
    [InlineData(@"C:\Program Files\nodejs\node.exe server.js", @"C:\Program Files\nodejs\node.exe", "server.js")]
    [InlineData("/usr/bin/python3 -m http.server 8000", "/usr/bin/python3", "-m http.server 8000")]
    [InlineData(@"dotnet test D:\Repos\App\App.slnx --filter Category!=Live", null, "test App.slnx --filter Category!=Live")]
    [InlineData(@"UnrealEditor -project=C:\Games\NightOwl\NightOwl.uproject -log", null, "-project=NightOwl.uproject -log")]
    [InlineData("curl https://example.com/api/v1 -o out/ /tmp/a", null, "https://example.com/api/v1 -o out/ a")]
    [InlineData(@"cmd /d /s /c ""git status""", null, "/d /s /c \"git status\"")]
    [InlineData("python3\n-c\nprint(1)", null, "-c print(1)")]
    public void Leaves_out_the_program_and_cuts_paths_to_their_last_part(string commandLine, string? executablePath, string expected) =>
        Assert.Equal(expected, ProcessArguments.Short(commandLine, executablePath));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("node")]
    [InlineData(@"""C:\Program Files\nodejs\node.exe""")]
    public void Is_null_without_arguments(string? commandLine) => Assert.Null(ProcessArguments.Short(commandLine, null));

    [Fact]
    public void A_long_one_is_cut_off()
    {
        var shown = ProcessArguments.Short("node " + string.Join(' ', Enumerable.Repeat("--flag", 100)), null)!;

        Assert.Equal(201, shown.Length);
        Assert.EndsWith("…", shown, StringComparison.Ordinal);
    }
}
