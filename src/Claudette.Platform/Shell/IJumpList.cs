namespace Claudette.Platform.Shell;

/// <summary>A folder offered in the Windows jump list or macOS's Open Recent (DESIGN.md §4, "Other ways in").</summary>
public sealed record RecentFolderEntry(string Label, string Path);

/// <summary>The recent folders in the Windows taskbar jump list. Choosing one launches Claudette with <c>--folder</c>.</summary>
public interface IJumpList
{
    /// <summary>Replaces the list. Call it on the UI thread.</summary>
    void Update(IReadOnlyList<RecentFolderEntry> folders);
}
