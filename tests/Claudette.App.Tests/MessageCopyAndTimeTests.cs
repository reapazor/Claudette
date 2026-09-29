using System.Globalization;
using System.Text.Json.Nodes;
using Claudette.App.Conversation;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>Copy on messages and code blocks, and when each message was sent (DESIGN.md §5, "Copy and times").</summary>
public class MessageCopyAndTimeTests
{
    // The harness's clock starts on Monday 28 September 2026 at 12:00 UTC, and its time zone is UTC.

    [Fact]
    public async Task Live_messages_take_their_time_from_the_clock()
    {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();

        tab.ComposerText = "Fix the build";
        await tab.SendCommand.ExecuteAsync(null);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        h.Transport.EmitTurn("Fixed.");
        await TabTestHarness.Eventually(() => tab.Items.OfType<AssistantTextItem>().Any(), "the reply");

        var prompt = tab.Items.OfType<UserMessageItem>().Single();
        var reply = tab.Items.OfType<AssistantTextItem>().Single();
        Assert.Equal(DateTimeOffset.Parse("2026-09-28T12:00:00Z", CultureInfo.InvariantCulture), prompt.SentAt);
        Assert.Equal("12:00", prompt.TimeText);
        Assert.Equal("Monday, 28 September 2026 12:00", prompt.TimeTip);
        Assert.Equal("12:01", reply.TimeText);
        Assert.True(reply.HasTime);

        // The next day, looking at the tab again gives the time its day.
        h.Time.Advance(TimeSpan.FromDays(1));
        tab.IsSelected = false;
        var changed = new List<string?>();
        prompt.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        tab.IsSelected = true;
        Assert.Contains(nameof(MessageItem.TimeText), changed);
        Assert.Equal("Mon 12:00", prompt.TimeText);
    }

    [Fact]
    public void Times_read_in_the_current_culture_and_the_clocks_time_zone()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.Parse("2026-09-28T12:00:00Z", CultureInfo.InvariantCulture));
        var twelveHour = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        twelveHour.DateTimeFormat.ShortTimePattern = "h:mm tt";
        var today = DateTimeOffset.Parse("2026-09-28T09:05:00Z", CultureInfo.InvariantCulture);

        using (new CultureScope(twelveHour))
        {
            Assert.Equal("9:05 AM", MessageTimes.Short(today, clock));
        }
        using (new CultureScope(CultureInfo.InvariantCulture))
        {
            Assert.Equal("Sun 23:59", MessageTimes.Short(DateTimeOffset.Parse("2026-09-27T23:59:00Z", CultureInfo.InvariantCulture), clock));
            Assert.Equal("Tue 08:00", MessageTimes.Short(DateTimeOffset.Parse("2026-09-22T08:00:00Z", CultureInfo.InvariantCulture), clock));
            // A week or more ago: the date.
            Assert.Equal("09/21/2026 08:00", MessageTimes.Short(DateTimeOffset.Parse("2026-09-21T08:00:00Z", CultureInfo.InvariantCulture), clock));
            // In the clock's time zone: 09:05 UTC is 18:35 in Adelaide, the same day there.
            clock.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(9.5), "Test", "Test"));
            Assert.Equal("18:35", MessageTimes.Short(today, clock));
            Assert.Equal("Monday, 28 September 2026 18:35", MessageTimes.Full(today, clock));
        }
    }

    [Fact]
    public async Task Restored_messages_take_their_times_from_the_transcript()
    {
        using var culture = new CultureScope(CultureInfo.InvariantCulture);
        await using var h = new TabTestHarness();
        h.WriteTranscript("s1",
            Wire.Entry("user", "2026-09-27T09:30:00Z", new JsonObject { ["role"] = "user", ["content"] = "Why is the build red?" }),
            Wire.Entry("assistant", "2026-09-27T09:31:00Z", Wire.Message(new JsonObject { ["type"] = "text", ["text"] = "A missing `using`." })),
            // An entry without a time shows none, rather than the time it was restored.
            new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = "Thanks" }, ["sessionId"] = "s1" }.ToJsonString());
        h.Services.State.Tabs = [new TabState { Folder = h.WorkFolder, IsPinned = true, SessionId = "s1" }];
        h.Shell.Restore(null);
        var tab = h.Shell.AllTabs.Single();

        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Idle && tab.Items.OfType<AssistantTextItem>().Any(), "the restored conversation");

        var prompts = tab.Items.OfType<UserMessageItem>().ToArray();
        Assert.Equal(["Why is the build red?", "Thanks"], prompts.Select(p => p.Text));
        Assert.Equal("Sun 09:30", prompts[0].TimeText);
        Assert.Equal("Sun 09:31", tab.Items.OfType<AssistantTextItem>().Single().TimeText);
        Assert.False(prompts[1].HasTime);
        Assert.Null(prompts[1].TimeText);

        // New messages in the resumed session are live again.
        tab.ComposerText = "And now?";
        await tab.SendCommand.ExecuteAsync(null);
        Assert.Equal("12:00", tab.Items.OfType<UserMessageItem>().Last().TimeText);
    }

    [Fact]
    public async Task Copy_message_puts_the_message_on_the_clipboard_and_says_Copied_for_a_moment()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        tab.AddSuffixCommand.Execute(h.Services.Suffix("clarify"));
        tab.ComposerText = "Add a test";
        await tab.SendCommand.ExecuteAsync(null);
        const string reply = "Run it with:\n\n```bash\ndotnet test\n```\n\nIt **passes**.";
        h.Transport.EmitTurn(reply);
        await TabTestHarness.Eventually(() => tab.Items.OfType<AssistantTextItem>().Any(), "the reply");
        var prompt = tab.Items.OfType<UserMessageItem>().Single();
        var answer = tab.Items.OfType<AssistantTextItem>().Single();

        // A reply copies as Markdown, fences and all.
        await tab.CopyMessageCommand.ExecuteAsync(answer);
        Assert.Equal(reply, h.Platform.Clipboard);
        Assert.True(answer.IsCopied);

        h.Time.Advance(TabViewModel.CopiedFor);
        Assert.False(answer.IsCopied);

        // A user message copies as it was sent, with its quick suffixes.
        await tab.CopyMessageCommand.ExecuteAsync(prompt);
        Assert.Equal("Add a test\n\nAsk clarifying questions before you start.", h.Platform.Clipboard);
        Assert.True(prompt.IsCopied);
    }

    [Fact]
    public async Task Copy_on_a_code_block_copies_the_code_and_says_Copied_for_a_moment()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var block = new object();
        var shown = new List<bool>();

        // What the block shows: the code, without its fences; a final line break isn't part of it.
        await tab.CopyCodeAsync("dotnet build\ndotnet test\n", block, shown.Add);

        Assert.Equal("dotnet build\ndotnet test", h.Platform.Clipboard);
        Assert.Equal([true], shown);

        // Copying again starts the moment over.
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await tab.CopyCodeAsync("dotnet test", block, shown.Add);
        h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([true, true], shown);
        h.Time.Advance(TabViewModel.CopiedFor - TimeSpan.FromSeconds(1));
        Assert.Equal([true, true, false], shown);
        Assert.Equal("dotnet test", h.Platform.Clipboard);
    }
}

/// <summary>Sets the current culture for a test, and puts the previous one back.</summary>
internal sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

    public CultureScope(CultureInfo culture) => CultureInfo.CurrentCulture = culture;

    public void Dispose() => CultureInfo.CurrentCulture = _previous;
}
