using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.Views;
using Claudette.Core.Settings;

namespace Claudette.App.UiTests;

/// <summary>
/// Where a tab works, on its row in the sidebar (DESIGN.md §4, "Sidebar"): the git branch and the Perforce changelist
/// at the right of the second line, so the name keeps the first line's width.
/// </summary>
public class TabRowBadgesUiTests
{
    [AvaloniaFact]
    public async Task The_branch_and_changelist_sit_at_the_right_of_the_second_line_without_making_the_row_taller()
    {
        await using var h = new TabTestHarness(settings =>
        {
            settings.Perforce.ShowChangelistOnTabs = true;
            settings.Appearance.ShowBranchOnTabs = false;
        }, dispatcher: new AvaloniaUiDispatcher());
        Directory.CreateDirectory(Path.Combine(h.WorkFolder, ".git", "refs", "heads", "feature"));
        File.WriteAllText(Path.Combine(h.WorkFolder, ".git", "HEAD"), "ref: refs/heads/feature/auth\n");
        File.WriteAllText(Path.Combine(h.WorkFolder, ".git", "config"), "[core]\n");
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        var row = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("tabrow"));
        var branch = row.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "BranchBadge");
        var changelist = row.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("changelist"));
        var height = row.Bounds.Height;
        Assert.False(branch.IsEffectivelyVisible);
        Assert.False(changelist.IsEffectivelyVisible);

        h.Services.Settings.Appearance.ShowBranchOnTabs = true;
        h.Services.SaveSettings();
        EmitBash(h, "b1", "p4 edit -c 12345 src/login.cpp", "//depot/src/login.cpp#7 - opened for edit");
        await UiText.SettleUntilAsync(window, () => changelist.IsEffectivelyVisible && branch.IsEffectivelyVisible, "the badges");

        var name = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == tab.DisplayName);
        var detail = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == tab.RowDetail);
        Assert.Equal("feature/auth", tab.BranchBadge);
        Assert.Equal(tab.BranchBadgeTip, AutomationProperties.GetName(branch));
        // On the second line, after its text, the branch then the changelist; the name has the first line to itself.
        Assert.True(Below(branch, name, row) && Below(changelist, name, row), "The badges aren't under the name.");
        Assert.True(Overlaps(branch, detail, row) && Overlaps(changelist, detail, row), "The badges aren't level with the second line.");
        Assert.True(InRow(detail, row).Right <= InRow(branch, row).Left, "The model and effort run into the branch.");
        Assert.True(InRow(branch, row).Right <= InRow(changelist, row).Left, "The branch runs into the changelist.");
        Assert.Equal(height, row.Bounds.Height, precision: 3);
        var badge = changelist.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("rowbadge"));
        Assert.Equal(new CornerRadius(4), badge.CornerRadius);

        h.Services.Settings.Appearance.Style = AppStyle.Claude;
        h.Services.SaveSettings();
        UiText.Settle(window);
        Assert.Equal(new CornerRadius(8), badge.CornerRadius);
        window.Close();
    }

    private static Rect InRow(Control control, Visual row) => new(control.TranslatePoint(default, row)!.Value, control.Bounds.Size);

    private static bool Below(Control control, Control above, Visual row) => InRow(control, row).Top >= InRow(above, row).Bottom - 0.5;

    private static bool Overlaps(Control a, Control b, Visual row) => InRow(a, row).Top < InRow(b, row).Bottom && InRow(b, row).Top < InRow(a, row).Bottom;

    private static void EmitBash(TabTestHarness h, string id, string command, string output)
    {
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = "Bash", ["input"] = new JsonObject { ["command"] = command } }) },
        });
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = output, ["is_error"] = false }) },
            ["tool_use_result"] = new JsonObject { ["stdout"] = output, ["stderr"] = "", ["interrupted"] = false },
        });
    }
}
