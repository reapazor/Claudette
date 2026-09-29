using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.App.Views;
using Claudette.Usage;

namespace Claudette.App.UiTests;

/// <summary>
/// The main window rendered for real, headlessly: the usage header, the sidebar, the conversation and the composer,
/// driven with keyboard and mouse input (DESIGN.md §15, "UI").
/// </summary>
public class MainWindowTests
{
    [AvaloniaFact]
    public async Task The_window_shows_the_usage_header_the_sidebar_and_the_conversation()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        await h.OpenTabAsync();
        h.Transport.EmitTurn("Found **two** problems in `Parser.cs`.");
        await using var tracker = new UsageTracker(h.Services, new UsageStore(Path.Combine(h.Root, "usage.db"), h.Time));
        using var usage = new UsageViewModel(h.Services, tracker);
        tracker.OnRateLimitEvent(RateLimitEvent(0.62, h.Time.GetUtcNow().AddHours(2).AddMinutes(14), 0.38));
        await TabTestHarness.Eventually(() => usage.HasData && h.Shell.SelectedTab!.Status != TabStatus.Working, "the meters and the reply");
        var main = new MainWindowViewModel(h.Services) { CurrentPage = h.Shell, Usage = usage, AccountText = "me@example.com · Max" };

        var window = new MainWindow { DataContext = main, Width = 1200, Height = 800 };
        window.Show();
        UiText.Settle(window);

        await Verify(UiText.Describe(window, (h.Root, "{root}")));
    }

    [AvaloniaFact]
    public async Task Typing_in_the_composer_and_pressing_Enter_sends_the_message()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        var composer = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "Composer");

        composer.Focus();
        window.KeyTextInput("Fix the build");
        UiText.Settle(window);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        UiText.Settle(window);

        await TabTestHarness.Eventually(() => h.Transport.Sent.Any(IsUserMessage("Fix the build")), "the message");
        Assert.True(string.IsNullOrEmpty(composer.Text));
        // Shift+Enter is a new line, not a send.
        window.KeyTextInput("one");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Shift);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.Shift);
        UiText.Settle(window);
        Assert.Single(h.Transport.Sent, m => m["type"]?.GetValue<string>() == "user");
    }

    [AvaloniaFact]
    public async Task A_permission_card_shows_the_command_and_a_click_on_Allow_answers_it()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        tab.ComposerText = "clean up";
        await tab.SendCommand.ExecuteAsync(null);
        var window = UiText.Show(new ShellView { DataContext = h.Shell });

        h.Transport.Emit("""{"type":"control_request","request_id":"p1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"rm -rf build","description":"Delete the build output"}}}""");
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.NeedsInput, "the prompt");
        UiText.Settle(window);
        var conversation = window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "ConversationItems");
        var shown = UiText.Describe(conversation, (h.Root, "{root}"));

        var allow = conversation.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Allow");
        Click(window, allow);

        await TabTestHarness.Eventually(() => h.Transport.Sent.Any(m =>
            m["type"]?.GetValue<string>() == "control_response" && m["response"]?["request_id"]?.GetValue<string>() == "p1"
            && m["response"]?["response"]?["behavior"]?.GetValue<string>() == "allow"), "the answer");
        Assert.NotEqual(TabStatus.NeedsInput, tab.Status);
        // Verify resumes off the UI thread, so it comes last.
        await Verify(shown);
    }

    [AvaloniaFact]
    public async Task The_sidebar_shortcut_collapses_it_to_the_rail()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        var primary = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

        window.KeyPressQwerty(PhysicalKey.B, primary);
        window.KeyReleaseQwerty(PhysicalKey.B, primary);
        UiText.Settle(window);

        Assert.True(h.Shell.IsSidebarCollapsed);
        var sidebar = window.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "Sidebar");
        await Verify(UiText.Describe(sidebar, (h.Root, "{root}")));
    }

    private static void Click(Window window, Control target)
    {
        var center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseMove(center);
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        UiText.Settle(window);
    }

    private static Func<JsonObject, bool> IsUserMessage(string text) => m =>
        m["type"]?.GetValue<string>() == "user" && m.ToJsonString().Contains(text, StringComparison.Ordinal);

    private static Core.Protocol.RateLimitEventMessage RateLimitEvent(double session, DateTimeOffset resetsAt, double weekly)
    {
        var info = new JsonObject
        {
            ["status"] = "allowed",
            ["rateLimitType"] = "five_hour",
            ["unifiedWindows"] = new JsonObject
            {
                ["five_hour"] = new JsonObject { ["utilization"] = session, ["resetsAt"] = resetsAt.ToUnixTimeSeconds() },
                ["seven_day"] = new JsonObject { ["utilization"] = weekly, ["resetsAt"] = resetsAt.AddDays(3).ToUnixTimeSeconds() },
            },
        };
        return new Core.Protocol.RateLimitEventMessage(info, new JsonObject { ["type"] = "rate_limit_event", ["rate_limit_info"] = info.DeepClone() });
    }
}
