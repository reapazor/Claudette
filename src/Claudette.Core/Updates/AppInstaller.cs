using System.Runtime.InteropServices;

namespace Claudette.Core.Updates;

/// <summary>How this copy of Claudette was installed, which decides how it updates (DESIGN.md §2, "Updating Claudette").</summary>
public enum AppInstallKind
{
    /// <summary>A folder Claudette can't replace itself: a Linux build, or an unpackaged Windows one. The update is offered as a link.</summary>
    Other,

    /// <summary>Run from a checkout's build output. New builds come from the checkout (DESIGN.md §9, "Working on Claudette").</summary>
    SourceBuild,

    /// <summary>Installed from the MSIX package on Windows.</summary>
    Msix,

    /// <summary><c>Claudette.app</c> on macOS, from the <c>.dmg</c>.</summary>
    MacApp,
}

/// <summary>A downloaded package, checked and ready to install.</summary>
/// <param name="PackagePath">The downloaded file.</param>
/// <param name="StagedPath">What the installer prepared from it, such as the new <c>Claudette.app</c> next to the old one.</param>
public sealed record PreparedInstall(AppVersion Version, string PackagePath, string? StagedPath = null);

/// <summary>What the caller does once the install has started.</summary>
public enum InstallOutcome
{
    /// <summary>Close now: something outside Claudette finishes the install and starts the new version.</summary>
    ExitNow,

    /// <summary>The new version is starting: wait for it to say it's up, then close.</summary>
    WaitForNewVersion,
}

/// <summary>Why an update couldn't be installed, in words to show.</summary>
/// <param name="CanOpenPackage">The downloaded package can still be installed by hand: opening it starts the OS's installer.</param>
public sealed class AppInstallException(string message, bool canOpenPackage = false, Exception? inner = null) : Exception(message, inner)
{
    public bool CanOpenPackage { get; } = canOpenPackage;
}

/// <summary>
/// Installs a downloaded release over this copy of Claudette, the way its platform installs apps: an MSIX update on
/// Windows, swapping <c>Claudette.app</c> on macOS. Implementations live in Claudette.Platform.
/// </summary>
public interface IAppInstaller
{
    AppInstallKind Kind { get; }

    /// <summary>Whether <see cref="PrepareAsync"/> and <see cref="InstallAsync"/> can run here.</summary>
    bool CanInstall => Kind is AppInstallKind.Msix or AppInstallKind.MacApp;

    /// <summary>
    /// Checks the downloaded package (that it's Claudette, the expected version, and signed as this copy is) and gets it
    /// ready, without touching the running app. Throws <see cref="AppInstallException"/>.
    /// </summary>
    Task<PreparedInstall> PrepareAsync(string packagePath, AppVersion version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs the update and has the new version started with <paramref name="restartArguments"/>. The tabs are
    /// already closed. Throws <see cref="AppInstallException"/> if nothing was changed.
    /// </summary>
    /// <param name="readyFile">Where the new version writes <paramref name="nonce"/> once it's up.</param>
    Task<InstallOutcome> InstallAsync(PreparedInstall prepared, IReadOnlyList<string> restartArguments, string readyFile, string nonce, CancellationToken cancellationToken = default);
}

/// <summary>For builds that can't install updates: Linux, unpackaged Windows builds, and source builds.</summary>
public sealed class NoAppInstaller(AppInstallKind kind = AppInstallKind.Other) : IAppInstaller
{
    public AppInstallKind Kind { get; } = kind;

    public Task<PreparedInstall> PrepareAsync(string packagePath, AppVersion version, CancellationToken cancellationToken = default) =>
        throw new AppInstallException("This copy of Claudette can't install updates itself.");

    public Task<InstallOutcome> InstallAsync(PreparedInstall prepared, IReadOnlyList<string> restartArguments, string readyFile, string nonce, CancellationToken cancellationToken = default) =>
        throw new AppInstallException("This copy of Claudette can't install updates itself.");
}

/// <summary>A release newer than the running version, and the package in it for this platform, if there is one.</summary>
public sealed record AvailableUpdate(AppRelease Release, ReleaseAsset? Asset)
{
    public AppVersion Version => Release.Version;

    /// <summary>
    /// The newest release above <paramref name="current"/>. Pre-releases count only when asked for. A release without a
    /// package for this platform is still returned, so the user hears about it.
    /// </summary>
    public static AvailableUpdate? Find(IEnumerable<AppRelease> releases, AppVersion current, bool includePrereleases, AppInstallKind kind, Architecture architecture)
    {
        var newest = releases
            .Where(r => r.Version > current && (includePrereleases || !r.IsPrerelease))
            .OrderByDescending(r => r.Version)
            .FirstOrDefault();
        return newest is null ? null : new AvailableUpdate(newest, PickAsset(newest, kind, architecture));
    }

    /// <summary>
    /// The package for this install: <c>Claudette-&lt;version&gt;-&lt;arch&gt;.msix</c> or <c>….dmg</c>, as
    /// <c>.github/workflows/package.yml</c> names them. Null when there's none, or this install can't use one.
    /// </summary>
    public static ReleaseAsset? PickAsset(AppRelease release, AppInstallKind kind, Architecture architecture)
    {
        var extension = kind switch
        {
            AppInstallKind.Msix => ".msix",
            AppInstallKind.MacApp => ".dmg",
            _ => null,
        };
        var arch = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        if (extension is null || arch is null)
        {
            return null;
        }
        return release.Assets.FirstOrDefault(a => a.Name.EndsWith($"-{arch}{extension}", StringComparison.OrdinalIgnoreCase));
    }
}
