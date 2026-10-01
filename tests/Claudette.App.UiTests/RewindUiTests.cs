using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.Views;

namespace Claudette.App.UiTests;

/// <summary>A message's menu, to go back to it (DESIGN.md §5, "Rewind and branch").</summary>
public class RewindUiTests
{
    [AvaloniaFact]
    public async Task A_messages_menu_offers_to_go_back_to_it()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell }, 1100, 800);
        tab.ComposerText = "first";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit("""{"type":"user","uuid":"u1","isReplay":true,"message":{"role":"user","content":"first"}}""");
        await UiText.SettleUntilAsync(window, () => tab.Items.OfType<UserMessageItem>().Single().Uuid == "u1", "the echo");
        var message = tab.Items.OfType<UserMessageItem>().Single();

        // The menu is in a flyout, outside the conversation's own tree: its commands must still reach the tab.
        var opener = window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "More for this message");
        var flyout = Assert.IsType<MenuFlyout>(opener.Flyout);
        flyout.ShowAt(opener);
        UiText.Settle(window);
        var items = flyout.Items.OfType<MenuItem>().ToDictionary(i => (string)i.Header!);

        Assert.Equal(["Edit and resend…", "Branch from here", "Restore files to before this…"], items.Keys);
        Assert.Same(tab.EditAndResendCommand, items["Edit and resend…"].Command);
        Assert.Same(tab.BranchFromHereCommand, items["Branch from here"].Command);
        Assert.Same(tab.RestoreFilesBeforeCommand, items["Restore files to before this…"].Command);
        Assert.All(items.Values, i => Assert.Same(message, i.CommandParameter));
        // Its id came back with the echo: Claude Code may have copies of files from before it.
        Assert.True(items["Restore files to before this…"].IsVisible);
        flyout.Hide();
    }
}
