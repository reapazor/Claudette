using Claudette.Core.Diffs;
using Claudette.Core.ProjectTools;
using Claudette.Core.ProjectTools.Godot;
using Claudette.Core.Settings;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.ProjectTools;

/// <summary>Godot projects (DESIGN.md §18, "Godot"), in temporary folders. No real Godot is ever run.</summary>
public sealed class GodotProviderTests : IDisposable
{
    private const string Godot4CSharp = """
        ; Engine configuration file.
        ; It's best edited using the editor UI and not directly.

        config_version=5

        [application]

        config/name="Night Owl"
        run/main_scene="res://main.tscn"
        config/features=PackedStringArray("4.3", "C#", "Forward Plus")
        config/icon="res://icon.svg"

        [dotnet]

        project/assembly_name="Night Owl"
        """;

    private const string Godot3 = """
        config_version=4

        [application]

        config/name="Old Owl"
        config/features=PoolStringArray( "GLES3" )
        """;

    private readonly TempFolder _temp = new("claudette-godot");
    private readonly GodotProvider _godot = new();

    public void Dispose() => _temp.Dispose();

    private ProjectToolContext Context(ToolOS os = ToolOS.Linux, IFileProbe? probe = null, ProjectToolSettings? settings = null, IProjectMemory? memory = null, ISystemProcesses? processes = null) => new()
    {
        OS = os,
        Settings = settings ?? new ProjectToolSettings(),
        Paths = new ProjectToolPaths(_temp.Combine("home"), LocalAppData: _temp.Combine("local"), Applications: _temp.Combine("Applications")),
        Probe = probe ?? new Probe(),
        Memory = memory ?? NoProjectMemory.Instance,
        Processes = processes,
    };

    private ProjectInfo Describe(string file, ProjectToolContext context) =>
        _godot.Describe(new ProjectCandidate(GodotProvider.KindId, file, "x"), context);

    private static ProjectAction Action(ProjectInfo info, string id) => info.Actions.Single(a => a.Id == id);

    [Fact]
    public void A_folder_with_project_godot_is_a_Godot_project()
    {
        _temp.CreateFolder("repo/.git");
        var file = _temp.Write("repo/game/project.godot", Godot4CSharp);

        Assert.Equal([file], _godot.Find(_temp.Combine("repo"), Context()).Select(c => c.Path));
        Assert.Equal([file], _godot.Find(_temp.CreateFolder("repo/game/scripts"), Context()).Select(c => c.Path));
    }

    [Fact]
    public void Project_godot_gives_the_name_version_and_language()
    {
        var four = GodotProject.Parse(Godot4CSharp, "/g/project.godot", "/g", hasCSharpFiles: false);
        Assert.Equal(("Night Owl", 5, "4.3", true, ".godot"), (four.Name, four.ConfigVersion, four.Version, four.IsCSharp, four.ImportFolder));
        Assert.Equal(["4.3", "C#", "Forward Plus"], four.Features);

        var three = GodotProject.Parse(Godot3, "/g/old/project.godot", "/g/old", hasCSharpFiles: false);
        Assert.Equal(("Old Owl", 4, "3", false, ".import"), (three.Name, three.ConfigVersion, three.Version, three.IsCSharp, three.ImportFolder));
        Assert.True(GodotProject.Parse(Godot3, "/g/old/project.godot", "/g/old", hasCSharpFiles: true).IsCSharp);

        var bare = GodotProject.Parse("", "/g/Plain/project.godot", "/g/Plain", false);
        Assert.Equal(("Plain", null), (bare.Name, bare.Version));
    }

    // ---- Finding Godot --------------------------------------------------------------------------------------------------

    [Fact]
    public void Godot_is_looked_for_on_the_PATH_under_its_usual_names()
    {
        var file = _temp.Write("game/project.godot", Godot4CSharp);
        var probe = new Probe { OnPath = { ["godot4"] = "/usr/bin/godot4" } };

        Assert.Equal("/usr/bin/godot4", GodotExecutables.Find(GodotProject.Read(file), Context(probe: probe)));
        probe.OnPath["godot"] = "/usr/local/bin/godot";
        Assert.Equal("/usr/local/bin/godot", GodotExecutables.Find(GodotProject.Read(file), Context(probe: probe)));
        Assert.Equal(@"C:\Tools\godot.exe", GodotExecutables.Detect(Context(ToolOS.Windows, new Probe { OnPath = { ["godot.exe"] = @"C:\Tools\godot.exe" } })));
    }

    [Fact]
    public void On_macOS_Godot_app_is_found_in_Applications()
    {
        var mono = _temp.Write("Applications/Godot_mono.app/Contents/MacOS/Godot", "");

        Assert.Equal(mono, GodotExecutables.Detect(Context(ToolOS.MacOS)));
        var plain = _temp.Write("Applications/Godot.app/Contents/MacOS/Godot", "");
        Assert.Equal(plain, GodotExecutables.Detect(Context(ToolOS.MacOS)));
    }

    [Fact]
    public void On_Windows_Scoop_and_WinGet_places_are_looked_in()
    {
        var winget = _temp.Write("local/Microsoft/WinGet/Packages/GodotEngine.GodotEngine_Microsoft.Winget.Source_8wekyb3d8bbwe/Godot_v4.3-stable_win64.exe", "");
        _temp.Write("local/Microsoft/WinGet/Packages/GodotEngine.GodotEngine_Microsoft.Winget.Source_8wekyb3d8bbwe/Godot_v4.3-stable_win64_console.exe", "");
        Assert.Equal(winget, GodotExecutables.Detect(Context(ToolOS.Windows)));

        var scoop = _temp.Write("home/scoop/shims/godot.exe", "");
        Assert.Equal(scoop, GodotExecutables.Detect(Context(ToolOS.Windows)));
    }

    [Fact]
    public void A_pick_for_the_project_wins_then_Settings_path()
    {
        var file = _temp.Write("game/project.godot", Godot4CSharp);
        var setting = _temp.Write("tools/Godot_v4.3-stable_mono_linux.x86_64", "");
        var picked = _temp.Write("picked/godot-custom", "");
        var probe = new Probe { OnPath = { ["godot"] = "/usr/bin/godot" } };

        Assert.Equal(setting, GodotExecutables.Find(GodotProject.Read(file), Context(probe: probe, settings: new ProjectToolSettings { GodotPath = setting })));
        var memory = new DictionaryMemory { Values = { [(file, GodotExecutables.ExecutableKey)] = picked } };
        Assert.Equal(picked, GodotExecutables.Find(GodotProject.Read(file), Context(probe: probe, settings: new ProjectToolSettings { GodotPath = setting }, memory: memory)));
        Assert.Equal((picked, null), GodotExecutables.Validate(picked, ToolOS.Linux));
        Assert.Null(GodotExecutables.Validate(_temp.Write("notes.txt", ""), ToolOS.Linux).Path);
    }

    [Fact]
    public void Not_found_offers_the_pick_and_a_C_sharp_project_needs_the_dotnet_build()
    {
        var file = _temp.Write("game/project.godot", Godot4CSharp);

        var missing = Describe(file, Context());
        Assert.StartsWith("Godot wasn't found.", missing.Problem, StringComparison.Ordinal);
        Assert.Equal("Choose Godot executable…", missing.Fix!.Label);
        Assert.False(Action(missing, "open-in-godot").IsEnabled);

        var plain = _temp.Write("bin/godot", "");
        var wrongBuild = Describe(file, Context(settings: new ProjectToolSettings { GodotPath = plain }));
        Assert.Contains("isn't the .NET build of Godot", wrongBuild.Problem, StringComparison.Ordinal);

        _temp.CreateFolder("bin/GodotSharp");
        Assert.Null(Describe(file, Context(settings: new ProjectToolSettings { GodotPath = plain })).Problem);
        Assert.True(GodotExecutables.IsDotNetBuild("/opt/Godot_v4.3-stable_mono_linux.x86_64"));
        var mac = _temp.Write("Mac.app/Contents/MacOS/Godot", "");
        Assert.False(GodotExecutables.IsDotNetBuild(mac));
        _temp.CreateFolder("Mac.app/Contents/Resources/GodotSharp");
        Assert.True(GodotExecutables.IsDotNetBuild(mac));
    }

    // ---- Actions ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Open_and_run_are_detached_and_C_sharp_projects_build_their_solution()
    {
        var godot = _temp.Write("bin/godot-mono", "");
        var file = _temp.Write("game/project.godot", Godot4CSharp);
        var root = _temp.Combine("game");
        var context = Context(settings: new ProjectToolSettings { GodotPath = godot });

        var before = Describe(file, context);
        Assert.Equal(["open-in-godot", "run-project", "build-csharp", "open-solution", "clean", "kill-editors"], before.Actions.Select(a => a.Id));
        Assert.Equal(["--editor", "--path", root], Action(before, "open-in-godot").Process!.Arguments);
        Assert.True(Action(before, "open-in-godot").Process!.Detached);
        Assert.True(Action(before, "open-in-godot").IsMain);
        Assert.Equal(["--path", root], Action(before, "run-project").Process!.Arguments);
        Assert.False(Action(before, "build-csharp").IsEnabled);
        Assert.False(Action(before, "open-solution").IsEnabled);

        var csproj = _temp.Write("game/Night Owl.csproj", "<Project />");
        var build = Action(Describe(file, context), "build-csharp").Process!;
        Assert.Equal("dotnet", build.FileName);
        Assert.Equal(["build", csproj], build.Arguments);
        var sln = _temp.Write("game/Night Owl.sln", "");
        var after = Describe(file, context);
        Assert.Equal(["build", sln], Action(after, "build-csharp").Process!.Arguments);
        Assert.Equal(sln, Action(after, "open-solution").OpenPath);
        Assert.Equal("Godot 4.3", after.ShortVersion);
    }

    [Fact]
    public void A_GDScript_project_has_no_C_sharp_actions()
    {
        var file = _temp.Write("old/project.godot", Godot3);

        Assert.Equal(["open-in-godot", "run-project", "clean", "kill-editors"], Describe(file, Context()).Actions.Select(a => a.Id));
    }

    [Fact]
    public void Clean_deletes_dot_godot_for_Godot_4_and_dot_import_for_Godot_3_and_nothing_else()
    {
        var four = _temp.Write("four/project.godot", Godot4CSharp);
        _temp.Write("four/.godot/imported/icon.svg-1.ctex", "x");
        _temp.Write("four/.import/stale", "x");
        _temp.Write("four/main.tscn", "x");
        var three = _temp.Write("three/project.godot", Godot3);
        _temp.Write("three/.import/icon.png-1.stex", "x");

        var cleanFour = Assert.IsType<DeleteFolders>(Action(Describe(four, Context()), "clean").Destructive);
        var cleanThree = Assert.IsType<DeleteFolders>(Action(Describe(three, Context()), "clean").Destructive);

        Assert.Equal([_temp.Combine("four", ".godot")], cleanFour.Folders());
        Assert.Equal([_temp.Combine("three", ".import")], cleanThree.Folders());
        Assert.Contains("reimports every asset", cleanFour.Warning, StringComparison.Ordinal);
        Assert.Equal("Clean .godot…", Action(Describe(four, Context()), "clean").Label);
    }

    [Fact]
    public void Kill_all_Godot_editors_matches_any_Godot_build()
    {
        var file = _temp.Write("game/project.godot", Godot4CSharp);
        var processes = new FakeSystemProcesses();
        Assert.Equal("No Godot editor is running", Action(Describe(file, Context(processes: processes)), "kill-editors").DisabledReason);

        processes.Running.Add(new SystemProcess(4, "Godot_v4.3-stable_mono_win64", @"Godot_v4.3-stable_mono_win64.exe --editor --path ""D:\Games\Night Owl"""));
        processes.Running.Add(new SystemProcess(5, "godot", "godot --path /home/matt/owl"));
        var kill = Assert.IsType<KillProcesses>(Action(Describe(file, Context(processes: processes)), "kill-editors").Destructive);

        Assert.Equal(["Night Owl", "owl"], processes.Find(kill.Names).Select(p => kill.ProjectOf!(p)));
    }

    [Fact]
    public void The_note_says_the_version_the_language_and_how_to_check_and_build()
    {
        var project = GodotProject.Parse(Godot4CSharp, "/g/owl/project.godot", "/g/owl", hasCSharpFiles: false);

        var note = GodotProvider.SystemPromptNote(project, "/opt/godot-mono", "/g/owl/Night Owl.sln");

        Assert.Equal(
            """
            This is a Godot 4.3 project (C#), Night Owl, at /g/owl.
            To check it without the editor, run: /opt/godot-mono --headless --path /g/owl --quit
            To check one script, run: /opt/godot-mono --headless --path /g/owl --check-only --script res://path/to/script.gd
            To build the C# code, run: dotnet build '/g/owl/Night Owl.sln'
            Don't open the editor unless asked.
            """.ReplaceLineEndings("\n"), note);
        Assert.StartsWith("This is a Godot 3 project (GDScript)", GodotProvider.SystemPromptNote(GodotProject.Parse(Godot3, "/g/project.godot", "/g", false), null, null), StringComparison.Ordinal);
        var file = _temp.Write("game/project.godot", Godot4CSharp);
        Assert.Null(Describe(file, Context(settings: new ProjectToolSettings { TellClaudeAboutGodot = false })).SystemPromptNote);
    }

    private sealed class Probe : IFileProbe
    {
        public Dictionary<string, string> OnPath { get; } = [];

        public bool FileExists(string path) => File.Exists(path);

        public string? FindOnPath(string fileName) => OnPath.GetValueOrDefault(fileName);

        public string ExpandEnvironmentVariables(string path) => path;
    }
}
