using System.Text.Json.Serialization;
using Claudette.Core.Development;
using Claudette.Core.Updates;

namespace Claudette.Core.LoginItems;

/// <summary>
/// One copy of Claudette on this machine, which the login entry can start (DESIGN.md §9, "Starting at login"). A machine
/// can have several: an installed release, another build, and source builds in one or more checkouts.
/// </summary>
/// <param name="Kind">How it was installed, which decides which copy the entry prefers (<see cref="Rank"/>).</param>
/// <param name="Location">
/// The MSIX's package family name; <c>Claudette.app</c>'s bundle; or, for another build or a source build, the folder
/// with <c>Claudette.dll</c>. For a source build that's its build output, not the copy it runs from.
/// </param>
/// <param name="Version">Its version, to name it.</param>
public sealed record ClaudetteCopy(AppInstallKind Kind, string Location, string Version)
{
    public const string AssemblyName = "Claudette";

    private static StringComparison PathComparison => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Which copy the entry prefers: a release (2), then another build (1), then a source build (0).</summary>
    [JsonIgnore]
    public int Rank => Kind switch
    {
        AppInstallKind.Msix or AppInstallKind.MacApp => 2,
        AppInstallKind.Other => 1,
        _ => 0,
    };

    /// <summary>Whether this is the same copy as <paramref name="other"/>, whichever version each was when noted.</summary>
    public bool IsSameCopy(ClaudetteCopy? other) =>
        other is not null && other.Kind == Kind && string.Equals(Trim(other.Location), Trim(Location), PathComparison);

    /// <summary>
    /// Whether its files are still there. The MSIX can't be checked from its family name here: the platform does that
    /// (<see cref="ILoginItems.Exists"/>).
    /// </summary>
    public bool ExistsOnDisk() => Kind switch
    {
        AppInstallKind.Msix => false,
        AppInstallKind.MacApp => Directory.Exists(Location),
        _ => File.Exists(Path.Combine(Location, AssemblyName + ".dll")),
    };

    /// <summary>
    /// The copy in words, for the switch: "the installed Claudette 0.3.0", "Claudette 0.3.0 in C:\Tools\Claudette", "the
    /// source build in D:\Repositories\Claudette".
    /// </summary>
    public string Describe() => Kind switch
    {
        AppInstallKind.Msix or AppInstallKind.MacApp => $"the installed Claudette {Version}",
        AppInstallKind.SourceBuild => $"the source build in {SourceBuild.Detect(Location)?.RepositoryRoot ?? Location}",
        _ => $"Claudette {Version} in {Location}",
    };

    /// <summary>What sort of copy this is, in words: "installed Claudette", "source build".</summary>
    [JsonIgnore]
    public string KindName => Kind switch
    {
        AppInstallKind.Msix or AppInstallKind.MacApp => "installed Claudette",
        AppInstallKind.SourceBuild => "source build",
        _ => "build of Claudette",
    };

    private static string Trim(string location) => Path.TrimEndingDirectorySeparator(location);
}
