using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Claudette.App.Controls;

namespace Claudette.App.UiTests;

/// <summary>Text in different fonts or sizes side by side shares a baseline (Controls/Baseline; DESIGN.md §3).</summary>
public class BaselineTests
{
    [AvaloniaTheory]
    [InlineData(VerticalAlignment.Top)]
    [InlineData(VerticalAlignment.Center)]
    public void Smaller_monospace_text_goes_on_the_baseline_of_the_text_beside_it_whatever_its_size(VerticalAlignment alignment)
    {
        var (window, name, code) = Row(alignment, codeSize: 11);

        AssertOnBaseline(code, name, window);
        Assert.NotEqual(0, Baseline.ShiftOf(code));

        code.FontSize = 26;
        UiText.Settle(window);
        AssertOnBaseline(code, name, window);
        Assert.True(Baseline.ShiftOf(code) < 0, "bigger, it goes up to meet the line");

        name.FontSize = 30;
        UiText.Settle(window);
        AssertOnBaseline(code, name, window);
    }

    [AvaloniaFact]
    public void Its_shift_holds_steady_across_layout_passes_and_goes_with_the_setting()
    {
        var (window, name, code) = Row(VerticalAlignment.Top, codeSize: 10);
        var shift = Baseline.ShiftOf(code);

        for (var i = 0; i < 5; i++)
        {
            window.InvalidateMeasure();
            UiText.Settle(window);
            Assert.Equal(shift, Baseline.ShiftOf(code));
        }

        Baseline.SetAlignWith(code, null);
        Assert.Null(code.RenderTransform);
        Assert.NotNull(name);
    }

    /// <summary>A name in the interface font and code beside it, both top-aligned in a row.</summary>
    private static (Window Window, TextBlock Name, TextBlock Code) Row(VerticalAlignment alignment, double codeSize)
    {
        var name = new TextBlock { Text = "GrappleComponent.cpp", FontSize = 14, VerticalAlignment = alignment };
        var code = new TextBlock { Text = "+47 -0", FontSize = codeSize, FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"), VerticalAlignment = alignment };
        Grid.SetColumn(code, 1);
        Baseline.SetAlignWith(code, name);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 40, Children = { name, code } };
        return (UiText.Show(row, 400, 100), name, code);
    }

    internal static void AssertOnBaseline(TextBlock text, TextBlock other, Visual relativeTo) =>
        Assert.InRange(Baseline.BaselineIn(text, relativeTo)!.Value - Baseline.BaselineIn(other, relativeTo)!.Value, -0.6, 0.6);
}
