using Avalonia;
using Avalonia.Controls.Documents;
using LiveMarkdown.Avalonia;
using TextMateSharp.Grammars;

namespace Claudette.App.Views;

/// <summary>
/// The syntax colors of code blocks in Markdown, which follow the theme (DESIGN.md §3, "Visual style"). App.axaml sets
/// <see cref="ThemeProperty"/> from the <c>CodeColorTheme</c> token rather than LiveMarkdown's own
/// <see cref="CodeBlock.ColorTheme"/>: LiveMarkdown re-highlights a block whose theme changes by setting its code again
/// as text, and once the block is shown, a one-line block's text goes to its text block rather than its lines. The
/// block then lost its colors, and Copy copied nothing, until Claudette restarted (GitHub issue #14).
/// </summary>
internal sealed class CodeBlockTheme : AvaloniaObject
{
    public static readonly AttachedProperty<ThemeName> ThemeProperty =
        AvaloniaProperty.RegisterAttached<CodeBlockTheme, CodeBlock, ThemeName>("Theme", ThemeName.DarkPlus);

    static CodeBlockTheme() => ThemeProperty.Changed.AddClassHandler<CodeBlock>((block, e) => Apply(block, e.GetNewValue<ThemeName>()));

    private CodeBlockTheme()
    {
    }

    public static ThemeName GetTheme(CodeBlock block) => block.GetValue(ThemeProperty);

    public static void SetTheme(CodeBlock block, ThemeName value) => block.SetValue(ThemeProperty, value);

    private static void Apply(CodeBlock block, ThemeName theme)
    {
        // Before the block is shown, LiveMarkdown's own re-highlighting works.
        if (block.CodeTextBlock is null)
        {
            block.ColorTheme = theme;
            return;
        }
        var code = block.Code;
        var highlights = block.AutoSyntaxHighlight;
        block.AutoSyntaxHighlight = false;
        block.ColorTheme = theme;
        if (code is null)
        {
            block.AutoSyntaxHighlight = highlights;
            return;
        }
        // A plain run per line with line breaks between, as LiveMarkdown lays the lines out, so streaming more of the
        // reply still finds each line where it expects; then highlighted in one go, so comments and strings that span
        // lines color correctly.
        block.Inlines.Clear();
        var lines = code.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                block.Inlines.Add(new LineBreak());
            }
            block.Inlines.Add(new Run(lines[i]));
        }
        block.AutoSyntaxHighlight = highlights;
        if (highlights)
        {
            block.HighlightSyntax();
        }
    }
}
