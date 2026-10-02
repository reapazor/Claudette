using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.App.Views;
using Claudette.Core.Settings;

namespace Claudette.App.UiTests;

/// <summary>
/// The composer's large pastes and the stash (DESIGN.md §5, "Attachments", "Drafts and the stash"), long messages shown
/// short, a turn's files on its footer (DESIGN.md §8, "Changes per turn"), and Ctrl/Cmd+Enter as the send key (DESIGN.md
/// §14), rendered.
/// </summary>
public class ComposerExtrasUiTests
{
    private static readonly RawInputModifiers Primary = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static readonly string BigLog = string.Join("\n", Enumerable.Range(1, 2000).Select(i => $"log line {i:D5} with something in it"));

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public async Task A_large_paste_is_a_chip_and_its_message_shows_its_start()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });

        tab.ComposerText = "why does this fail?";
        tab.AddPastedText(BigLog);
        UiText.Settle(window);

        var chips = Named<ItemsControl>(window, "PastedTextList");
        Assert.True(chips.IsEffectivelyVisible);
        var shown = UiText.Describe(chips);
        Assert.Contains($"Pasted text · {2000:N0} lines", shown, StringComparison.Ordinal);
        Assert.Contains(chips.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b) == "Remove pasted text");

        await tab.SendCommand.ExecuteAsync(null);
        await UiText.SettleUntilAsync(window, () => tab.Items.OfType<UserMessageItem>().Any(), "the message");
        UiText.Settle(window);
        Assert.False(chips.IsEffectivelyVisible);
        var card = tab.Items.OfType<UserMessageItem>().Last();
        var showAll = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, card.ShowAllCommand));
        Assert.True(showAll.IsEffectivelyVisible);
        Assert.Contains("Show all (", UiText.Describe(showAll), StringComparison.Ordinal);

        showAll.Command!.Execute(null);
        UiText.Settle(window);
        Assert.False(showAll.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task The_stash_button_shows_while_the_stash_has_something_and_its_menu_puts_one_back()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        var button = Named<Button>(window, "StashButton");
        Assert.False(button.IsEffectivelyVisible);

        tab.ComposerText = "for later";
        var composer = Named<TextBox>(window, "Composer");
        composer.Focus();
        window.KeyPressQwerty(PhysicalKey.S, Primary);
        window.KeyReleaseQwerty(PhysicalKey.S, Primary);
        UiText.Settle(window);

        Assert.Equal("", tab.ComposerText);
        Assert.True(button.IsEffectivelyVisible);
        Assert.Contains("Stashed (1) ▾", UiText.Describe(button), StringComparison.Ordinal);
        var flyout = Assert.IsType<Flyout>(button.Flyout);
        flyout.ShowAt(button);
        UiText.Settle(window);
        var menu = Assert.IsAssignableFrom<Control>(flyout.Content);
        await UiText.SettleUntilAsync(window, () => UiText.Describe(menu).Contains("\"for later\"", StringComparison.Ordinal), "the entry");
        Assert.Contains(menu.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b) == "Delete from the stash");

        var entry = menu.GetVisualDescendants().OfType<Button>().First(b => b.DataContext is StashEntry && ReferenceEquals(b.Command, tab.UnstashCommand));
        entry.Command!.Execute(entry.CommandParameter);
        entry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        UiText.Settle(window);

        Assert.Equal("for later", tab.ComposerText);
        Assert.False(button.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task A_turns_footer_lists_its_files()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        var path = Path.Combine(h.WorkFolder, "auth.cs");
        tab.ComposerText = "change it";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit(new JsonObject { ["type"] = "system", ["subtype"] = "init", ["session_id"] = "s1", ["model"] = "claude-opus-5-5" });
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = "e1", ["name"] = "Edit", ["input"] = new JsonObject { ["file_path"] = path } }) },
        });
        await File.WriteAllTextAsync(path, "one\ntwo\n", TestContext.Current.CancellationToken);
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "e1", ["content"] = "The file has been updated." }) },
            ["tool_use_result"] = new JsonObject { ["filePath"] = path, ["oldString"] = "a", ["originalFile"] = "one\n", ["structuredPatch"] = new JsonArray() },
        });
        h.Transport.Emit("""{"type":"result","subtype":"success","is_error":false,"session_id":"s1","duration_ms":1200}""");
        await UiText.SettleUntilAsync(window, () => tab.Items.LastOrDefault() is TurnSummaryItem { HasFiles: true } t && t.Files[0].Stats is not null, "the footer");
        UiText.Settle(window);

        var files = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("turnfiles"));
        Assert.True(files.IsEffectivelyVisible);
        Assert.Contains("· 1 file changed", UiText.Describe(files), StringComparison.Ordinal);
        var flyout = Assert.IsType<Flyout>(files.Flyout);
        flyout.ShowAt(files);
        UiText.Settle(window);
        var list = Assert.IsAssignableFrom<Control>(flyout.Content);
        await UiText.SettleUntilAsync(window, () => UiText.Describe(list).Contains("\"+1 −0\"", StringComparison.Ordinal), "the file's counts");
        Assert.Contains("\"auth.cs\"", UiText.Describe(list), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task With_Ctrl_Enter_as_the_send_key_Enter_starts_a_new_line()
    {
        await using var h = new TabTestHarness(s => s.Keyboard.SendKey = SendKey.PrimaryEnter, dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        var composer = Named<TextBox>(window, "Composer");
        composer.Focus();
        Assert.Contains("Enter for a new line", composer.PlaceholderText, StringComparison.Ordinal);

        window.KeyTextInput("first line");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyTextInput("second line");
        UiText.Settle(window);
        Assert.DoesNotContain(tab.Items, i => i is UserMessageItem);
        Assert.Contains('\n', tab.ComposerText);

        window.KeyPressQwerty(PhysicalKey.Enter, Primary);
        window.KeyReleaseQwerty(PhysicalKey.Enter, Primary);
        await UiText.SettleUntilAsync(window, () => tab.Items.OfType<UserMessageItem>().Any(), "the message");

        Assert.Equal("first line\nsecond line", tab.Items.OfType<UserMessageItem>().Single().Text.ReplaceLineEndings("\n"));
        Assert.Equal("", tab.ComposerText);
    }
}
