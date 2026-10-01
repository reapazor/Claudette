using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;

namespace Claudette.App.Tests;

/// <summary>Accessibility (DESIGN.md §3, "Accessibility"): what screen readers are told.</summary>
public class AccessibilityTests
{
    [Fact]
    public async Task Prompts_finished_turns_and_errors_are_announced()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();

        h.Transport.Emit("""{"type":"control_request","request_id":"p1","request":{"subtype":"can_use_tool","tool_name":"Bash","tool_use_id":"t1","input":{"command":"npm test"}}}""");
        await TabTestHarness.Eventually(() => h.Shell.Announcement.Length > 0, "the prompt");
        Assert.Equal("work: Allow this command? npm test", h.Shell.Announcement);

        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => h.Shell.Announcement == "work: Claude finished.", "the turn");

        // The same words again still change the live region, so they're read again.
        h.Shell.Announce("work: Claude finished.");
        Assert.NotEqual("work: Claude finished.", h.Shell.Announcement);
        Assert.StartsWith("work: Claude finished.", h.Shell.Announcement, StringComparison.Ordinal);
    }
}
