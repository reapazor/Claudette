namespace Claudette.Core.Library;

/// <summary>
/// Recognizes folders kept in sync by a cloud storage client, so Claudette can say so before putting transcripts
/// there (DESIGN.md §9, "Privacy"). A heuristic over folder names; a false positive only costs a confirmation.
/// </summary>
internal static class CloudSyncFolders
{
    private static readonly string[] OneDriveVariables = ["OneDrive", "OneDriveConsumer", "OneDriveCommercial"];

    public static bool IsInCloudSyncFolder(string path, string homeFolder, Func<string, string?> getEnvironmentVariable, out string? provider)
    {
        provider = Detect(path, homeFolder, getEnvironmentVariable);
        return provider is not null;
    }

    private static string? Detect(string path, string homeFolder, Func<string, string?> getEnvironmentVariable)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var name in OneDriveVariables)
        {
            if (getEnvironmentVariable(name) is { Length: > 0 } root && IsUnder(path, root))
            {
                return "OneDrive";
            }
        }

        var segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < segments.Length; i++)
        {
            // macOS: every File Provider client (Google Drive, Dropbox, OneDrive, Box, ...) lives under here.
            if (Is(segments[i], "Library") && Is(segments[i + 1], "CloudStorage") && i + 2 < segments.Length)
            {
                return FromCloudStorageName(segments[i + 2]);
            }
            if (Is(segments[i], "Library") && Is(segments[i + 1], "Mobile Documents"))
            {
                return "iCloud Drive";
            }
        }

        foreach (var segment in segments)
        {
            if (Is(segment, "My Drive") || Is(segment, "Google Drive") || segment.StartsWith("GoogleDrive", StringComparison.OrdinalIgnoreCase))
            {
                return "Google Drive";
            }
            if (segment.StartsWith("Dropbox", StringComparison.OrdinalIgnoreCase))
            {
                return "Dropbox";
            }
            if (segment.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase))
            {
                return "OneDrive";
            }
            if (Is(segment, "iCloudDrive") || Is(segment, "iCloud Drive"))
            {
                return "iCloud Drive";
            }
        }

        // "Box" is a common word, so only Box Drive's own folders in the home folder count.
        if (homeFolder.Length > 0 && (IsUnder(path, Path.Combine(homeFolder, "Box")) || IsUnder(path, Path.Combine(homeFolder, "Box Sync"))))
        {
            return "Box";
        }
        return null;
    }

    private static string FromCloudStorageName(string name)
    {
        if (name.StartsWith("GoogleDrive", StringComparison.OrdinalIgnoreCase))
        {
            return "Google Drive";
        }
        foreach (var known in (string[])["Dropbox", "OneDrive", "Box", "iCloud"])
        {
            if (name.StartsWith(known, StringComparison.OrdinalIgnoreCase))
            {
                return known == "iCloud" ? "iCloud Drive" : known;
            }
        }
        var dash = name.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 ? name[..dash] : name;
    }

    private static bool Is(string segment, string name) => segment.Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Path prefix test that works for paths from any OS, without touching the disk.</summary>
    private static bool IsUnder(string path, string root)
    {
        var p = path.Replace('\\', '/').TrimEnd('/');
        var r = root.Replace('\\', '/').TrimEnd('/');
        return r.Length > 0
            && (p.Equals(r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase));
    }
}
