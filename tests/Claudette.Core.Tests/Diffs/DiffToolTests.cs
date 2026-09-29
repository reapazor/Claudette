using System.ComponentModel;
using System.Text.RegularExpressions;
using Claudette.Core.Diffs;
using Claudette.Core.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Core.Tests.Diffs;

public sealed partial class DiffToolTests : IDisposable
{
    private static readonly DiffToolSides Sides = new(@"C:\Temp\x\auth (before).cs", @"C:\My Repo\auth.cs", "auth.cs (before)", "auth.cs (now)");

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"claudette-difftool-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 14, 30, 5, 123, TimeSpan.Zero));
    private readonly FakeProcessLauncher _launcher = new();

    public DiffToolTests() => Directory.CreateDirectory(Path.Combine(_root, "repo"));

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    private string TempDirectory => Path.Combine(_root, "temp");

    // --- Custom command templates -------------------------------------------------------------------------------

    [Fact]
    public void A_custom_command_splits_on_quotes_before_substituting()
    {
        Assert.True(DiffToolCommand.TryParse(
            """
            "C:\Program Files\Beyond Compare 5\BCompare.exe" "{left}" "{right}" /title1="{leftTitle}" /title2="{rightTitle}"
            """,
            out var command, out _));

        Assert.Equal(@"C:\Program Files\Beyond Compare 5\BCompare.exe", command.Program);
        Assert.Equal(["{left}", "{right}", "/title1={leftTitle}", "/title2={rightTitle}"], command.ArgumentTemplate);
        Assert.Equal(
            [@"C:\Temp\x\auth (before).cs", @"C:\My Repo\auth.cs", "/title1=auth.cs (before)", "/title2=auth.cs (now)"],
            command.BuildArguments(Sides));
    }

    [Fact]
    public void Unquoted_placeholders_still_become_single_arguments()
    {
        Assert.True(DiffToolCommand.TryParse("/usr/bin/meld {left} {right} --label {leftTitle}", out var command, out _));

        Assert.Equal([@"C:\Temp\x\auth (before).cs", @"C:\My Repo\auth.cs", "--label", "auth.cs (before)"], command.BuildArguments(Sides));
    }

    [Theory]
    [InlineData(@"C:\Tools\diff.exe")]
    [InlineData(@"""C:\My Tools\diff.exe"" /title1={leftTitle}")]
    public void Without_left_and_right_they_are_added_at_the_end(string line)
    {
        Assert.True(DiffToolCommand.TryParse(line, out var command, out _));

        Assert.Equal(["{left}", "{right}"], command.ArgumentTemplate.TakeLast(2));
    }

    [Fact]
    public void Mentioning_one_side_is_left_as_written()
    {
        Assert.True(DiffToolCommand.TryParse("tool --only {RIGHT}", out var command, out _));

        Assert.Equal(["--only", @"C:\My Repo\auth.cs"], command.BuildArguments(Sides));
    }

    [Fact]
    public void Other_braces_and_substituted_values_are_left_alone()
    {
        var sides = Sides with { Left = "{right}" };

        var arguments = DiffToolTemplate.Substitute(["{left}", "{other}", "a{leftTitle}b", "{Right}"], sides);

        Assert.Equal(["{right}", "{other}", "aauth.cs (before)b", @"C:\My Repo\auth.cs"], arguments);
    }

    [Fact]
    public void Empty_quotes_are_an_empty_argument_and_backslashes_are_plain()
    {
        Assert.True(DiffToolTemplate.TryTokenize("""  a ""  "b c"d \x\ "e\" """, out var tokens, out _));

        Assert.Equal(["a", "", "b cd", @"\x\", @"e\"], tokens);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"C:\\Program Files\\tool.exe {left} {right}")]
    [InlineData("\"\" {left} {right}")]
    public void Unusable_commands_report_an_error(string? line)
    {
        Assert.False(DiffToolCommand.TryParse(line, out var command, out var error));
        Assert.Null(command);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    // --- Detection ------------------------------------------------------------------------------------------------

    [Fact]
    public void Windows_presets_are_found_in_install_folders_and_on_path()
    {
        var probe = new FakeFileProbe
        {
            Variables = { ["ProgramFiles"] = @"C:\Program Files", ["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local" },
            Files =
            {
                @"C:\Program Files\Beyond Compare 4\BCompare.exe",
                @"C:\Program Files\Beyond Compare 5\BCompare.exe",
                @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\bin\code.cmd",
            },
            OnPath = { ["git.exe"] = @"C:\Program Files\Git\cmd\git.exe", ["ksdiff"] = @"C:\odd\ksdiff" },
        };

        var detected = DiffToolDetector.Detect(probe, DiffToolPlatform.Windows);

        Assert.Equal(
            [
                (DiffToolPresets.BeyondCompare, @"C:\Program Files\Beyond Compare 5\BCompare.exe"),
                (DiffToolPresets.VSCode, @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\bin\code.cmd"),
                (DiffToolPresets.GitDifftool, @"C:\Program Files\Git\cmd\git.exe"),
            ],
            detected.Select(d => (d.Preset.Id, d.ExecutablePath)));
    }

    [Fact]
    public void Undefined_variables_do_not_match()
    {
        var probe = new FakeFileProbe { Files = { @"%ProgramFiles%\WinMerge\WinMergeU.exe" } };

        Assert.Empty(DiffToolDetector.Detect(probe, DiffToolPlatform.Windows));
    }

    [Fact]
    public void MacOS_presets_include_app_bundles_and_kaleidoscope()
    {
        var probe = new FakeFileProbe
        {
            Files = { "/Applications/Beyond Compare.app/Contents/MacOS/bcomp", "/Applications/p4merge.app/Contents/MacOS/p4merge" },
            OnPath = { ["ksdiff"] = "/usr/local/bin/ksdiff", ["WinMergeU.exe"] = "/odd/WinMergeU.exe" },
        };

        var detected = DiffToolDetector.Detect(probe, DiffToolPlatform.MacOS);

        Assert.Equal([DiffToolPresets.BeyondCompare, DiffToolPresets.Kaleidoscope, DiffToolPresets.P4Merge], detected.Select(d => d.Preset.Id));
    }

    [Fact]
    public void Linux_presets_are_found_on_path()
    {
        var probe = new FakeFileProbe { OnPath = { ["bcompare"] = "/usr/bin/bcompare", ["meld"] = "/usr/bin/meld", ["code"] = "/usr/bin/code" } };

        var detected = DiffToolDetector.Detect(probe, DiffToolPlatform.Linux);

        Assert.Equal([DiffToolPresets.BeyondCompare, DiffToolPresets.VSCode, DiffToolPresets.Meld], detected.Select(d => d.Preset.Id));
    }

    [Fact]
    public void Every_preset_has_placeholders_for_both_sides_on_each_os()
    {
        foreach (var variant in DiffToolPresets.All.SelectMany(p => p.Variants))
        {
            Assert.Contains("{left}", variant.Arguments);
            Assert.Contains("{right}", variant.Arguments);
            Assert.NotEmpty(variant.Paths.Concat(variant.PathNames));
        }
        Assert.Equal(DiffToolPresets.All.Count, DiffToolPresets.All.Select(p => p.Id).Distinct().Count());
        Assert.Null(DiffToolPresets.Find(DiffToolPresets.WinMerge)!.For(DiffToolPlatform.MacOS));
        Assert.Null(DiffToolPresets.Find(DiffToolPresets.Kaleidoscope)!.For(DiffToolPlatform.Windows));
    }

    [Fact]
    public void The_real_probe_expands_variables_and_searches_path()
    {
        Assert.Null(FileProbe.Instance.FindOnPath($"no-such-tool-{Guid.NewGuid():N}"));
        Assert.Equal("%CLAUDETTE_NOT_SET_X%", FileProbe.Instance.ExpandEnvironmentVariables("%CLAUDETTE_NOT_SET_X%"));
        Assert.True(FileProbe.Instance.FileExists(typeof(DiffToolTests).Assembly.Location));
    }

    // --- Launching ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_custom_command_launches_with_the_before_file_and_the_real_file()
    {
        var after = Path.Combine(_root, "repo", "auth.cs");
        await File.WriteAllTextAsync(after, "now\n", TestContext.Current.CancellationToken);
        var choice = new DiffToolChoice(DiffToolKind.Custom, CustomCommand: """ "C:\Tools\My Diff\diff.exe" "{left}" "{right}" /title1="{leftTitle}" /title2="{rightTitle}" """);

        await Launcher().LaunchAsync(choice, "before\n", after, TempDirectory, TestContext.Current.CancellationToken);

        var spec = Assert.Single(_launcher.Started);
        var left = Path.Combine(TempDirectory, "20260928-143005-123", "auth (before).cs");
        Assert.Equal(@"C:\Tools\My Diff\diff.exe", spec.FileName);
        Assert.Equal([left, after, "/title1=auth.cs (before)", "/title2=auth.cs (now)"], spec.Arguments);
        Assert.Equal(Path.Combine(_root, "repo"), spec.WorkingDirectory);
        Assert.Null(spec.Environment);
        Assert.Equal("before\n", await File.ReadAllTextAsync(left, TestContext.Current.CancellationToken));
        Assert.True(File.GetAttributes(left).HasFlag(FileAttributes.ReadOnly));
        Assert.True(_launcher.Processes[0].StandardInputClosed);
    }

    [Fact]
    public async Task A_preset_launches_the_detected_executable_with_its_arguments()
    {
        var probe = new FakeFileProbe { Variables = { ["ProgramFiles"] = @"C:\Program Files" }, Files = { @"C:\Program Files\WinMerge\WinMergeU.exe" } };
        var after = Path.Combine(_root, "repo", "Program.cs");

        await Launcher(probe, DiffToolPlatform.Windows).LaunchAsync(
            new DiffToolChoice(DiffToolKind.Preset, DiffToolPresets.WinMerge), null, after, TempDirectory, TestContext.Current.CancellationToken);

        var spec = Assert.Single(_launcher.Started);
        var left = Path.Combine(TempDirectory, "20260928-143005-123", "Program (before).cs");
        Assert.Equal(@"C:\Program Files\WinMerge\WinMergeU.exe", spec.FileName);
        Assert.Equal(["/e", "/u", "/wl", "/dl", "Program.cs (before)", "/dr", "Program.cs (now)", left, after], spec.Arguments);
        Assert.Equal("", await File.ReadAllTextAsync(left, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Beyond_compare_on_macOS_uses_dash_options()
    {
        var probe = new FakeFileProbe { Files = { "/usr/local/bin/bcomp" } };
        var after = Path.Combine(_root, "repo", "a.txt");

        await Launcher(probe, DiffToolPlatform.MacOS).LaunchAsync(
            new DiffToolChoice(DiffToolKind.Preset, DiffToolPresets.BeyondCompare), "x", after, TempDirectory, TestContext.Current.CancellationToken);

        var spec = Assert.Single(_launcher.Started);
        Assert.Equal("/usr/local/bin/bcomp", spec.FileName);
        Assert.Equal(["-lro", "-title1=a.txt (before)", "-title2=a.txt (now)"], spec.Arguments.Take(3));
    }

    [Fact]
    public async Task Launching_does_not_wait_for_the_tool_and_disposes_it_after_it_exits()
    {
        var launch = Launcher().LaunchAsync(new DiffToolChoice(DiffToolKind.Custom, CustomCommand: "tool"), "x", Path.Combine(_root, "repo", "a.txt"), TempDirectory, TestContext.Current.CancellationToken);
        await launch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var process = Assert.Single(_launcher.Processes);
        Assert.False(process.Disposed.IsCompleted);

        process.Exit(0);

        await process.Disposed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(process.Killed);
    }

    [Fact]
    public async Task A_preset_that_is_not_installed_is_a_clear_error_and_writes_nothing()
    {
        var error = await Assert.ThrowsAsync<DiffToolException>(() => Launcher(new FakeFileProbe(), DiffToolPlatform.Windows).LaunchAsync(
            new DiffToolChoice(DiffToolKind.Preset, DiffToolPresets.BeyondCompare), "x", Path.Combine(_root, "repo", "a.txt"), TempDirectory, TestContext.Current.CancellationToken));

        Assert.Contains("Beyond Compare wasn't found", error.Message, StringComparison.Ordinal);
        Assert.Empty(_launcher.Started);
        Assert.False(Directory.Exists(TempDirectory));
    }

    [Theory]
    [InlineData(DiffToolKind.Preset, "no-such-preset", null, "no diff tool preset")]
    [InlineData(DiffToolKind.Preset, DiffToolPresets.Kaleidoscope, null, "isn't available on this OS")]
    [InlineData(DiffToolKind.Custom, null, "\"unclosed {left}", "can't be used")]
    [InlineData(DiffToolKind.BuiltIn, null, null, "built-in")]
    public async Task Unusable_choices_are_clear_errors(DiffToolKind kind, string? presetId, string? command, string message)
    {
        var error = await Assert.ThrowsAsync<DiffToolException>(() => Launcher(new FakeFileProbe(), DiffToolPlatform.Windows).LaunchAsync(
            new DiffToolChoice(kind, presetId, command), "x", Path.Combine(_root, "repo", "a.txt"), TempDirectory, TestContext.Current.CancellationToken));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.Empty(_launcher.Started);
    }

    [Fact]
    public async Task A_tool_that_cannot_start_is_a_clear_error()
    {
        _launcher.StartFailure = new Win32Exception("The system cannot find the file specified.");

        var error = await Assert.ThrowsAsync<DiffToolException>(() => Launcher().LaunchAsync(
            new DiffToolChoice(DiffToolKind.Custom, CustomCommand: "missing-tool"), "x", Path.Combine(_root, "repo", "a.txt"), TempDirectory, TestContext.Current.CancellationToken));

        Assert.Contains("Couldn't start 'missing-tool'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Q&A.md", false)]
    [InlineData("Q & A.md", true)]
    [InlineData("100%.md", false)]
    [InlineData("plain.md", true)]
    public async Task Batch_file_tools_on_windows_refuse_names_cmd_would_misread(string fileName, bool allowed)
    {
        Assert.SkipWhen(_root.Any(char.IsWhiteSpace), "The temp folder's path has spaces, so every path would be quoted.");
        var probe = new FakeFileProbe { Variables = { ["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local" }, Files = { @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\bin\code.cmd" } };
        var launch = Launcher(probe, DiffToolPlatform.Windows).LaunchAsync(
            new DiffToolChoice(DiffToolKind.Preset, DiffToolPresets.VSCode), "x", Path.Combine(_root, "repo", fileName), TempDirectory, TestContext.Current.CancellationToken);

        if (allowed)
        {
            await launch;
            Assert.Equal(["--diff", "--wait"], Assert.Single(_launcher.Started).Arguments.Take(2));
        }
        else
        {
            var error = await Assert.ThrowsAsync<DiffToolException>(() => launch);
            Assert.Contains("cmd.exe", error.Message, StringComparison.Ordinal);
            Assert.Empty(_launcher.Started);
        }
    }

    [Fact]
    public async Task Test_opens_a_sample_diff_of_two_temporary_files()
    {
        await Launcher().TestAsync(new DiffToolChoice(DiffToolKind.Custom, CustomCommand: "tool {left} {right} {leftTitle} {rightTitle}"), TempDirectory, TestContext.Current.CancellationToken);

        var spec = Assert.Single(_launcher.Started);
        var folder = Path.Combine(TempDirectory, "20260928-143005-123");
        Assert.Equal([Path.Combine(folder, "sample (before).txt"), Path.Combine(folder, "sample.txt"), "sample.txt (before)", "sample.txt (now)"], spec.Arguments);
        Assert.True(File.GetAttributes(spec.Arguments[0]).HasFlag(FileAttributes.ReadOnly));
        Assert.False(File.GetAttributes(spec.Arguments[1]).HasFlag(FileAttributes.ReadOnly));
        Assert.NotEqual(
            await File.ReadAllTextAsync(spec.Arguments[0], TestContext.Current.CancellationToken),
            await File.ReadAllTextAsync(spec.Arguments[1], TestContext.Current.CancellationToken));
    }

    // --- Temporary files ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("auth.cs", "auth (before).cs")]
    [InlineData("Makefile", "Makefile (before)")]
    [InlineData("archive.tar.gz", "archive.tar (before).gz")]
    [InlineData(".gitignore", ".gitignore (before)")]
    public void Before_files_keep_their_extension(string name, string expected)
    {
        Assert.Equal(expected, DiffTempFiles.BeforeFileName(name));
    }

    [Fact]
    public async Task Launches_in_the_same_millisecond_get_their_own_folders()
    {
        var first = await DiffTempFiles.WriteBeforeAsync(TempDirectory, "a.cs", "1", _time.GetUtcNow(), TestContext.Current.CancellationToken);
        var second = await DiffTempFiles.WriteBeforeAsync(TempDirectory, "a.cs", "2", _time.GetUtcNow(), TestContext.Current.CancellationToken);

        Assert.NotEqual(Path.GetDirectoryName(first), Path.GetDirectoryName(second));
        Assert.Equal("20260928-143005-123-2", Path.GetFileName(Path.GetDirectoryName(second)));
    }

    [Fact]
    public async Task Cleanup_deletes_read_only_temp_files_and_leaves_anything_else()
    {
        var before = await DiffTempFiles.WriteBeforeAsync(TempDirectory, "a.cs", "1", _time.GetUtcNow(), TestContext.Current.CancellationToken);
        await DiffTempFiles.WriteBeforeAsync(TempDirectory, "b.cs", "2", _time.GetUtcNow(), TestContext.Current.CancellationToken);
        var foreign = Path.Combine(TempDirectory, "keep-me", "notes.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        await File.WriteAllTextAsync(foreign, "mine", TestContext.Current.CancellationToken);

        Assert.True(DiffTempFiles.Cleanup(TempDirectory));

        Assert.False(File.Exists(before));
        Assert.True(File.Exists(foreign));
        Assert.Single(Directory.EnumerateDirectories(TempDirectory));

        File.Delete(foreign);
        Directory.Delete(Path.GetDirectoryName(foreign)!);
        await DiffTempFiles.WriteBeforeAsync(TempDirectory, "c.cs", "3", _time.GetUtcNow(), TestContext.Current.CancellationToken);
        Assert.True(DiffTempFiles.Cleanup(TempDirectory));
        Assert.False(Directory.Exists(TempDirectory));
        Assert.True(DiffTempFiles.Cleanup(TempDirectory));
    }

    private DiffToolLauncher Launcher(IFileProbe? probe = null, DiffToolPlatform platform = DiffToolPlatform.Linux) =>
        new(_launcher, _time, probe ?? new FakeFileProbe(), platform);

    /// <summary>A file system and environment made up by the test.</summary>
    private sealed partial class FakeFileProbe : IFileProbe
    {
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> OnPath { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool FileExists(string path) => Files.Contains(path);

        public string? FindOnPath(string fileName) => OnPath.GetValueOrDefault(fileName);

        public string ExpandEnvironmentVariables(string path) =>
            VariablePattern().Replace(path, m => Variables.GetValueOrDefault(m.Groups[1].Value) ?? m.Value);

        [GeneratedRegex("%([^%]+)%")]
        private static partial Regex VariablePattern();
    }
}
