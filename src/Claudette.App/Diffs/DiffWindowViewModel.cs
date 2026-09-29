using Claudette.App.ViewModels;
using Claudette.Core.Diffs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.Diffs;

/// <summary>One side of a diff line: its number, text and syntax colors.</summary>
public sealed record DiffCell(string Number, string Text, IReadOnlyList<ColoredRun>? Runs, DiffOp Op)
{
    public bool IsAdded => Op == DiffOp.Added;

    public bool IsRemoved => Op == DiffOp.Removed;
}

/// <summary>A row of the inline diff.</summary>
public sealed record InlineDiffRow(string OldNumber, string NewNumber, string Marker, string Text, IReadOnlyList<ColoredRun>? Runs, DiffOp Op, bool IsHeader)
{
    public bool IsAdded => Op == DiffOp.Added && !IsHeader;

    public bool IsRemoved => Op == DiffOp.Removed && !IsHeader;
}

/// <summary>A row of the side-by-side diff; an empty cell is a line only the other side has.</summary>
public sealed record SideBySideDiffRow(DiffCell? Left, DiffCell? Right, bool IsHeader, string HeaderText)
{
    public bool HasLeft => Left is not null;

    public bool HasRight => Right is not null;
}

/// <summary>What the diff view compares, and what it can do with the file.</summary>
public sealed record DiffSource(
    string Path,
    string DisplayPath,
    string? Before,
    string BeforeLabel,
    Func<Task>? OpenInDiffTool,
    Func<Task> OpenInEditor,
    Func<Task> Reveal,
    Func<Task> CopyPath);

/// <summary>
/// The built-in diff view (DESIGN.md §8): the file before Claude's first change (or at HEAD) against the file now,
/// inline or side by side, with syntax highlighting.
/// </summary>
public sealed partial class DiffWindowViewModel : ViewModelBase
{
    private readonly DiffSource _source;
    private readonly bool _dark;
    private string? _after;
    private IReadOnlyList<IReadOnlyList<ColoredRun>>? _beforeColors;
    private IReadOnlyList<IReadOnlyList<ColoredRun>>? _afterColors;

    public DiffWindowViewModel(DiffSource source, bool dark)
    {
        _source = source;
        _dark = dark;
        _ = LoadAsync();
    }

    public string Title => Path.GetFileName(_source.Path);

    public string DisplayPath => _source.DisplayPath;

    public string BeforeLabel => _source.BeforeLabel;

    public bool CanOpenInDiffTool => _source.OpenInDiffTool is not null;

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial string Stats { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInline))]
    public partial bool IsSideBySide { get; set; }

    public bool IsInline => !IsSideBySide;

    partial void OnIsSideBySideChanged(bool value) => Build();

    /// <summary>The whole file rather than just the changes with a few lines around them.</summary>
    [ObservableProperty]
    public partial bool ShowWholeFile { get; set; }

    partial void OnShowWholeFileChanged(bool value) => Build();

    [ObservableProperty]
    public partial IReadOnlyList<InlineDiffRow> InlineRows { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<SideBySideDiffRow> SideRows { get; set; } = [];

    [RelayCommand]
    private Task OpenInDiffToolAsync() => _source.OpenInDiffTool?.Invoke() ?? Task.CompletedTask;

    [RelayCommand]
    private Task OpenInEditorAsync() => _source.OpenInEditor();

    [RelayCommand]
    private Task RevealAsync() => _source.Reveal();

    [RelayCommand]
    private Task CopyPathAsync() => _source.CopyPath();

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        IsLoading = true;
        var before = _source.Before;
        var path = _source.Path;
        var dark = _dark;
        var (after, beforeColors, afterColors, message) = await Task.Run(() =>
        {
            string? current = null;
            try
            {
                current = File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (IOException)
            {
            }
            if ((before is not null && LineDiff.LooksBinary(before)) || (current is not null && LineDiff.LooksBinary(current)))
            {
                return (current, null, null, "This is a binary file, so there's nothing to show line by line.");
            }
            var name = Path.GetFileName(path);
            return (current,
                before is null ? null : SyntaxHighlighter.Highlight(LineDiff.SplitLines(before), name, dark),
                current is null ? null : SyntaxHighlighter.Highlight(LineDiff.SplitLines(current), name, dark),
                current is null ? "The file has been deleted." : null);
        });
        _after = after;
        _beforeColors = beforeColors;
        _afterColors = afterColors;
        Message = message;
        var (added, removed) = LineDiff.Count(before, after);
        Stats = $"+{added} −{removed}";
        IsLoading = false;
        Build();
    }

    private void Build()
    {
        if (IsLoading)
        {
            return;
        }
        var before = _source.Before;
        var lines = new List<(DiffLineEntry? Line, string? Header)>();
        if (ShowWholeFile)
        {
            lines.AddRange(LineDiff.Full(before, _after).Select(l => ((DiffLineEntry?)l, (string?)null)));
        }
        else
        {
            foreach (var hunk in LineDiff.Hunks(before, _after))
            {
                lines.Add((null, hunk.Header));
                lines.AddRange(hunk.Lines.Select(l => ((DiffLineEntry?)l, (string?)null)));
            }
        }

        if (IsSideBySide)
        {
            var rows = new List<SideBySideDiffRow>();
            var run = new List<DiffLineEntry>();
            foreach (var (line, header) in lines)
            {
                if (line is not null)
                {
                    run.Add(line);
                    continue;
                }
                Flush();
                rows.Add(new SideBySideDiffRow(null, null, true, header!));
            }
            Flush();
            SideRows = rows;

            void Flush()
            {
                foreach (var pair in LineDiff.SideBySide(run))
                {
                    rows.Add(new SideBySideDiffRow(Cell(pair.Left, left: true), Cell(pair.Right, left: false), false, ""));
                }
                run.Clear();
            }
        }
        else
        {
            InlineRows = lines.Select(item => item.Line is { } l
                ? new InlineDiffRow(l.OldNumber?.ToString() ?? "", l.NewNumber?.ToString() ?? "",
                    l.Op switch { DiffOp.Added => "+", DiffOp.Removed => "−", _ => " " }, l.Text, Colors(l), l.Op, false)
                : new InlineDiffRow("", "", "", item.Header!, null, DiffOp.Context, true)).ToArray();
        }
    }

    private DiffCell? Cell(DiffLineEntry? line, bool left)
    {
        if (line is null)
        {
            return null;
        }
        var number = left ? line.OldNumber : line.NewNumber;
        var colors = left ? _beforeColors : _afterColors;
        return new DiffCell(number?.ToString() ?? "", line.Text, number is { } n && colors is not null && n - 1 < colors.Count ? colors[n - 1] : null, line.Op);
    }

    /// <summary>Removed lines take the old file's colors; added and unchanged lines the new file's.</summary>
    private IReadOnlyList<ColoredRun>? Colors(DiffLineEntry line) => line.Op == DiffOp.Removed
        ? line.OldNumber is { } old && _beforeColors is { } before && old - 1 < before.Count ? before[old - 1] : null
        : line.NewNumber is { } now && _afterColors is { } after && now - 1 < after.Count ? after[now - 1] : null;
}
