using System.Runtime.Versioning;
using System.Security;
using Claudette.Core.ProjectTools.Unreal;
using Microsoft.Win32;

namespace Claudette.Platform.ProjectTools;

/// <summary>
/// Unreal's registry entries on Windows (DESIGN.md §18, "Finding the engine"): launcher installs under
/// <c>HKLM\SOFTWARE\EpicGames\Unreal Engine\&lt;version&gt;</c> (read in both the 64-bit and 32-bit views), and source
/// builds registered by UnrealVersionSelector under <c>HKCU\SOFTWARE\Epic Games\Unreal Engine\Builds</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUnrealEngineRegistry : IUnrealEngineRegistry
{
    public string? FindLauncherInstall(string version)
    {
        foreach (var view in (RegistryView[])[RegistryView.Registry64, RegistryView.Registry32])
        {
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = machine.OpenSubKey($@"SOFTWARE\EpicGames\Unreal Engine\{version}");
                if (key?.GetValue("InstalledDirectory") is string { Length: > 0 } folder)
                {
                    return folder;
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                // Not readable: try the other view.
            }
        }
        return null;
    }

    public IReadOnlyDictionary<string, string> SourceBuilds()
    {
        var builds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Epic Games\Unreal Engine\Builds");
            foreach (var name in key?.GetValueNames() ?? [])
            {
                if (key!.GetValue(name) is string { Length: > 0 } folder)
                {
                    builds.TryAdd(name, folder);
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // None readable.
        }
        return builds;
    }
}

/// <summary>The Unreal registry for the OS Claudette runs on: the real one on Windows, none elsewhere.</summary>
public static class UnrealEngineRegistries
{
    public static IUnrealEngineRegistry CreateForCurrentOS() =>
        OperatingSystem.IsWindows() ? new WindowsUnrealEngineRegistry() : NoUnrealEngineRegistry.Instance;
}
