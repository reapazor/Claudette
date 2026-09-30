using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Claudette.App.Controls;
using Claudette.App.Diffs;
using Claudette.App.Views;

namespace Claudette.App.UiTests;

/// <summary>
/// The built-in diff view rendered (DESIGN.md §8): long lines scroll sideways, every line together with the line numbers
/// staying put, and a long diff scrolls up and down.
/// </summary>
public class DiffWindowUiTests
{
    [AvaloniaFact]
    public async Task Long_lines_scroll_sideways_together_and_the_line_numbers_stay_put()
    {
        await using var file = await DiffFile.CreateAsync("one\ntwo\n", "one\n" + new string('x', 400) + "\ntwo\n");
        var (window, viewModel) = await ShowAsync(file);
        var scroller = Shown<LineScroller>(window);
        var number = scroller.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.Text == "1");
        var numberAt = number.TranslatePoint(default, window);

        Assert.True(scroller.ScrollableWidth > 0);
        Assert.True(Bar(scroller).IsVisible);

        scroller.Offset = 100;
        UiText.Settle(window);

        Assert.Contains(Code(scroller), code => code.Code == "two");
        Assert.All(Code(scroller), code => Assert.Equal(-100, code.Bounds.X));
        Assert.Equal(numberAt, number.TranslatePoint(default, window));

        // Shift turns the wheel sideways; the list doesn't scroll up or down with it
        window.MouseWheel(Middle(scroller, window), new Vector(0, -1), RawInputModifiers.Shift);
        UiText.Settle(window);

        Assert.Equal(150, scroller.Offset);
        Assert.Equal(0, Scroll(scroller).Offset.Y);

        viewModel.IsSideBySide = true;
        UiText.Settle(window);
        var sides = Shown<LineScroller>(window);
        sides.Offset = 80;
        UiText.Settle(window);

        Assert.NotSame(scroller, sides);
        Assert.True(sides.ScrollableWidth > 0);
        Assert.All(Code(sides), code => Assert.Equal(-80, code.Bounds.X));
        Assert.Contains(Code(sides), code => code.Code == "one" && IsOnTheRight(code, sides));
    }

    [AvaloniaFact]
    public async Task Lines_that_fit_have_no_sideways_scroll_bar()
    {
        await using var file = await DiffFile.CreateAsync("one\ntwo\n", "one\nthree\n");
        var (window, _) = await ShowAsync(file);
        var scroller = Shown<LineScroller>(window);

        Assert.Equal(0, scroller.ScrollableWidth);
        Assert.False(Bar(scroller).IsVisible);
    }

    [AvaloniaFact]
    public async Task A_long_diff_scrolls_up_and_down()
    {
        var lines = string.Concat(Enumerable.Range(1, 300).Select(i => $"line {i}\n"));
        await using var file = await DiffFile.CreateAsync(lines, lines + "added\n");
        var (window, viewModel) = await ShowAsync(file);
        viewModel.ShowWholeFile = true;
        UiText.Settle(window);
        var scroller = Shown<LineScroller>(window);
        var scroll = Scroll(scroller);

        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);

        window.MouseWheel(Middle(scroller, window), new Vector(0, -3));
        UiText.Settle(window);

        Assert.True(scroll.Offset.Y > 0);
    }

    /// <summary>The syntax colors follow a change of theme while the window is open (GitHub issue #14).</summary>
    [AvaloniaFact]
    public async Task The_syntax_colors_follow_the_theme_while_the_window_is_open()
    {
        var app = Application.Current!;
        var theme = app.RequestedThemeVariant;
        try
        {
            app.RequestedThemeVariant = ThemeVariant.Light;
            await using var file = await DiffFile.CreateAsync("var a = 1;\n", "var a = 2;\n");
            var (window, viewModel) = await ShowAsync(file);
            var light = Colors(viewModel);

            app.RequestedThemeVariant = ThemeVariant.Dark;
            await UiText.SettleUntilAsync(window, () => !Colors(viewModel).SequenceEqual(light), "the dark colors");

            // As a window opened in the dark theme colors it.
            var (_, opened) = await ShowAsync(file, dark: true);
            Assert.Equal(Colors(opened), Colors(viewModel));
        }
        finally
        {
            app.RequestedThemeVariant = theme;
        }

        static List<string?> Colors(DiffWindowViewModel viewModel) => [.. viewModel.InlineRows.SelectMany(r => r.Runs ?? []).Select(r => r.Color)];
    }

    private static async Task<(DiffWindow Window, DiffWindowViewModel ViewModel)> ShowAsync(DiffFile file, bool dark = false)
    {
        var source = new DiffSource(file.Path, "src/auth.cs", file.Before, "compared with before Claude's first change in this session",
            OpenInDiffTool: null, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask);
        var viewModel = new DiffWindowViewModel(source, dark);
        var window = new DiffWindow { DataContext = viewModel, Width = 700, Height = 400 };
        window.Show();
        await UiText.SettleUntilAsync(window, () => !viewModel.IsLoading, "the diff");
        return (window, viewModel);
    }

    private static T Shown<T>(Visual root) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().Single(v => v.IsEffectivelyVisible);

    private static ScrollBar Bar(LineScroller scroller) => scroller.GetVisualChildren().OfType<ScrollBar>().Single();

    private static ListBox List(LineScroller scroller) => scroller.GetVisualChildren().OfType<ListBox>().Single();

    /// <summary>The code on each line shown, which scrolls: the one child of each <see cref="ScrollingLine"/>.</summary>
    private static List<ColoredTextBlock> Code(LineScroller scroller) =>
        scroller.GetVisualDescendants().OfType<ScrollingLine>().Where(l => l.IsEffectivelyVisible).Select(l => (ColoredTextBlock)l.Child!).ToList();

    private static ScrollViewer Scroll(LineScroller scroller) => List(scroller).GetVisualDescendants().OfType<ScrollViewer>().First();

    private static bool IsOnTheRight(Visual visual, LineScroller scroller) =>
        visual.FindAncestorOfType<ScrollingLine>()!.TranslatePoint(default, scroller)!.Value.X > scroller.Bounds.Width / 2;

    private static Point Middle(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    /// <summary>A file on disk as it is now, with what it held before.</summary>
    private sealed class DiffFile : IAsyncDisposable
    {
        private readonly string _folder;

        private DiffFile(string folder, string path, string before) => (_folder, Path, Before) = (folder, path, before);

        public string Path { get; }

        public string Before { get; }

        public static async Task<DiffFile> CreateAsync(string before, string now)
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"claudette-diff-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            var path = System.IO.Path.Combine(folder, "auth.cs");
            await File.WriteAllTextAsync(path, now, TestContext.Current.CancellationToken);
            return new DiffFile(folder, path, before);
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(_folder, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
