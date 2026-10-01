using Claudette.App.Conversation;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Installation;
using Claudette.Core.Protocol;

namespace Claudette.App.Tests;

/// <summary>Protocol logging and Settings → Advanced → Diagnostics (DESIGN.md §13, "Logging"; §14; §16).</summary>
public class DiagnosticsTests
{
    [Fact]
    public async Task With_protocol_logging_on_a_tab_logs_and_shows_skipped_messages()
    {
        await using var h = new TabTestHarness(s => s.Advanced.LogProtocol = true);
        var tab = await h.OpenTabAsync();

        var launch = h.Factory.Launches.Single();
        Assert.NotNull(launch.ProtocolLogPath);
        Assert.StartsWith(h.Services.Paths.ProtocolLogDirectory, launch.ProtocolLogPath, StringComparison.Ordinal);

        h.Transport.Emit("""{"type":"hologram","payload":{"shiny":true}}""");

        var item = await EventuallyItemAsync<UnsupportedMessageItem>(tab);
        Assert.Equal("Unsupported message from Claude Code: hologram", item.Title);
        Assert.Contains("\"shiny\": true", item.Json, StringComparison.Ordinal);
        Assert.False(item.IsExpanded);
    }

    [Fact]
    public async Task With_protocol_logging_off_nothing_is_logged_or_shown()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();

        Assert.Null(h.Factory.Launches.Single().ProtocolLogPath);
        h.Transport.Emit("""{"type":"hologram"}""");
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Idle && tab.Items.OfType<TurnSummaryItem>().Any(), "the turn");

        Assert.Empty(tab.Items.OfType<UnsupportedMessageItem>());
    }

    [Fact]
    public async Task Diagnostics_show_the_versions_and_what_was_skipped()
    {
        await using var h = new TabTestHarness();
        h.Services.Diagnostics.RecordUnknownMessage("hologram");
        h.Services.Diagnostics.RecordParseError();
        var settings = new SettingsViewModel(h.Services, null, null) { SelectedCategory = "Advanced" };

        Assert.Equal(ClaudeLocator.MinimumVersion.ToString(), settings.Advanced.MinimumVersionText);
        Assert.Contains("hologram ×1", settings.Advanced.DiagnosticsText, StringComparison.Ordinal);
        Assert.Contains("Lines that couldn't be read: 1", settings.Advanced.DiagnosticsText, StringComparison.Ordinal);

        await settings.Advanced.CopyDiagnosticsCommand.ExecuteAsync(null);

        Assert.StartsWith("Claudette ", h.Platform.Clipboard, StringComparison.Ordinal);
        Assert.Contains($"Minimum supported Claude Code: {ClaudeLocator.MinimumVersion}", h.Platform.Clipboard, StringComparison.Ordinal);
        Assert.Contains("hologram ×1", h.Platform.Clipboard, StringComparison.Ordinal);
        Assert.Single(settings.SearchResultsFor("log protocol"));
    }

    private static async Task<T> EventuallyItemAsync<T>(TabViewModel tab) where T : ConversationItem
    {
        await TabTestHarness.Eventually(() => tab.Items.OfType<T>().Any(), typeof(T).Name);
        return InlineDispatcher.Read(() => tab.Items.OfType<T>().First());
    }
}

internal static class SettingsSearchExtensions
{
    public static IReadOnlyList<SettingsSearchResult> SearchResultsFor(this SettingsViewModel settings, string text)
    {
        settings.SearchText = text;
        return [.. settings.SearchResults];
    }
}
