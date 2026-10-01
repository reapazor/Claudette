using System.Collections.ObjectModel;
using Claudette.App.Services;
using Claudette.Core.Git;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>One folder in the new-tab picker.</summary>
public sealed class FolderEntry(string path, string? branch, DateTimeOffset? lastUsed, int openTabs, bool isFavorite, int number, TimeProvider time)
{
    public string Path { get; } = path;

    public string Name => System.IO.Path.GetFileName(Path) is { Length: > 0 } name ? name : Path;

    /// <summary>The path with the home folder shortened to <c>~</c>.</summary>
    public string ShortPath
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.StartsWith(home, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                ? "~" + Path[home.Length..]
                : Path;
        }
    }

    public string? Branch { get; } = branch;

    public bool HasBranch => Branch is not null;

    public bool IsFavorite { get; } = isFavorite;

    public string FavoriteMenuText => IsFavorite ? "Remove from favorites" : "Add to favorites";

    public bool Exists { get; } = Directory.Exists(path);

    public int OpenTabs { get; } = openTabs;

    /// <summary>1–9 for the first nine entries, which those keys pick.</summary>
    public string NumberText { get; } = number is >= 1 and <= 9 ? number.ToString() : "";

    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (!Exists)
            {
                parts.Add("not found");
            }
            if (lastUsed is { } used)
            {
                parts.Add(Ago(time.GetUtcNow() - used));
            }
            if (OpenTabs > 0)
            {
                parts.Add($"{OpenTabs} open");
            }
            return string.Join(" · ", parts);
        }
    }

    private static string Ago(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalHours: < 1 } => $"{(int)span.TotalMinutes} min ago",
        { TotalDays: < 1 } => $"{(int)span.TotalHours} h ago",
        { TotalDays: < 2 } => "yesterday",
        _ => $"{(int)span.TotalDays} days ago",
    };
}

/// <summary>The picker behind Ctrl/Cmd+T and the sidebar's <b>New tab</b> (DESIGN.md §4, "Opening a tab").</summary>
public sealed partial class NewTabPickerViewModel : ViewModelBase
{
    private readonly AppServices _services;

    public ShortcutTips Tips => _services.Tips;
    private readonly ShellViewModel _shell;

    public NewTabPickerViewModel(AppServices services, ShellViewModel shell)
    {
        _services = services;
        _shell = shell;
        Refresh();
    }

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    partial void OnSearchChanged(string value) => Refresh();

    public ObservableCollection<FolderEntry> Favorites { get; } = [];

    public ObservableCollection<FolderEntry> Recents { get; } = [];

    public bool HasFavorites => Favorites.Count > 0;

    public bool HasRecents => Recents.Count > 0;

    public bool IsEmpty => !HasFavorites && !HasRecents;

    /// <summary>Favorites first, then recents, in display order (what the number keys pick from).</summary>
    public IReadOnlyList<FolderEntry> All => [.. Favorites, .. Recents];

    [ObservableProperty]
    public partial FolderEntry? Selected { get; set; }

    [RelayCommand]
    private async Task OpenAsync(FolderEntry? entry)
    {
        if (entry is { Exists: true })
        {
            _shell.ClosePicker();
            await _shell.OpenFolderAsync(entry.Path);
        }
    }

    public Task OpenNumberAsync(int number) =>
        number >= 1 && number <= All.Count ? OpenAsync(All[number - 1]) : Task.CompletedTask;

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await _services.Platform.PickFolderAsync("Choose a working folder") is { } folder)
        {
            _shell.ClosePicker();
            await _shell.OpenFolderAsync(folder);
        }
    }

    [RelayCommand]
    private void Close() => _shell.ClosePicker();

    /// <summary>"Open from History…" (DESIGN.md §4, "Opening a tab").</summary>
    [RelayCommand]
    private void OpenHistory() => _shell.OpenHistoryCommand.Execute(null);

    [RelayCommand]
    private void ToggleFavorite(FolderEntry? entry)
    {
        if (entry is null)
        {
            return;
        }
        FolderHistory.SetFavorite(_services.State, entry.Path, !entry.IsFavorite);
        _services.SaveState();
        Refresh();
    }

    [RelayCommand]
    private void RemoveRecent(FolderEntry? entry)
    {
        if (entry is null)
        {
            return;
        }
        FolderHistory.Remove(_services.State, entry.Path);
        _services.SaveState();
        Refresh();
    }

    [RelayCommand]
    private Task CopyPathAsync(FolderEntry? entry) =>
        entry is null ? Task.CompletedTask : _services.Platform.SetClipboardTextAsync(entry.Path);

    [RelayCommand]
    private Task RevealAsync(FolderEntry? entry) =>
        entry is { Exists: true } ? _services.Platform.RevealFolderAsync(entry.Path) : Task.CompletedTask;

    private void Refresh()
    {
        var state = _services.State;
        var search = Search.Trim();
        bool Matches(string path) => search.Length == 0 || path.Contains(search, StringComparison.OrdinalIgnoreCase);
        int OpenTabs(string path) => _shell.AllTabs.Count(t => FolderHistory.SamePath(t.GroupFolder, path));
        DateTimeOffset? LastUsed(string path) => state.RecentFolders.FirstOrDefault(r => FolderHistory.SamePath(r.Path, path))?.LastUsed;

        var number = 1;
        Favorites.Clear();
        foreach (var path in state.FavoriteFolders.Where(Matches))
        {
            Favorites.Add(new FolderEntry(path, GitInfo.TryGetBranch(path), LastUsed(path), OpenTabs(path), isFavorite: true, number++, _services.Time));
        }
        Recents.Clear();
        foreach (var recent in state.RecentFolders.Where(r => !FolderHistory.IsFavorite(state, r.Path) && Matches(r.Path)))
        {
            Recents.Add(new FolderEntry(recent.Path, GitInfo.TryGetBranch(recent.Path), recent.LastUsed, OpenTabs(recent.Path), isFavorite: false, number++, _services.Time));
        }
        Selected = All.FirstOrDefault(e => e.Exists);
        OnPropertyChanged(nameof(HasFavorites));
        OnPropertyChanged(nameof(HasRecents));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(All));
    }
}
