using Claudette.Core.Processes;
using Claudette.Core.Updates;

namespace Claudette.Platform.Updates.Mac;

/// <summary>
/// Updates <c>Claudette.app</c> from a release's <c>.dmg</c> (DESIGN.md §2, "Updating Claudette"). Preparing mounts the
/// image, copies the new app next to the running one and checks it: a valid signature from the same team, the same
/// bundle identifier, and the release's version. Installing hands over to <c>update-helper.sh</c>, which swaps the apps
/// once Claudette has quit and starts the new one.
/// </summary>
/// <param name="appBundle">The running <c>Claudette.app</c>.</param>
/// <param name="workDirectory">Where the image is mounted and the helper script written: the updates folder.</param>
/// <param name="processId">This process, which the helper waits for.</param>
public sealed class MacAppInstaller(IProcessLauncher launcher, TimeProvider timeProvider, string appBundle, string workDirectory, int processId, string launchMode = "open") : IAppInstaller
{
    public const string BundleIdentifier = "com.reapazor.claudette";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);

    public AppInstallKind Kind => AppInstallKind.MacApp;

    /// <summary>Where the new app is copied: hidden, next to the running one, so swapping them is a rename.</summary>
    public string StagedPath => Path.Combine(Path.GetDirectoryName(appBundle)!, ".Claudette-update.app");

    /// <summary>The running app's bundle, when this process runs from one: <c>…/Claudette.app/Contents/MacOS/</c>.</summary>
    public static string? FindBundle(string baseDirectory)
    {
        var macOS = new DirectoryInfo(baseDirectory.TrimEnd('/'));
        return macOS is { Name: "MacOS", Parent: { Name: "Contents", Parent: { } bundle } } && bundle.Name.EndsWith(".app", StringComparison.Ordinal)
            ? bundle.FullName
            : null;
    }

    public async Task<PreparedInstall> PrepareAsync(string packagePath, AppVersion version, CancellationToken cancellationToken = default)
    {
        if (appBundle.Contains("/AppTranslocation/", StringComparison.Ordinal))
        {
            throw new AppInstallException(
                "macOS is running Claudette from a temporary copy, which can't be updated. Move Claudette to Applications, open it from there and update again, or install this update by hand.",
                canOpenPackage: true);
        }
        var staged = StagedPath;
        try
        {
            DeleteDirectory(staged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AppInstallException($"Claudette can't write to {Path.GetDirectoryName(appBundle)}, so it can't update itself there. Install this update by hand.", canOpenPackage: true, ex);
        }

        var mount = Path.Combine(workDirectory, "mount-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(mount);
        var attach = await RunAsync("/usr/bin/hdiutil", ["attach", "-nobrowse", "-noautoopen", "-readonly", "-mountpoint", mount, packagePath], cancellationToken);
        if (attach.ExitCode != 0)
        {
            TryDeleteDirectory(mount);
            throw new AppInstallException($"The disk image couldn't be opened: {Tail(attach)}", canOpenPackage: true);
        }
        try
        {
            var source = Directory.EnumerateDirectories(mount, "*.app").FirstOrDefault()
                ?? throw new AppInstallException("The disk image has no app in it.");
            var copy = await RunAsync("/usr/bin/ditto", [source, staged], cancellationToken);
            if (copy.ExitCode != 0)
            {
                throw new AppInstallException($"The new version couldn't be copied next to this one: {Tail(copy)}", canOpenPackage: true);
            }
        }
        finally
        {
            await RunAsync("/usr/bin/hdiutil", ["detach", mount, "-quiet"], CancellationToken.None);
            TryDeleteDirectory(mount);
        }

        try
        {
            await VerifyAsync(staged, version, cancellationToken);
        }
        catch
        {
            TryDeleteDirectory(staged);
            throw;
        }
        return new PreparedInstall(version, packagePath, staged);
    }

    private async Task VerifyAsync(string staged, AppVersion version, CancellationToken cancellationToken)
    {
        // The developer this Claudette is signed by, asked first: a check that can't be made refuses the update rather
        // than letting it through. The new version must then satisfy a code requirement naming that team, with a
        // certificate Apple issued, which an ad-hoc signature or someone else's certificate doesn't.
        var mine = await TeamAsync(appBundle, cancellationToken);
        if (mine is TeamCheck.Unreadable unreadable)
        {
            throw new AppInstallException($"This Claudette's own signature couldn't be read, so the new version couldn't be checked against it: {unreadable.Reason}");
        }
        IReadOnlyList<string> verify = mine is TeamCheck.Signed signed
            ? ["--verify", "--deep", "--strict", $"-R=anchor apple generic and certificate leaf[subject.OU] = \"{signed.Team}\"", staged]
            : ["--verify", "--deep", "--strict", staged];
        var signature = await RunAsync("/usr/bin/codesign", verify, cancellationToken);
        if (signature.ExitCode != 0)
        {
            if (mine is TeamCheck.Signed own)
            {
                // Say why, when it's the developer: the requirement's failure message doesn't.
                var other = await TeamAsync(staged, cancellationToken) is TeamCheck.Signed { Team: var team } ? team : null;
                if (other != own.Team)
                {
                    throw new AppInstallException($"The new version is signed by a different developer ({other ?? "none"}) than this one ({own.Team}), so it wasn't installed.");
                }
            }
            throw new AppInstallException($"The new version's signature didn't check out, so it wasn't installed: {Tail(signature)}");
        }
        var plist = Path.Combine(staged, "Contents", "Info.plist");
        if (await PlistValueAsync(plist, "CFBundleIdentifier", cancellationToken) != BundleIdentifier)
        {
            throw new AppInstallException("The disk image holds a different app, not Claudette.");
        }
        var bundleVersion = AppVersion.TryParse(await PlistValueAsync(plist, "CFBundleShortVersionString", cancellationToken));
        if (bundleVersion != version)
        {
            throw new AppInstallException($"The app in the disk image is version {bundleVersion?.ToString() ?? "unknown"}, not {version} as the release says.");
        }
    }

    public Task<InstallOutcome> InstallAsync(PreparedInstall prepared, IReadOnlyList<string> restartArguments, string readyFile, string nonce, CancellationToken cancellationToken = default)
    {
        if (prepared.StagedPath is not { } staged || !Directory.Exists(staged))
        {
            throw new AppInstallException("The prepared update is gone. Download it again.");
        }
        try
        {
            Directory.CreateDirectory(workDirectory);
            var script = Path.Combine(workDirectory, "update-helper.sh");
            File.WriteAllText(script, HelperScript());
            launcher.Start(new ProcessStartSpec("/bin/sh",
                [script, processId.ToString(System.Globalization.CultureInfo.InvariantCulture), appBundle, staged, readyFile, nonce, launchMode, .. restartArguments])
            {
                Detached = true,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new AppInstallException($"The update couldn't be started: {ex.Message}", canOpenPackage: true, ex);
        }
        return Task.FromResult(InstallOutcome.ExitNow);
    }

    /// <summary>The helper script, from this assembly's resources.</summary>
    public static string HelperScript()
    {
        using var stream = typeof(MacAppInstaller).Assembly.GetManifestResourceStream("update-helper.sh")
            ?? throw new InvalidOperationException("update-helper.sh is missing from Claudette.Platform's resources.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>What <c>codesign</c> says about an app's signer.</summary>
    private abstract record TeamCheck
    {
        /// <summary>Signed with a Developer ID: its team identifier.</summary>
        public sealed record Signed(string Team) : TeamCheck;

        /// <summary>Not signed, or signed ad hoc (a build that wasn't released).</summary>
        public sealed record Unsigned : TeamCheck;

        /// <summary><c>codesign</c> couldn't say (it failed or timed out).</summary>
        public sealed record Unreadable(string Reason) : TeamCheck;
    }

    /// <summary>The team <c>codesign</c> reports for an app. A team identifier is ten letters and digits.</summary>
    private async Task<TeamCheck> TeamAsync(string path, CancellationToken cancellationToken)
    {
        var result = await RunAsync("/usr/bin/codesign", ["-dv", "--verbose=2", path], cancellationToken);
        // codesign writes the details to standard error.
        var text = result.StandardError + result.StandardOutput;
        if (result.ExitCode != 0)
        {
            return text.Contains("not signed at all", StringComparison.Ordinal)
                ? new TeamCheck.Unsigned()
                : new TeamCheck.Unreadable(Tail(result));
        }
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("TeamIdentifier=", StringComparison.Ordinal))
            {
                var team = line["TeamIdentifier=".Length..].Trim();
                if (team.Length == 0 || team == "not set")
                {
                    return new TeamCheck.Unsigned();
                }
                return team.Length == 10 && team.All(char.IsAsciiLetterOrDigit)
                    ? new TeamCheck.Signed(team)
                    : new TeamCheck.Unreadable($"codesign gave an unexpected team, \"{team}\".");
            }
        }
        return new TeamCheck.Unsigned();
    }

    private async Task<string?> PlistValueAsync(string plist, string key, CancellationToken cancellationToken)
    {
        var result = await RunAsync("/usr/bin/plutil", ["-extract", key, "raw", "-o", "-", plist], cancellationToken);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    private async Task<ProcessResult> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            return await ProcessRunner.RunAsync(launcher, new ProcessStartSpec(program, arguments), CommandTimeout, timeProvider, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProcessResult(-1, "", ex.Message);
        }
    }

    private static string Tail(ProcessResult result) =>
        (result.StandardError.Trim() is { Length: > 0 } error ? error : result.StandardOutput.Trim()) is { Length: > 0 } text ? text : $"exit code {result.ExitCode}";

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            DeleteDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
