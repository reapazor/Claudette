namespace Claudette.App.Services;

/// <summary>OS dialogs, the browser and the clipboard, behind an interface so view models stay testable.</summary>
public interface IPlatformServices
{
    Task<string?> PickFolderAsync(string title);

    Task<string?> PickFileAsync(string title);

    Task OpenUrlAsync(string url);

    /// <summary>Shows a folder in Finder or Explorer.</summary>
    Task RevealFolderAsync(string path);

    Task SetClipboardTextAsync(string text);

    /// <summary>Files copied in Finder or Explorer, as local paths, for pasting into the composer (DESIGN.md §5).</summary>
    Task<IReadOnlyList<string>> GetClipboardFilesAsync();

    /// <summary>The clipboard's text, which a paste prefers over an image of the same thing; null when there's none.</summary>
    Task<string?> GetClipboardTextAsync();

    /// <summary>An image on the clipboard, such as a screenshot, as PNG bytes (JPEG when that's too big); null when there's none.</summary>
    Task<byte[]?> GetClipboardImageAsync();

    /// <summary>Opens a file in the app the OS uses for it, for example the user's editor (DESIGN.md §8).</summary>
    Task OpenFileAsync(string path);

    /// <summary>
    /// Opens a file, or a folder the OS treats as a document (a macOS <c>.xcworkspace</c>), in the app the OS uses for
    /// it (DESIGN.md §18, "Project tools").
    /// </summary>
    Task OpenPathAsync(string path) => Directory.Exists(path) ? RevealFolderAsync(path) : OpenFileAsync(path);

    /// <summary>The font families installed on this machine, for Settings → Appearance. Empty when unknown.</summary>
    IReadOnlyList<string> InstalledFonts() => [];

    /// <summary>
    /// The OS's save dialog, offering <paramref name="types"/> (the first is the default) and starting from
    /// <paramref name="suggestedName"/>. Returns the chosen path, or null for Cancel.
    /// </summary>
    Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<SaveFileType> types);
}

/// <summary>A kind of file the save dialog offers, such as Markdown (<c>md</c>).</summary>
public sealed record SaveFileType(string Name, string Extension);

/// <summary>Runs work on the UI thread.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}
