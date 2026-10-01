using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>One thing the command palette can do: a command, a tab to go to, a folder to open.</summary>
/// <param name="Group">What kind it is, shown beside it: "Command", "Tab", "Folder" or "Settings".</param>
/// <param name="Shortcut">Its keyboard shortcut as this OS shows it, if it has one.</param>
public sealed record PaletteEntry(string Label, string Group, Func<Task> Run, string? Shortcut = null, string? Detail = null)
{
    public bool HasShortcut => !string.IsNullOrEmpty(Shortcut);

    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}

/// <summary>
/// The command palette (DESIGN.md §4, "Command palette"): everything it can do, filtered as the user types by the
/// letters of each label in order, best matches first. Up, Down and Enter choose; Escape closes.
/// </summary>
public sealed partial class CommandPaletteViewModel : ViewModelBase
{
    private readonly IReadOnlyList<PaletteEntry> _entries;
    private readonly Action _close;

    /// <param name="entries">Everything, in the order shown before anything is typed.</param>
    /// <param name="close">Closes the palette, after running an entry or on Escape.</param>
    public CommandPaletteViewModel(IReadOnlyList<PaletteEntry> entries, Action close)
    {
        _entries = entries;
        _close = close;
        Filter();
    }

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    partial void OnQueryChanged(string value) => Filter();

    public ObservableCollection<PaletteEntry> Results { get; } = [];

    [ObservableProperty]
    public partial PaletteEntry? Selected { get; set; }

    public bool HasResults => Results.Count > 0;

    [RelayCommand]
    private void MoveDown() => Move(1);

    [RelayCommand]
    private void MoveUp() => Move(-1);

    private void Move(int by)
    {
        if (Results.Count == 0)
        {
            return;
        }
        var at = Selected is null ? -1 : Results.IndexOf(Selected);
        Selected = Results[(at + by + Results.Count) % Results.Count];
    }

    /// <summary>Runs the chosen entry (or the one clicked), after closing the palette so it acts on the window.</summary>
    [RelayCommand]
    private async Task RunAsync(PaletteEntry? entry)
    {
        if ((entry ?? Selected) is not { } chosen)
        {
            return;
        }
        _close();
        await chosen.Run();
    }

    [RelayCommand]
    private void Close() => _close();

    private void Filter()
    {
        var query = Query.Trim();
        var matches = query.Length == 0
            ? _entries
            : _entries
                .Select((entry, order) => (Entry: entry, Order: order, Score: Score(entry.Label, query) ?? Score($"{entry.Group} {entry.Label}", query) - 50))
                .Where(m => m.Score is not null)
                .OrderByDescending(m => m.Score)
                .ThenBy(m => m.Order)
                .Select(m => m.Entry)
                .ToArray();
        Results.Clear();
        foreach (var entry in matches)
        {
            Results.Add(entry);
        }
        Selected = Results.FirstOrDefault();
        OnPropertyChanged(nameof(HasResults));
    }

    /// <summary>
    /// How well <paramref name="query"/> matches <paramref name="label"/>: its letters must all appear in order, ignoring
    /// case. A match at the start, at the start of words and in a run scores higher. Null when it doesn't match.
    /// </summary>
    internal static int? Score(string label, string query)
    {
        var score = 0;
        var at = 0;
        var previous = -2;
        foreach (var c in query)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }
            var found = label.IndexOf(c.ToString(), at, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return null;
            }
            score += 1;
            if (found == 0)
            {
                score += 8;
            }
            else if (!char.IsLetterOrDigit(label[found - 1]))
            {
                score += 5;
            }
            if (found == previous + 1)
            {
                score += 3;
            }
            score -= Math.Min(found - at, 5);
            previous = found;
            at = found + 1;
        }
        return score;
    }
}
