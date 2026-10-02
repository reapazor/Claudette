using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.Views;

namespace Claudette.App.UiTests;

/// <summary>Claude's clarifying questions and plans (DESIGN.md §7) answered with the mouse.</summary>
public sealed class QuestionCardUiTests
{
    [AvaloniaFact]
    public async Task A_multi_select_question_keeps_every_box_ticked_and_a_single_choice_keeps_one()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });

        h.Transport.Emit("""
            {"type":"control_request","request_id":"p1","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion","input":{"questions":[
              {"question":"Which parts?","header":"Parts","multiSelect":true,"options":[{"label":"API"},{"label":"UI"},{"label":"Docs"}]},
              {"question":"Which database?","header":"Database","multiSelect":false,"options":[{"label":"Postgres"},{"label":"SQLite"}]}]}}}
            """);
        await TabTestHarness.Eventually(() => tab.Items.OfType<QuestionItem>().Any(), "the question");
        var prompt = tab.Items.OfType<QuestionItem>().Single();
        UiText.Settle(window);
        var conversation = window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "ConversationItems");
        Assert.IsType<CheckBox>(Choice(conversation, "API"));
        Assert.IsType<RadioButton>(Choice(conversation, "Postgres"));

        Click(window, Choice(conversation, "API"));
        Click(window, Choice(conversation, "Docs"));
        Click(window, Choice(conversation, "Postgres"));
        Click(window, Choice(conversation, "SQLite"));

        Assert.Equal(["API", "Docs"], prompt.Questions[0].Options.Where(o => o.IsSelected).Select(o => o.Label));
        Assert.Equal(["SQLite"], prompt.Questions[1].Options.Where(o => o.IsSelected).Select(o => o.Label));
        Assert.True(Choice(conversation, "API").IsChecked);
        Assert.True(Choice(conversation, "Docs").IsChecked);
        Assert.False(Choice(conversation, "Postgres").IsChecked);
        Assert.Equal("API, Docs", prompt.Questions[0].Answer);
    }

    [AvaloniaFact]
    public async Task A_plans_card_offers_auto_mode_first_while_the_tab_can_use_it()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        var conversation = window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "ConversationItems");
        Button[] Approves() => [.. conversation.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && b.Content is string text && text.StartsWith("Approve", StringComparison.Ordinal))];
        async Task<PlanItem> PlanAsync(string requestId)
        {
            h.Transport.Emit("""{"type":"control_request","request_id":"ID","request":{"subtype":"can_use_tool","tool_name":"ExitPlanMode","input":{"plan":"1. Read\n2. Fix"}}}"""
                .Replace("\"ID\"", $"\"{requestId}\"", StringComparison.Ordinal));
            await UiText.SettleUntilAsync(window, () => tab.Items.OfType<PlanItem>().Any(p => p.Request.RequestId == requestId) && Approves().Length > 0, "the plan");
            return tab.Items.OfType<PlanItem>().Single(p => p.Request.RequestId == requestId);
        }

        var plan = await PlanAsync("p1");
        Assert.Equal(["Approve, auto mode", "Approve, accept edits", "Approve, ask before edits"], Approves().Select(b => b.Content as string));
        Assert.Equal([true, false, false], Approves().Select(b => b.Classes.Contains("accent")));
        Click(window, Approves()[0]);
        Assert.Equal("Approved. Auto mode checks actions and blocks risky ones", plan.Outcome);

        // Haiku has no auto mode: Approve, accept edits is the main button again.
        tab.ChooseModelCommand.Execute(tab.Models.Single(m => m.Value == "haiku"));
        await tab.ModelSwitch.ConfirmCommand.ExecuteAsync(null);
        await PlanAsync("p2");
        Assert.Equal(["Approve, accept edits", "Approve, ask before edits"], Approves().Select(b => b.Content as string));
        Assert.Equal([true, false], Approves().Select(b => b.Classes.Contains("accent")));
    }

    /// <summary>The check box or radio button for an option, the only one it has.</summary>
    private static ToggleButton Choice(Control conversation, string label) =>
        conversation.GetVisualDescendants().OfType<ToggleButton>()
            .Single(b => b.DataContext is QuestionOption o && o.Label == label);

    private static void Click(Window window, Control target)
    {
        var center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseMove(center);
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        UiText.Settle(window);
    }
}
