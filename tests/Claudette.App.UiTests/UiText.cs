using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Claudette.App.UiTests;

/// <summary>
/// What a view shows, as indented text for snapshot tests: the visible text, buttons, fields and meters, in tree order.
/// Positions and pixels are left out, so a snapshot only changes when what the user sees or can do changes.
/// </summary>
public static class UiText
{
    /// <summary>Puts <paramref name="content"/> in a window of the given size and lets it lay out.</summary>
    public static Window Show(Control content, double width = 1200, double height = 800)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        Settle(window);
        return window;
    }

    /// <summary>Runs queued UI work, then lays the window out again, a few times over for work that queues more.</summary>
    public static void Settle(Window window)
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    public static string Describe(Visual root, params (string From, string To)[] replacements)
    {
        var builder = new StringBuilder();
        Walk(root, 0, builder);
        var text = builder.ToString();
        foreach (var (from, to) in replacements)
        {
            text = text.Replace(from, to, StringComparison.Ordinal);
        }
        return text.Replace('\\', '/');
    }

    private static void Walk(Visual visual, int depth, StringBuilder builder)
    {
        if (!visual.IsVisible)
        {
            return;
        }
        var line = Line(visual);
        if (line is not null)
        {
            builder.Append(' ', depth * 2).AppendLine(line);
            depth++;
        }
        // A field, text or meter says what it holds, and so does a button with a plain label; a button made of
        // several parts (a tab's row, the usage header) goes on to list them.
        if (line is not null && (visual is TextBox or TextBlock or ProgressBar or ComboBox || visual is ContentControl control && SingleText(control) is not null))
        {
            return;
        }
        foreach (var child in visual.GetVisualChildren())
        {
            Walk(child, depth, builder);
        }
    }

    private static string? Line(Visual visual) => visual switch
    {
        TextBox box => $"[field] {Quote(box.Text is { Length: > 0 } text ? text : box.PlaceholderText)}",
        ComboBox combo => $"[choice] {Quote(combo.SelectionBoxItem?.ToString())}",
        CheckBox check => $"[{(check.IsChecked == true ? "x" : " ")}] {Label(check)}",
        ToggleButton toggle => $"[toggle{(toggle.IsChecked == true ? " on" : "")}] {Label(toggle)}",
        Button button => $"[button] {Label(button)}",
        SplitButton split => $"[split button] {Label(split)}",
        ProgressBar bar => $"[meter {bar.Value:0}%]",
        TextBlock block when !IsInside<TextBox>(visual) && TextOf(block) is { Length: > 0 } text => Quote(text),
        _ => null,
    };

    /// <summary>A text block's text, including one built from inlines (Markdown, runs with formatting).</summary>
    private static string? TextOf(TextBlock block) => block.Text is { Length: > 0 } text ? text : block.Inlines is { } inlines ? Flatten(inlines) : null;

    private static string Flatten(IEnumerable<Inline> inlines) => string.Concat(inlines.Select(inline => inline switch
    {
        Run run => run.Text ?? "",
        Span span => Flatten(span.Inlines),
        LineBreak => "\n",
        _ => "",
    }));

    private static string Label(ContentControl control) =>
        AutomationProperties.GetName(control) is { Length: > 0 } name ? name
        : SingleText(control) is { } text ? text
        : control.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && TextOf(t) is { Length: > 0 }) ? "(parts below)"
        : "(icon)";

    /// <summary>The label of a button whose content is just text, or null when it's made of several parts.</summary>
    private static string? SingleText(ContentControl control)
    {
        if (control.Content is string text)
        {
            return text;
        }
        var parts = control.GetVisualDescendants()
            .Where(v => v.IsEffectivelyVisible && v is TextBox or ProgressBar or ComboBox or Button or ToggleButton or TextBlock)
            .ToList();
        return parts is [TextBlock only] && TextOf(only) is { Length: > 0 } label ? label : null;
    }

    private static bool IsInside<T>(Visual visual) => visual.GetVisualAncestors().OfType<T>().Any();

    private static string Quote(string? text) => text is null ? "\"\"" : $"\"{text.ReplaceLineEndings("⏎")}\"";
}
