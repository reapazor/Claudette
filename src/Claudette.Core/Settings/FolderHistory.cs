namespace Claudette.Core.Settings;

/// <summary>Recent and favorite folders for the new-tab picker (DESIGN.md §4, "Opening a tab").</summary>
public static class FolderHistory
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static bool SamePath(string a, string b) => PathComparer.Equals(Normalize(a), Normalize(b));

    /// <summary>Moves (or adds) a folder to the top of the recent list and trims it. Favorites don't count toward the limit.</summary>
    public static void Touch(AppState state, string folder, DateTimeOffset now, int limit)
    {
        var path = Normalize(folder);
        state.RecentFolders.RemoveAll(r => SamePath(r.Path, path));
        state.RecentFolders.Insert(0, new RecentFolder { Path = path, LastUsed = now });
        var kept = 0;
        state.RecentFolders.RemoveAll(r => !IsFavorite(state, r.Path) && ++kept > limit);
    }

    public static void Remove(AppState state, string folder) =>
        state.RecentFolders.RemoveAll(r => SamePath(r.Path, folder));

    public static bool IsFavorite(AppState state, string folder) =>
        state.FavoriteFolders.Any(f => SamePath(f, folder));

    public static void SetFavorite(AppState state, string folder, bool favorite)
    {
        state.FavoriteFolders.RemoveAll(f => SamePath(f, folder));
        if (favorite)
        {
            state.FavoriteFolders.Add(Normalize(folder));
        }
    }

    public static string Normalize(string folder) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
}
