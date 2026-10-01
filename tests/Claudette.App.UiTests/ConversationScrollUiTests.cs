using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.App.Views;

namespace Claudette.App.UiTests;

/// <summary>The conversation following new output (DESIGN.md §5).</summary>
public class ConversationScrollUiTests
{
    [AvaloniaFact]
    public async Task Following_the_newest_output_holds_still_rather_than_flickering()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell }, 1100, 800);
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "ConversationScroll");
        var items = window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "ConversationItems");
        double FromBottom() => scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y;
        // Where the newest message's bottom edge is in the view: what the eye follows.
        double? NewestBottom() => items.ContainerFromIndex(items.ItemCount - 1) is { } last && last.TranslatePoint(new Point(0, last.Bounds.Height), scroll) is { } point
            ? Math.Round(point.Y)
            : null;
        // LiveMarkdown parses off the UI thread, so a reply grows once its text is in: only then is there nothing left to
        // move it. Before that, a reply that fits in the view grows downwards, which is rendering, not flicker.
        bool Rendered(string text, int times) =>
            items.GetRealizedContainers().Any(c => UiText.Describe(c).Split(text).Length - 1 >= times);
        // The offset itself can change while what's in view doesn't: the list corrects its estimate of the messages
        // above, which nobody sees. What's in view mustn't move.
        void AssertStill(string when)
        {
            var newest = NewestBottom();
            Assert.NotNull(newest);
            for (var frame = 0; frame < 6; frame++)
            {
                UiText.Settle(window);
                Assert.True(NewestBottom() is { } now && Math.Abs(now - newest.Value) <= 1 && FromBottom() <= 1, $"{when}: moved on its own, frame {frame} (newest at {newest} → {NewestBottom()}, {FromBottom():0} from the bottom)");
            }
        }

        // Replies of different heights, just past the height of the view and then well past it.
        for (var i = 0; i < 12; i++)
        {
            tab.ComposerText = $"question {i}";
            await tab.SendCommand.ExecuteAsync(null);
            h.Transport.EmitTurn(string.Join("\n\n", Enumerable.Repeat($"Reply {i}: some text that wraps over a line or two in the conversation view.", 1 + (i % 5))));
            await UiText.SettleUntilAsync(window, () => tab.Status == TabStatus.Idle && tab.IsSettled && Rendered($"Reply {i}:", 1 + (i % 5)) && FromBottom() <= 1, $"turn {i} at the bottom");
            AssertStill($"after turn {i}");
        }

        tab.ComposerText = "one more";
        await tab.SendCommand.ExecuteAsync(null);
        for (var i = 0; i < 20; i++)
        {
            h.Transport.Emit(new JsonObject
            {
                ["type"] = "stream_event",
                ["event"] = new JsonObject
                {
                    ["type"] = "content_block_delta",
                    ["index"] = 0,
                    ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = $"Streaming piece {i} with enough words to wrap now and then. " },
                },
            });
            await UiText.SettleUntilAsync(window, () => tab.IsSettled && Rendered($"Streaming piece {i} ", 1) && FromBottom() <= 1, $"piece {i} at the bottom");
            AssertStill($"after piece {i}");
        }
    }

    private static Point Center(Window window, ScrollViewer scroll) =>
        scroll.TranslatePoint(new Point(scroll.Bounds.Width / 2, scroll.Bounds.Height / 2), window)!.Value;

    /// <summary>Scrolls up by about <paramref name="distance"/> with the mouse wheel, as a reader does: a notch a frame.</summary>
    private static void WheelUp(Window window, ScrollViewer scroll, double distance)
    {
        var target = Math.Max(0, scroll.Offset.Y - distance);
        var center = Center(window, scroll);
        for (var notch = 0; notch < 200 && scroll.Offset.Y > target; notch++)
        {
            window.MouseWheel(center, new Vector(0, 1));
            UiText.Settle(window);
        }
    }

    /// <summary>
    /// Scrolling up in the same moment new output arrives (or the list corrects its estimate of what's above) leaves
    /// the reader where they went, rather than taking them back to the bottom.
    /// </summary>
    [AvaloniaFact]
    public async Task Scrolling_up_as_the_conversation_grows_stays_where_the_reader_went()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell }, 1100, 800);
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "ConversationScroll");
        double FromBottom() => scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y;
        for (var i = 0; i < 10; i++)
        {
            tab.ComposerText = $"question {i}";
            await tab.SendCommand.ExecuteAsync(null);
            h.Transport.EmitTurn(string.Join("\n\n", Enumerable.Repeat($"Reply {i}: some text that wraps over a line or two in the conversation view.", 1 + (i % 5))));
            await UiText.SettleUntilAsync(window, () => tab.Status == TabStatus.Idle && tab.IsSettled && FromBottom() <= 1, $"turn {i} at the bottom");
        }

        // New output, then a notch of the wheel before the next layout pass: the wheel's pass sees the move and the
        // growth together.
        tab.Items.Add(new NoteItem("Arrived as the reader scrolled up.", NoteKind.Info));
        window.MouseWheel(Center(window, scroll), new Vector(0, 1));
        UiText.Settle(window);

        Assert.True(FromBottom() > 40, $"back at the bottom ({FromBottom():0} from it)");
        // Output that comes later leaves them there too.
        tab.Items.Add(new NoteItem("And more.", NoteKind.Info));
        UiText.Settle(window);
        Assert.True(FromBottom() > 40, $"back at the bottom after more output ({FromBottom():0} from it)");
    }

    [AvaloniaFact]
    public async Task Scrolling_back_through_earlier_replies_holds_still_too()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell }, 1100, 800);
        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "ConversationScroll");
        var items = window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "ConversationItems");
        for (var i = 0; i < 25; i++)
        {
            tab.ComposerText = $"question {i}";
            await tab.SendCommand.ExecuteAsync(null);
            h.Transport.EmitTurn(string.Join("\n\n", Enumerable.Repeat($"Reply {i}: some text that wraps over a line or two in the conversation view.", 1 + (i % 5))));
            await UiText.SettleUntilAsync(window, () => tab.Status == TabStatus.Idle && tab.IsSettled, $"turn {i}");
        }

        // A screen at a time back up, as a reader would: what's at the top of the view stays put once it's there.
        for (var page = 0; page < 8 && scroll.Offset.Y > 0; page++)
        {
            WheelUp(window, scroll, scroll.Viewport.Height);
            var top = items.GetRealizedContainers().OrderBy(c => c.TranslatePoint(default, scroll)!.Value.Y).First(c => c.TranslatePoint(new Point(0, c.Bounds.Height), scroll)!.Value.Y > 0);
            var (index, y) = (items.IndexFromContainer(top), top.TranslatePoint(default, scroll)!.Value.Y);
            for (var frame = 0; frame < 6; frame++)
            {
                UiText.Settle(window);
                var same = items.ContainerFromIndex(index);
                Assert.True(same is not null && Math.Abs(same.TranslatePoint(default, scroll)!.Value.Y - y) <= 1,
                    $"page {page}: message {index} moved on its own, frame {frame} ({y:0} → {same?.TranslatePoint(default, scroll)?.Y:0})");
            }
        }
    }
}
