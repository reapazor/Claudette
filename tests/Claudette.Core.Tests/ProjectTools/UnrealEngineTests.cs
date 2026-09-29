using System.Text.Json.Nodes;
using Claudette.Core.ProjectTools;
using Claudette.Core.ProjectTools.Unreal;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.ProjectTools;

/// <summary>Reading <c>.uproject</c> and <c>Build.version</c>, and finding a project's engine (DESIGN.md §18).</summary>
public sealed class UnrealEngineTests : IDisposable
{
    private readonly TempFolder _temp = new("claudette-unreal");

    public void Dispose() => _temp.Dispose();

    // ---- The .uproject -----------------------------------------------------------------------------------------

    [Fact]
    public void A_uproject_with_comments_trailing_commas_and_unknown_fields_is_read()
    {
        var path = ProjectToolFixtures.UProject(_temp, "NightOwl/NightOwl.uproject", association: "5.4");

        var project = UnrealProject.Read(path);

        Assert.Equal("NightOwl", project.Name);
        Assert.Equal(_temp.Combine("NightOwl"), project.Root);
        Assert.Equal("5.4", project.EngineAssociation);
        Assert.Equal(["NightOwl"], project.Modules);
        Assert.Equal(["ModelingToolsEditorMode"], project.EnabledPlugins);
        Assert.True(project.HasCode);
    }

    [Fact]
    public void The_editor_target_comes_from_the_Editor_Target_cs_files()
    {
        ProjectToolFixtures.UProject(_temp, "NightOwl/NightOwl.uproject", code: false);
        _temp.Write("NightOwl/Source/NightOwlServer.Target.cs", "");
        _temp.Write("NightOwl/Source/OwlEditor.Target.cs", "");

        Assert.Equal("OwlEditor", UnrealProject.Read(_temp.Combine("NightOwl", "NightOwl.uproject")).EditorTarget);

        _temp.Write("NightOwl/Source/NightOwlEditor.Target.cs", "");
        Assert.Equal("NightOwlEditor", UnrealProject.Read(_temp.Combine("NightOwl", "NightOwl.uproject")).EditorTarget);
    }

    [Fact]
    public void Without_targets_the_editor_target_is_the_project_name_and_Editor()
    {
        var path = ProjectToolFixtures.UProject(_temp, "Blueprints/Blueprints.uproject", association: "", code: false);

        var project = UnrealProject.Read(path);

        Assert.Equal("BlueprintsEditor", project.EditorTarget);
        Assert.False(project.HasCode);
        Assert.Null(project.EngineAssociation);
    }

    [Fact]
    public void A_uproject_that_isnt_JSON_still_reads_as_a_project()
    {
        var path = _temp.Write("Broken/Broken.uproject", "{ this isn't json");

        var project = UnrealProject.Read(path);

        Assert.Equal("Broken", project.Name);
        Assert.Null(project.EngineAssociation);
    }

    // ---- Build.version -------------------------------------------------------------------------------------------

    [Fact]
    public void Build_version_gives_the_engine_version()
    {
        var version = UnrealBuildVersion.Parse(JsonNode.Parse("""{ "MajorVersion": 5, "MinorVersion": 4, "PatchVersion": 2, "BranchName": "++UE5+Release-5.4" }"""));

        Assert.Equal(new UnrealBuildVersion(5, 4, 2, "++UE5+Release-5.4"), version);
        Assert.Equal("5.4", version!.Short);
        Assert.Equal("5.4.2", version.Full);
        Assert.Null(UnrealBuildVersion.Parse(JsonNode.Parse("""{ "BranchName": "x" }""")));
    }

    [Fact]
    public void Unreal_Engine_4_names_its_editor_UE4Editor()
    {
        var root = ProjectToolFixtures.Engine(_temp, "UE_4.27", major: 4, minor: 27);

        var engine = UnrealEngineLocator.Engine(root, UnrealEngineKind.Launcher)!;

        Assert.True(engine.IsUE4);
        Assert.Equal("UE4Editor", engine.EditorName);
        Assert.Equal("UnrealEditor", new UnrealEngine(root, UnrealEngineKind.Launcher, new UnrealBuildVersion(5, 4, 0, null)).EditorName);
    }

    // ---- Finding the engine ---------------------------------------------------------------------------------------

    [Fact]
    public void A_version_association_finds_the_launcher_install_in_LauncherInstalled_dat()
    {
        var engine = ProjectToolFixtures.Engine(_temp, "Epic Games/UE_5.4");
        ProjectToolFixtures.Engine(_temp, "Epic Games/UE_5.3", minor: 3);
        _temp.Write("programdata/Epic/UnrealEngineLauncher/LauncherInstalled.dat", $$"""
            {
              "InstallationList": [
                { "InstallLocation": "{{Json(_temp.Combine("Epic Games", "UE_5.3"))}}", "NamespaceId": "ue", "ItemId": "a", "ArtifactId": "UE_5.3", "AppVersion": "5.3.2", "AppName": "UE_5.3" },
                { "InstallLocation": "{{Json(engine)}}", "NamespaceId": "ue", "ItemId": "b", "ArtifactId": "UE_5.4", "AppVersion": "5.4.2", "AppName": "UE_5.4" },
                { "InstallLocation": "C:/Fortnite", "AppName": "Fortnite" }
              ]
            }
            """);
        var project = UnrealProject.Read(ProjectToolFixtures.UProject(_temp, "Game/Game.uproject", association: "5.4"));

        var found = UnrealEngineLocator.Find(project, ProjectToolFixtures.Context(_temp, ToolOS.Windows));

        Assert.Equal(engine, found!.Root);
        Assert.Equal(UnrealEngineKind.Launcher, found.Kind);
        Assert.Equal("5.4.2", found.Version!.Full);
        Assert.Equal("launcher install", found.KindText);
    }

    [Fact]
    public void On_macOS_LauncherInstalled_dat_is_in_Application_Support()
    {
        var engine = ProjectToolFixtures.Engine(_temp, "Shared/Epic Games/UE_5.4");
        _temp.Write("home/Library/Application Support/Epic/UnrealEngineLauncher/LauncherInstalled.dat",
            $$"""{ "InstallationList": [ { "InstallLocation": "{{Json(engine)}}", "AppName": "UE_5.4" } ] }""");
        var project = UnrealProject.Read(ProjectToolFixtures.UProject(_temp, "Game/Game.uproject", association: "5.4"));

        Assert.Equal(engine, UnrealEngineLocator.Find(project, ProjectToolFixtures.Context(_temp, ToolOS.MacOS))!.Root);
        Assert.Null(UnrealEngineLocator.Find(project, ProjectToolFixtures.Context(_temp, ToolOS.Linux)));
    }

    [Fact]
    public void On_Windows_the_registry_is_the_fallback_for_a_launcher_install()
    {
        var engine = ProjectToolFixtures.Engine(_temp, "UE_5.4");
        var registry = new FakeUnrealRegistry { LauncherInstalls = { ["5.4"] = engine } };
        var project = UnrealProject.Read(ProjectToolFixtures.UProject(_temp, "Game/Game.uproject", association: "5.4"));

        Assert.Equal(engine, UnrealEngineLocator.Find(project, ProjectToolFixtures.Context(_temp, ToolOS.Windows, registry: registry))!.Root);
        // Only Windows has a registry.
        Assert.Null(UnrealEngineLocator.Find(project, ProjectToolFixtures.Context(_temp, ToolOS.MacOS, registry: registry)));
    }

    [Theory]
    [InlineData("{E1B2C3D4-0000-4A4A-8888-1234567890AB}", "{E1B2C3D4-0000-4A4A-8888-1234567890AB}")]
    [InlineData("{e1b2c3d4-0000-4a4a-8888-1234567890ab}", "{E1B2C3D4-0000-4A4A-8888-1234567890AB}")]
    [InlineData("E1B2C3D4-0000-4A4A-8888-1234567890AB", "{e1b2c3d4-0000-4a4a-8888-1234567890ab}")]
    [InlineData("{E1B2C3D4-0000-4A4A-8888-1234567890AB}", "e1b2c3d4-0000-4a4a-8888-1234567890ab")]
    public void A_GUID_association_finds_the_registered_source_build_with_or_without_braces_in_any_case(string association, string registered)
    {
        var engine = ProjectToolFixtures.Engine(_temp, "UnrealEngine", installed: false);
        var registry = new FakeUnrealRegistry { Builds = { ["{00000000-0000-0000-0000-000000000000}"] = _temp.Combine("elsewhere"), [registered] = engine } };
        var project = UnrealProject.Read(ProjectToolFixtures.UProject(_temp, "Game/Game.uproject", association: association));

        var found = UnrealEngineLocator.Find(project, ProjectToolFixtures.Context(_temp, ToolOS.Windows, registry: registry));

        Assert.Equal(engine, found!.Root);
        Assert.Equal(UnrealEngineKind.SourceBuild, found.Kind);
        Assert.Equal("source build", found.KindText);
    }

    [Theory]
    [InlineData(ToolOS.Linux, "home/.config/Epic/UnrealEngine/Install.ini")]
    [InlineData(ToolOS.MacOS, "home/Library/Application Support/Epic/UnrealEngine/Install.ini")]
    public void On_macOS_and_Linux_source_builds_are_in_Install_ini(ToolOS os, string ini)
    {
        var engine = ProjectToolFixtures.Engine(_temp, "src/UnrealEngine", installed: false);
        _temp.Write(ini, $"""
            ; Registered by UnrealVersionSelector
            [Other]
            {"{"}E1B2C3D4-0000-4A4A-8888-1234567890AB{"}"}=/wrong/section

            [Installations]
            {"{"}00000000-0000-0000-0000-000000000000{"}"}=/nowhere
            {"{"}e1b2c3d4-0000-4a4a-8888-1234567890ab{"}"}={engine}
            """);
        var project = UnrealProject.Read(ProjectToolFixtures.UProject(_temp, "Game/Game.uproject", association: "{E1B2C3D4-0000-4A4A-8888-1234567890AB}"));

        Assert.Equal(engine, UnrealEngineLocator.Find(project, ProjectToolFixtures.Context(_temp, os))!.Root);
    }

    [Fact]
    public void Install_ini_is_read_tolerantly()
    {
        var builds = UnrealEngineLocator.ParseInstallIni("\uFEFF[Installations]\r\n  {A}  =  \"/Engines/A\"  \r\n# note\r\nnot a pair\r\n{B}=\r\n[installations]\n{C}=/c");

        Assert.Equal(new Dictionary<string, string> { ["{A}"] = "/Engines/A", ["{C}"] = "/c" }, builds);
    }

    [Fact]
    public void An_empty_association_finds_the_engine_in_a_parent_folder()
    {
        var engine = ProjectToolFixtures.Engine(_temp, "UE5", installed: false);
        var project = UnrealProject.Read(ProjectToolFixtures.UProject(_temp, "UE5/Games/NightOwl/NightOwl.uproject", association: ""));

        var found = UnrealEngineLocator.Find(project, ProjectToolFixtures.Context(_temp));

        Assert.Equal(engine, found!.Root);
        Assert.Equal(UnrealEngineKind.ParentFolder, found.Kind);
        Assert.Equal("engine in a parent folder", found.KindText);
    }

    [Fact]
    public void A_missing_engine_is_not_found()
    {
        var registry = new FakeUnrealRegistry { Builds = { ["{AAAA}"] = _temp.Combine("gone") }, LauncherInstalls = { ["5.4"] = _temp.Combine("gone") } };
        foreach (var association in (string?[])["5.4", "{AAAA}", "", "SomethingElse"])
        {
            var project = UnrealProject.Read(ProjectToolFixtures.UProject(_temp, "Game/Game.uproject", association: association));
            Assert.Null(UnrealEngineLocator.Find(project, ProjectToolFixtures.Context(_temp, ToolOS.Windows, registry: registry)));
        }
    }

    [Fact]
    public void A_remembered_engine_folder_wins_and_the_Engine_folder_itself_is_accepted()
    {
        var chosen = ProjectToolFixtures.Engine(_temp, "Custom/UE", installed: false);
        var launcher = ProjectToolFixtures.Engine(_temp, "UE_5.4");
        var registry = new FakeUnrealRegistry { LauncherInstalls = { ["5.4"] = launcher } };
        var path = ProjectToolFixtures.UProject(_temp, "Game/Game.uproject", association: "5.4");
        var memory = new DictionaryMemory { Values = { [(path, UnrealEngineLocator.EngineKey)] = Path.Combine(chosen, "Engine") } };

        var found = UnrealEngineLocator.Find(UnrealProject.Read(path), ProjectToolFixtures.Context(_temp, ToolOS.Windows, memory: memory, registry: registry));

        Assert.Equal(chosen, found!.Root);
        Assert.Equal(UnrealEngineKind.Chosen, found.Kind);
        Assert.Equal((chosen, null), UnrealProvider.ValidateEngineFolder(chosen + Path.DirectorySeparatorChar));
        Assert.Null(UnrealProvider.ValidateEngineFolder(_temp.Combine("Game")).Path);
    }

    private static string Json(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);
}
