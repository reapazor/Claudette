using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Claudette.App.Diffs;

namespace Claudette.App.Controls;

/// <summary>A line of code with syntax colors: <see cref="TextBlock.Text"/> split into <see cref="Runs"/>.</summary>
public sealed class ColoredTextBlock : SelectableTextBlock
{
    public static readonly StyledProperty<IReadOnlyList<ColoredRun>?> RunsProperty =
        AvaloniaProperty.Register<ColoredTextBlock, IReadOnlyList<ColoredRun>?>(nameof(Runs));

    public static readonly StyledProperty<string?> CodeProperty =
        AvaloniaProperty.Register<ColoredTextBlock, string?>(nameof(Code));

    private static readonly Dictionary<string, IBrush> Brushes = [];

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public IReadOnlyList<ColoredRun>? Runs { get => GetValue(RunsProperty); set => SetValue(RunsProperty, value); }

    /// <summary>The line's text.</summary>
    public string? Code { get => GetValue(CodeProperty); set => SetValue(CodeProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RunsProperty || change.Property == CodeProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var code = Code ?? "";
        if (Runs is not { Count: > 0 } runs)
        {
            Inlines = null;
            Text = code;
            return;
        }
        var inlines = new InlineCollection();
        var position = 0;
        foreach (var run in runs)
        {
            if (run.Start > position && run.Start <= code.Length)
            {
                inlines.Add(new Run(code[position..run.Start]));
            }
            var start = Math.Clamp(run.Start, 0, code.Length);
            var end = Math.Clamp(run.Start + run.Length, start, code.Length);
            var piece = new Run(code[start..end]);
            if (run.Color is { } color && BrushFor(color) is { } brush)
            {
                piece.Foreground = brush;
            }
            inlines.Add(piece);
            position = end;
        }
        if (position < code.Length)
        {
            inlines.Add(new Run(code[position..]));
        }
        Inlines = inlines;
    }

    private static IBrush? BrushFor(string color)
    {
        lock (Brushes)
        {
            if (!Brushes.TryGetValue(color, out var brush))
            {
                if (!Color.TryParse(color, out var parsed))
                {
                    return null;
                }
                brush = new ImmutableSolidColorBrush(parsed);
                Brushes[color] = brush;
            }
            return brush;
        }
    }
}
