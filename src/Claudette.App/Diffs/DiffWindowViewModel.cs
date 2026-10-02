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
/// <param name="Hunk">On a hunk's header row, the hunk, which <b>Revert</b> undoes (DESIGN.md §8, "Reverting").</param>
public sealed record InlineDiffRow(string OldNumber, string NewNumber, string Marker, string Text, IReadOnlyList<ColoredRun>? Runs, DiffOp Op, bool IsHeader, DiffHunk? Hunk = null)
{
    public bool CanRevert => Hunk is not null;

    public bool IsAdded => Op == DiffOp.Added && !IsHeader;

    public bool IsRemoved => Op == DiffOp.Removed && !IsHeader;
}

/// <summary>A row of the side-by-side diff; an empty cell is a line only the other side has.</summary>
public sealed record SideBySideDiffRow(DiffCell? Left, DiffCell? Right, bool IsHeader, string HeaderText, DiffHunk? Hunk = null)
{
    public bool CanRevert => Hunk is not null;

    public bool HasLeft => Left is not null;

    public bool HasRight => Right is not null;
}

/// <summary>What the diff view compares, and what it can do with the file.</summary>
/// <param name="BeforeKnown">
/// False when there's nothing to compare with: what the file held before Claude's first change isn't known (DESIGN.md
/// §8, "Before content"). The view shows the whole file as it is now, with nothing marked as changed.
/// </param>
/// <param name="Review">Marking the file as reviewed from the view, or null when it can't be.</param>
/// <param name="FromGit">
/// <paramref name="Before"/> is the file at HEAD (Changed files in git mode), not before Claude's first change. No HEAD
/// copy means the file is new to git, maybe the user's own, so it isn't deleted: there's nothing to revert to.
/// </param>
/// <param name="After">
/// What to compare with instead of the file now: how an earlier turn left it (DESIGN.md §8, "Changes per turn"). Null
/// reads the file.
/// </param>
/// <param name="AllowRevert">Whether <b>Revert</b> is offered; a turn's changes are only shown.</param>
public sealed record DiffSource(
    string Path,
    string DisplayPath,
    string? Before,
    string BeforeLabel,
    Func<Task>? OpenInDiffTool,
    Func<Task> OpenInEditor,
    Func<Task> Reveal,
    Func<Task> CopyPath,
    bool BeforeKnown = true,
    DiffReview? Review = null,
    bool FromGit = false,
    DiffAfter? After = null,
    bool AllowRevert = true);

/// <summary>A diff's "after" side that isn't the file now; null text means the file didn't exist then.</summary>
public sealed record DiffAfter(string? Text);

/// <summary>The diff view's <b>Reviewed</b> button (DESIGN.md §8, "Reviewed"). Called on the UI thread.</summary>
/// <param name="LatestChange">The id of Claude's latest change to the file, or null when it hasn't changed it.</param>
/// <param name="IsReviewed">Whether the file is marked as reviewed now.</param>
/// <param name="MarkReviewed">Marks the file as reviewed as of the given change: the one the view showed.</param>
public sealed record DiffReview(Func<string?> LatestChange, Func<bool> IsReviewed, Action<string?> MarkReviewed);

/// <summary>
/// The built-in diff view (DESIGN.md §8): the file before Claude's first change (or at HEAD) against the file now,
/// inline or side by side, with syntax highlighting.
/// </summary>
public sealed partial class DiffWindowViewModel : ViewModelBase
{
    private readonly DiffSource _source;
    private bool _dark;
    private string? _after;
    /// <summary>Claude's latest change to the file when it was read: what <b>Reviewed</b> marks.</summary>
    private string? _shownChange;
    private IReadOnlyList<IReadOnlyList<ColoredRun>>? _beforeColors;
    private IReadOnlyList<IReadOnlyList<ColoredRun>>? _afterColors;
    private bool _isBinary;

    public DiffWindowViewModel(DiffSource source, bool dark)
    {
        _source = source;
        _dark = dark;
        ShowWholeFile = !source.BeforeKnown;
        _ = LoadAsync();
    }

    public string Title => Path.GetFileName(_source.Path);

    public string DisplayPath => _source.DisplayPath;

    public string BeforeLabel => _source.BeforeLabel;

    public bool CanOpenInDiffTool => _source.OpenInDiffTool is not null;

    public bool CanMarkReviewed => _source.Review is not null;

    /// <summary>The file is marked as reviewed: the <b>Reviewed</b> button shows a check.</summary>
    [ObservableProperty]
    public partial bool IsReviewed { get; private set; }

    /// <summary>Asks the window to close, after <b>Reviewed</b>.</summary>
    public event Action? CloseRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevert))]
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

    // ---- Reverting (DESIGN.md §8) --------------------------------------------------------------------------------

    /// <summary>
    /// Claude's changes can be put back: the file as it was before is known, and the view isn't showing a file that's
    /// gone or binary.
    /// </summary>
    public bool CanRevert => HasRevertTarget && !IsLoading && _after is not null && !_isBinary && !string.Equals(_after, _source.Before, StringComparison.Ordinal);

    /// <summary>There's something to put back: what the file held is known, and from git, the file is at HEAD.</summary>
    private bool HasRevertTarget => _source.AllowRevert && _source.After is null && _source.BeforeKnown && !(_source.FromGit && _source.Before is null);

    /// <summary><b>Revert file</b>'s tip: what the file goes back to.</summary>
    public string RevertFileTip => _source.FromGit ? "Put the whole file back as it is at HEAD" : "Put the whole file back as it was before Claude changed it";

    /// <summary><b>Revert file</b> asks first: the second step shows in place.</summary>
    public InlineConfirmation RevertConfirmation => field ??= new(RevertFileAsync);

    /// <summary>
    /// Puts the whole file back as it was before Claude's first change; a file Claude created is deleted. From git, it's
    /// the file at HEAD, in the line endings the file has now: git keeps its copy with the ones it was committed in.
    /// </summary>
    [RelayCommand]
    private Task RevertFileAsync() => RevertAsync(_source.FromGit ? Revert.WithLineEndingsOf(_source.Before, _after) : _source.Before);

    /// <summary>Undoes one hunk of the diff, leaving the rest of Claude's changes.</summary>
    [RelayCommand]
    private Task RevertHunkAsync(DiffHunk? hunk)
    {
        if (hunk is null || Revert.Hunk(_after, hunk) is not { } reverted)
        {
            Message = ChangedSince;
            return Task.CompletedTask;
        }
        return RevertAsync(reverted);
    }

    private const string ChangedSince = "The file changed since this was shown, so nothing was reverted. Refresh to see it now.";

    private async Task RevertAsync(string? text)
    {
        if (!CanRevert)
        {
            return;
        }
        var path = _source.Path;
        var shown = _after;
        try
        {
            if (!await Task.Run(() => Revert.Write(path, shown, text)))
            {
                Message = ChangedSince;
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"Couldn't revert {Path.GetFileName(path)}: {ex.Message}";
            return;
        }
        await LoadAsync();
    }

    /// <summary>
    /// <b>Reviewed</b>: marks the file as reviewed and closes the view (DESIGN.md §8, "Reviewed"). It marks the change the
    /// view showed, so a change Claude made since leaves the file unreviewed.
    /// </summary>
    [RelayCommand]
    private void MarkReviewed()
    {
        if (_source.Review is not { } review)
        {
            return;
        }
        review.MarkReviewed(_shownChange);
        IsReviewed = review.IsReviewed();
        CloseRequested?.Invoke();
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        // Before the file is read, so a change Claude makes meanwhile isn't counted as seen.
        _shownChange = _source.Review?.LatestChange();
        IsReviewed = _source.Review?.IsReviewed() ?? false;
        var before = _source.Before;
        var path = _source.Path;
        var dark = _dark;
        var fixedAfter = _source.After;
        var (after, beforeColors, afterColors, message) = await Task.Run(() =>
        {
            string? current = fixedAfter?.Text;
            try
            {
                if (fixedAfter is null)
                {
                    current = File.Exists(path) ? File.ReadAllText(path) : null;
                }
            }
            catch (IOException)
            {
            }
            if (IsBinary(before, current))
            {
                return (current, null, null, "This is a binary file, so there's nothing to show line by line.");
            }
            var (beforeColors, afterColors) = Highlight(before, current, path, dark);
            return (current, beforeColors, afterColors, current is not null ? null : fixedAfter is null ? "The file has been deleted." : "The file didn't exist after this turn.");
        });
        _after = after;
        _isBinary = IsBinary(before, after);
        _beforeColors = _source.BeforeKnown ? beforeColors : afterColors;
        _afterColors = afterColors;
        Message = message;
        var (added, removed) = LineDiff.Count(before, after);
        Stats = _source.BeforeKnown ? $"+{added} −{removed}" : "";
        // Build the rows before saying it's loaded, so nothing sees "loaded" with no rows.
        BuildRows();
        IsLoading = false;
        OnPropertyChanged(nameof(CanRevert));
        if (dark != _dark)
        {
            // The theme changed while it loaded.
            _ = RecolorAsync();
        }
    }

    /// <summary>
    /// The window's theme changed: the syntax colors follow it, for the text already shown, without reading the file
    /// again (which would count Claude's later changes as seen).
    /// </summary>
    public void UseDarkColors(bool dark)
    {
        if (dark == _dark)
        {
            return;
        }
        _dark = dark;
        if (!IsLoading)
        {
            _ = RecolorAsync();
        }
    }

    private async Task RecolorAsync()
    {
        var dark = _dark;
        var before = _source.Before;
        var after = _after;
        var path = _source.Path;
        var (beforeColors, afterColors) = await Task.Run(() => IsBinary(before, after) ? (null, null) : Highlight(before, after, path, dark));
        // The theme changed again, or the file was read again, meanwhile: that colors it instead.
        if (dark != _dark || IsLoading || !ReferenceEquals(after, _after))
        {
            return;
        }
        _beforeColors = _source.BeforeKnown ? beforeColors : afterColors;
        _afterColors = afterColors;
        Build();
    }

    private static bool IsBinary(string? before, string? after) =>
        (before is not null && LineDiff.LooksBinary(before)) || (after is not null && LineDiff.LooksBinary(after));

    private static (IReadOnlyList<IReadOnlyList<ColoredRun>>? Before, IReadOnlyList<IReadOnlyList<ColoredRun>>? After) Highlight(
        string? before, string? after, string path, bool dark)
    {
        var name = Path.GetFileName(path);
        return (before is null ? null : SyntaxHighlighter.Highlight(LineDiff.SplitLines(before), name, dark),
            after is null ? null : SyntaxHighlighter.Highlight(LineDiff.SplitLines(after), name, dark));
    }

    /// <summary>With the "before" unknown, the file is compared with itself, so nothing shows as changed.</summary>
    private string? Before => _source.BeforeKnown ? _source.Before : _after;

    private void Build()
    {
        if (!IsLoading)
        {
            BuildRows();
        }
    }

    private void BuildRows()
    {
        var before = Before;
        var lines = new List<(DiffLineEntry? Line, string? Header, DiffHunk? Hunk)>();
        if (ShowWholeFile)
        {
            lines.AddRange(LineDiff.Full(before, _after).Select(l => ((DiffLineEntry?)l, (string?)null, (DiffHunk?)null)));
        }
        else
        {
            var revertable = HasRevertTarget && _after is not null && !_isBinary;
            foreach (var hunk in LineDiff.Hunks(before, _after))
            {
                lines.Add((null, hunk.Header, revertable ? hunk : null));
                lines.AddRange(hunk.Lines.Select(l => ((DiffLineEntry?)l, (string?)null, (DiffHunk?)null)));
            }
        }

        if (IsSideBySide)
        {
            var rows = new List<SideBySideDiffRow>();
            var run = new List<DiffLineEntry>();
            foreach (var (line, header, hunk) in lines)
            {
                if (line is not null)
                {
                    run.Add(line);
                    continue;
                }
                Flush();
                rows.Add(new SideBySideDiffRow(null, null, true, header!, hunk));
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
                : new InlineDiffRow("", "", "", item.Header!, null, DiffOp.Context, true, item.Hunk)).ToArray();
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
