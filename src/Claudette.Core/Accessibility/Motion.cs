using System.Text.Json;
using Claudette.Core.Json;
using Claudette.Core.Protocol;
using Claudette.Core.Settings;

namespace Claudette.Core.Accessibility;

/// <summary>Whether the operating system asks apps to reduce motion (DESIGN.md §3, "Accessibility").</summary>
public interface ISystemMotion
{
    /// <summary>True when the OS asks for less motion; false when it doesn't, or can't say.</summary>
    Task<bool> PrefersReducedMotionAsync(CancellationToken cancellationToken = default);
}

/// <summary>For an OS Claudette can't ask, and for tests: no preference.</summary>
public sealed class NoSystemMotion : ISystemMotion
{
    public Task<bool> PrefersReducedMotionAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
}

/// <summary>
/// Whether Claudette reduces motion (DESIGN.md §3, "Accessibility"): the pulsing busy dots, the working line's
/// twinkling glyph, and the animated taskbar and Dock icons.
/// </summary>
public static class Motion
{
    /// <summary>
    /// Settings → Appearance → Motion decides; following the system, motion is reduced when the OS asks for it, or when
    /// Claude Code's own <c>prefersReducedMotion</c> setting does, so a choice made for the terminal holds here too.
    /// </summary>
    public static bool Reduce(MotionSetting setting, bool systemPrefersReduced, bool claudeCodePrefersReduced) => setting switch
    {
        MotionSetting.Reduce => true,
        MotionSetting.Full => false,
        _ => systemPrefersReduced || claudeCodePrefersReduced,
    };

    /// <summary>
    /// Claude Code's documented <c>prefersReducedMotion</c>, from the user's settings file in its config folder. A file
    /// that's missing or unreadable, or a value that isn't true, is no preference.
    /// </summary>
    public static bool ClaudeCodePrefersReduced(string? configDirectory)
    {
        if (configDirectory is null)
        {
            return false;
        }
        try
        {
            var path = Path.Combine(configDirectory, "settings.json");
            return File.Exists(path) && JsonTree.ParseObject(File.ReadAllText(path))?.GetBool("prefersReducedMotion") == true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}
