using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Claudette.App.Services;

public sealed class AvaloniaPlatformServices(Func<TopLevel?> topLevel) : IPlatformServices
{
    public async Task<string?> PickFolderAsync(string title)
    {
        if (topLevel() is not { } top)
        {
            return null;
        }
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFileAsync(string title)
    {
        if (topLevel() is not { } top)
        {
            return null;
        }
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task OpenUrlAsync(string url)
    {
        if (topLevel() is { } top && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            await top.Launcher.LaunchUriAsync(uri);
        }
    }

    public async Task RevealFolderAsync(string path)
    {
        if (topLevel() is { } top)
        {
            await top.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
        }
    }

    public async Task OpenFileAsync(string path)
    {
        if (topLevel() is { } top && File.Exists(path))
        {
            await top.Launcher.LaunchFileInfoAsync(new FileInfo(path));
        }
    }

    /// <summary>A folder opens as the OS opens it: a macOS package such as an <c>.xcworkspace</c> opens in its app.</summary>
    public async Task OpenPathAsync(string path)
    {
        if (topLevel() is not { } top)
        {
            return;
        }
        if (Directory.Exists(path))
        {
            await top.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
        }
        else if (File.Exists(path))
        {
            await top.Launcher.LaunchFileInfoAsync(new FileInfo(path));
        }
    }

    public IReadOnlyList<string> InstalledFonts()
    {
        try
        {
            return [.. FontManager.Current.SystemFonts.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception)
        {
            return [];
        }
    }

    public async Task SetClipboardTextAsync(string text)
    {
        if (topLevel()?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    public async Task<IReadOnlyList<string>> GetClipboardFilesAsync()
    {
        if (topLevel()?.Clipboard is not { } clipboard || await clipboard.TryGetFilesAsync() is not { } items)
        {
            return [];
        }
        return items.Select(item => item.TryGetLocalPath()).OfType<string>().ToArray();
    }

    public async Task<string?> GetClipboardTextAsync() =>
        topLevel()?.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;

    /// <remarks>
    /// Avalonia 12 finds the image the way each OS puts one on the clipboard and gives it as a bitmap, which is sent as
    /// PNG, or JPEG when the PNG is too big (DESIGN.md §5, "Attachments").
    /// <list type="bullet">
    /// <item>Windows: <c>image/png</c> or <c>PNG</c>, then <c>CF_DIB</c>, <c>CF_DIBV5</c> or <c>CF_BITMAP</c>, so a
    /// Snipping Tool screenshot works.</item>
    /// <item>macOS: <c>public.png</c>, with <c>public.tiff</c> or <c>public.jpeg</c> converted to PNG when that's all
    /// there is.</item>
    /// <item>Linux (X11): <c>image/png</c> or <c>image/jpeg</c>.</item>
    /// </list>
    /// </remarks>
    public async Task<byte[]?> GetClipboardImageAsync()
    {
        if (topLevel()?.Clipboard is not { } clipboard || await clipboard.TryGetBitmapAsync() is not { } bitmap)
        {
            return null;
        }
        using (bitmap)
        {
            return ImageFiles.Encode(bitmap);
        }
    }
}

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
