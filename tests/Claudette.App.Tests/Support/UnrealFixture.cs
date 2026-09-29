using Claudette.Core.ProjectTools;
using Claudette.Core.ProjectTools.Unreal;

namespace Claudette.App.Tests.Support;

/// <summary>A pretend Unreal Engine and project in temporary folders (DESIGN.md §18). No real engine is ever used.</summary>
internal static class UnrealFixture
{
    /// <summary>
    /// Unreal Engine 5.4 in <paramref name="engineRoot"/> (its Build.version, build script and editor, all empty) and
    /// NightOwl.uproject in <paramref name="folder"/>, with an empty EngineAssociation: the engine is found in a parent
    /// folder when <paramref name="folder"/> is under <paramref name="engineRoot"/>.
    /// </summary>
    public static string Write(string engineRoot, string folder, bool code = true)
    {
        WriteFile(Path.Combine(engineRoot, "Engine", "Build", "Build.version"), """{ "MajorVersion": 5, "MinorVersion": 4, "PatchVersion": 2, "BranchName": "++UE5+Release-5.4" }""");
        WriteFile(UnrealCommands.BuildScript(engineRoot, ToolOSExtensions.Current), "");
        WriteFile(UnrealCommands.EditorPath(engineRoot, "UnrealEditor", ToolOSExtensions.Current), "");
        if (code)
        {
            WriteFile(Path.Combine(folder, "Source", "NightOwlEditor.Target.cs"), "");
        }
        var uproject = Path.Combine(folder, "NightOwl.uproject");
        WriteFile(uproject, """{ "FileVersion": 3, "EngineAssociation": "", "Modules": [ { "Name": "NightOwl" }, ], }""");
        return uproject;
    }

    public static void WriteFile(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
