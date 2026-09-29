using Avalonia.Controls;
using Avalonia.Input.Platform;
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

    public async Task SetClipboardTextAsync(string text)
    {
        if (topLevel()?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
