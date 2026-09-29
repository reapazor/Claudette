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

    /// <summary>Whether the clipboard has text, which a paste prefers over an image of the same thing.</summary>
    Task<bool> ClipboardHasTextAsync();

    /// <summary>An image on the clipboard, such as a screenshot, as PNG bytes; null when there's none.</summary>
    Task<byte[]?> GetClipboardImageAsync();

    /// <summary>Opens a file in the app the OS uses for it, for example the user's editor (DESIGN.md §8).</summary>
    Task OpenFileAsync(string path);
}

/// <summary>Runs work on the UI thread.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}
