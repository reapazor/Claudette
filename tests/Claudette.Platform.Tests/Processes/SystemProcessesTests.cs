using Claudette.Core.ProjectTools;
using Claudette.Platform.Processes;
using Claudette.Platform.Processes.Unix;

namespace Claudette.Platform.Tests.Processes;

/// <summary>Finding processes by name for project tools (DESIGN.md §18): an editor with a project open, Kill all editors.</summary>
public sealed class SystemProcessesTests
{
    private const string Editor = "/Users/Shared/Epic Games/UE_5.4/Engine/Binaries/Mac/UnrealEditor.app/Contents/MacOS/UnrealEditor";

    [Fact]
    public void On_macOS_a_program_with_spaces_in_its_path_is_named_by_the_file_that_exists()
    {
        var entries = PsOutput.Parse(
            $"  900     1 204800  12:04.50 Mon Sep 28 09:01:44 2026     {Editor} /Users/matt/My Games/NightOwl/NightOwl.uproject -debug\n" +
            "  901   900   1024   0:00.00 Mon Sep 28 09:01:45 2026     /Users/Shared/Epic Games/UE_5.4/Engine/Binaries/Mac/ShaderCompileWorker -xyz\n" +
            "  902     1   1024   0:00.00 Mon Sep 28 09:01:45 2026     /usr/bin/vim notes-about-UnrealEditor.txt\n" +
            "  903     1   1024   0:00.00 Mon Sep 28 09:01:45 2026     /Applications/Godot.app/Contents/MacOS/Godot --editor\n" +
            "  904     1   1024   0:00.00 Mon Sep 28 09:01:45 2026     /Applications/Claudette.app/Contents/MacOS/Claudette\n");
        var files = new HashSet<string> { Editor, "/usr/bin/vim", "/Applications/Godot.app/Contents/MacOS/Godot", "/Applications/Claudette.app/Contents/MacOS/Claudette" };

        var found = SystemProcesses.MatchPs(entries, ["UnrealEditor", "UE4Editor"], self: 904, files.Contains);

        var editor = Assert.Single(found);
        Assert.Equal((900, "UnrealEditor"), (editor.Pid, editor.Name));
        Assert.True(SystemProcessNames.CommandLineMentions(editor, "/Users/matt/My Games/NightOwl/NightOwl.uproject"));
        Assert.Equal([903], SystemProcesses.MatchPs(entries, ["godot*"], self: 904, files.Contains).Select(p => p.Pid));
        Assert.Empty(SystemProcesses.MatchPs(entries, ["Claudette"], self: 904, files.Contains));
    }

    [Theory]
    [InlineData("UnrealEditor.exe", true)]
    [InlineData("unrealeditor", true)]
    [InlineData("UnrealEditor-Cmd.exe", false)]
    [InlineData("UE4Editor", false)]
    public void Names_match_ignoring_case_and_exe(string name, bool matches)
    {
        Assert.Equal(matches, SystemProcessNames.Matches(name, ["UnrealEditor"]));
        Assert.True(SystemProcessNames.Matches("Godot_v4.3-stable_mono_win64.exe", ["godot*"]));
    }

    [Fact]
    public void A_command_line_mentions_a_path_with_either_slash_in_any_case()
    {
        var process = new SystemProcess(1, "UnrealEditor", @"""C:\UE\UnrealEditor.exe"" ""d:/games/nightowl/NightOwl.uproject""");

        Assert.True(SystemProcessNames.CommandLineMentions(process, @"D:\Games\NightOwl\NightOwl.uproject"));
        Assert.False(SystemProcessNames.CommandLineMentions(process with { CommandLine = null }, @"D:\Games\NightOwl\NightOwl.uproject"));
    }

    [Fact]
    public void The_real_process_list_finds_a_running_process_by_name_and_never_Claudette_itself()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsWindows(), "Reading every process runs ps on macOS, which is covered above.");
        var system = new SystemProcesses(new Core.Processes.ProcessLauncher(), TimeProvider.System);
        var self = Path.GetFileNameWithoutExtension(Environment.ProcessPath)!;

        Assert.DoesNotContain(system.Find([self]), p => p.Pid == Environment.ProcessId);
        Assert.Empty(system.Find(["no-such-process-claudette-test"]));
    }
}
