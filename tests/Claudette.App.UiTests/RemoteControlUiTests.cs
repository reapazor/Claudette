using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.App.ViewModels.Settings;
using Claudette.App.Views;
using Claudette.Core.Auth;

namespace Claudette.App.UiTests;

/// <summary>
/// Remote Control rendered (DESIGN.md §18): the switch in the tab's menu and in Tab settings…, the icon on the tab's
/// row, and the Claude app block in Settings → Claude Code.
/// </summary>
public class RemoteControlUiTests
{
    private const string SessionUrl = "https://claude.ai/code/session_01AbCdEf";

    [AvaloniaFact]
    public async Task Connect_to_the_Claude_app_ticks_in_the_tab_menu_and_marks_the_row()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        h.Transport.Answers["remote_control"] = request => request["enabled"]!.GetValue<bool>() ? new JsonObject { ["session_url"] = SessionUrl } : new JsonObject();
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        var icon = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "RemoteIcon");
        var row = icon.FindAncestorOfType<Button>()!;
        row.ContextMenu!.Open(row);
        UiText.Settle(window);
        var item = row.ContextMenu.Items.OfType<MenuItem>().Single(m => m.Header as string == "Connect to the Claude app");
        var open = row.ContextMenu.Items.OfType<MenuItem>().Single(m => m.Header as string == "Open in the Claude app");
        Assert.False(icon.IsEffectivelyVisible);
        Assert.False(item.IsChecked);
        Assert.True(item.IsEffectivelyEnabled);
        Assert.False(open.IsVisible);

        ClickMenuItem(window, item);
        await UiText.SettleUntilAsync(window, () => tab.RemoteControl.Status.IsConnected, "the connection");

        Assert.True(tab.RemoteControl.IsOn);
        Assert.True(item.IsChecked);
        Assert.True(icon.IsEffectivelyVisible);
        Assert.Equal("Connected to the Claude app", ToolTip.GetTip(icon));
        Assert.Equal("Connected to the Claude app", AutomationProperties.GetName(icon));
        Assert.True(open.IsVisible);
        open.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        UiText.Settle(window);
        Assert.Equal([SessionUrl], h.Platform.OpenedUrls);

        // Claude Code answers once it has closed the connection; until then the row's icon is fainter.
        h.Transport.Answers["remote_control"] = _ => null;
        ClickMenuItem(window, item);
        await UiText.SettleUntilAsync(window, () => tab.RemoteControl.IsLeaving, "switching off");

        Assert.False(item.IsChecked);
        Assert.True(icon.IsEffectivelyVisible);
        Assert.Equal(0.2, icon.Opacity);
        Assert.Equal("Disconnecting from the Claude app…", ToolTip.GetTip(icon));

        var id = h.Transport.Sent.Last(m => m["request"]?["subtype"]?.GetValue<string>() == "remote_control")["request_id"]!.GetValue<string>();
        h.Transport.Emit(Core.Protocol.OutgoingMessages.ControlSuccess(id, null));
        await UiText.SettleUntilAsync(window, () => !tab.RemoteControl.Status.IsConnected, "the disconnection");

        Assert.False(tab.RemoteControl.IsOn);
        Assert.False(item.IsChecked);
        Assert.False(icon.IsEffectivelyVisible);
        Assert.False(open.IsVisible);
    }

    [AvaloniaFact]
    public async Task The_menu_item_is_disabled_with_the_reason_for_an_API_key_account()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        h.Services.RemoteControl.UseAccount(new AuthStatus(true, "api_key", null, null, null, null, null, null));
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        var row = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "RemoteIcon").FindAncestorOfType<Button>()!;
        row.ContextMenu!.Open(row);
        UiText.Settle(window);

        var item = row.ContextMenu.Items.OfType<MenuItem>().Single(m => m.Header as string == "Connect to the Claude app");

        // The command can't run, so the item is disabled.
        Assert.False(item.IsEffectivelyEnabled);
        Assert.Equal("Claude Code is signed in with an API key. Remote Control needs a claude.ai subscription sign-in.", ToolTip.GetTip(item));
        Assert.False(tab.RemoteControl.IsOn);
    }

    [AvaloniaFact]
    public async Task Tab_settings_shows_the_switch_as_it_is_and_why_it_cant_change()
    {
        await using var h = new TabTestHarness(s => s.ClaudeCode.ConnectNewTabsToClaudeApp = true, dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var view = new TabSettingsView { DataContext = new TabSettingsViewModel(h.Services, tab, () => { }) };
        var window = UiText.Show(view);
        var box = view.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Connect to the Claude app");

        Assert.True(box.IsChecked);
        Assert.True(box.IsEnabled);

        // An account that can't use it: a tab that's off can't be turned on, and the reason shows.
        await tab.RemoteControl.SetAsync(false);
        h.Services.RemoteControl.UseAccount(new AuthStatus(true, "api_key", null, null, null, null, null, null));
        view.DataContext = new TabSettingsViewModel(h.Services, tab, () => { });
        UiText.Settle(window);

        Assert.False(box.IsChecked);
        Assert.False(box.IsEnabled);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text == "Claude Code is signed in with an API key. Remote Control needs a claude.ai subscription sign-in.");
    }

    [AvaloniaFact]
    public async Task Settings_has_a_Claude_app_block_in_the_Claude_Code_category()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        using var settings = new SettingsViewModel(h.Services, "me@example.com") { SelectedCategory = "Claude Code" };
        var window = new SettingsWindow { DataContext = settings, Width = 900, Height = 700 };
        window.Show();
        UiText.Settle(window);
        var block = window.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "RemoteControlSettings");
        Assert.True(block.IsEffectivelyVisible);
        var link = block.GetVisualDescendants().OfType<Button>().Single(b => b.Command == settings.ClaudeCode.OpenPushNotificationsDocsCommand);
        link.Command!.Execute(null);
        Assert.Equal([ClaudeCodePage.PushNotificationsDocs], h.Platform.OpenedUrls);

        await Verify(UiText.Describe(block));
    }

    /// <summary>What the menu does with a click on a check item: it ticks or unticks the item, then raises Click.</summary>
    private static void ClickMenuItem(Window window, MenuItem item)
    {
        item.SetCurrentValue(MenuItem.IsCheckedProperty, !item.IsChecked);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        UiText.Settle(window);
    }
}
