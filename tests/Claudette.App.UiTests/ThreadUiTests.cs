using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.App.Views;

namespace Claudette.App.UiTests;

/// <summary>Threads rendered (DESIGN.md §18, "Threads"): the sidebar's rows, and the strip over a thread's composer.</summary>
public class ThreadUiTests
{
    [AvaloniaFact]
    public async Task A_threads_sub_threads_sit_indented_under_it_and_the_thread_has_its_icon()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        h.Factory.ProcessPerSession = true;
        var plan = await ThreadScript.OpenAsync(h, "Plan");
        await ThreadScript.OpenAsync(h, "Other");
        var art = await ThreadScript.OpenAsync(h, "Art page");
        h.Shell.MakeThreadCommand.Execute(plan);
        art.AssignToThreadCommand.Execute(plan);
        var window = UiText.Show(new ShellView { DataContext = h.Shell });

        var rows = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("tabrow")).ToList();
        double Left(Control c) => c.TranslatePoint(default, window)!.Value.X;

        Assert.Equal(["Plan", "Art page", "Other"], rows.Select(r => ((TabViewModel)r.DataContext!).DisplayName));
        Assert.True(Left(rows[1]) > Left(rows[0]), "The sub-thread should be indented under its thread");
        Assert.Equal(Left(rows[0]), Left(rows[2]));
        var icon = Assert.Single(window.GetVisualDescendants().OfType<Border>(), b => b is { Name: "ThreadIcon", IsEffectivelyVisible: true });
        Assert.Equal("A thread, with 1 sub-thread", ToolTip.GetTip(icon));
        Assert.Equal(plan, icon.DataContext);
    }

    [AvaloniaFact]
    public async Task The_strip_over_a_threads_composer_shows_a_message_to_let_go_and_a_sub_thread_waiting()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        h.Factory.ProcessPerSession = true;
        var plan = await ThreadScript.OpenAsync(h, "Plan");
        var art = await ThreadScript.OpenAsync(h, "Art page");
        h.Shell.MakeThreadCommand.Execute(plan);
        art.AssignToThreadCommand.Execute(plan);
        h.Shell.SelectTab(plan.Id);
        var window = UiText.Show(new ShellView { DataContext = h.Shell });

        h.Transport.EmitTo(1, ThreadScript.Init());
        h.Transport.EmitTo(1, ThreadScript.CanUseTool("req-1", "npm test"));
        h.Transport.EmitTo(0, ThreadScript.SendMessage("hk1", "Art page", "Build the gallery next, with the same cards as the art page."));
        await UiText.SettleUntilAsync(window, () => plan.ThreadApprovals.Count == 1 && plan.WaitingSubThreads.Count == 1, "the strip's rows");
        var strip = Assert.Single(window.GetVisualDescendants().OfType<Border>(), b => b is { Name: "ThreadStrip", IsEffectivelyVisible: true });

        await Verify(UiText.Describe(strip));
    }
}
