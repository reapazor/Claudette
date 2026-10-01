using System.Text.Json.Nodes;
using Claudette.App.Conversation;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;

namespace Claudette.App.Tests;

/// <summary>Ultracode and the output style, in the effort dropdown (DESIGN.md §5, "Model and effort").</summary>
public class UltracodeAndStyleTests
{
    private static List<JsonObject> Requests(TabTestHarness h, string subtype) =>
        [.. h.Transport.Sent.Where(m => m["request"]?["subtype"]?.GetValue<string>() == subtype).Select(m => m["request"]!.AsObject())];

    [Fact]
    public async Task Ultracode_is_turned_on_and_off_and_comes_back_when_the_tab_starts_again()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        Assert.False(tab.IsUltracode);

        await tab.ToggleUltracodeCommand.ExecuteAsync(null);

        Assert.True(tab.IsUltracode);
        Assert.True(tab.State.Ultracode);
        Assert.EndsWith(" · Ultracode", tab.EffortName, StringComparison.Ordinal);
        var on = Requests(h, "apply_flag_settings").Last()["settings"]!.AsObject();
        Assert.True(on["ultracode"]!.GetValue<bool>());

        // Kept with the tab: a new Claude Code gets it too.
        await ((IRemoteControlHost)tab).RestartSessionAsync();
        await TabTestHarness.Eventually(() => Requests(h, "apply_flag_settings").Count == 2, "ultracode again");

        await tab.ToggleUltracodeCommand.ExecuteAsync(null);
        var off = Requests(h, "apply_flag_settings").Last()["settings"]!.AsObject();
        Assert.True(off.ContainsKey("ultracode"));
        Assert.Null(off["ultracode"]);
        Assert.False(tab.State.Ultracode);
    }

    [Fact]
    public async Task The_output_style_is_chosen_from_the_sessions_and_kept_for_the_folder()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();

        Assert.Equal(["Default", "Explanatory", "Learning"], tab.OutputStyleChoices.Select(c => c.Label));
        Assert.True(tab.OutputStyleChoices[0].IsCurrent);

        await tab.ChooseOutputStyleCommand.ExecuteAsync(tab.OutputStyleChoices[1]);

        var request = Requests(h, "update_settings").Single();
        Assert.Equal("localSettings", request["source"]!.GetValue<string>());
        Assert.Equal("Explanatory", request["settings"]!["outputStyle"]!.GetValue<string>());
        Assert.True(tab.OutputStyleChoices[1].IsCurrent);
        Assert.Contains(tab.Items.OfType<NoteItem>(), n => n.Text.StartsWith("Output style: Explanatory.", StringComparison.Ordinal));

        // The current one is no change.
        await tab.ChooseOutputStyleCommand.ExecuteAsync(tab.OutputStyleChoices[1]);
        Assert.Single(Requests(h, "update_settings"));
    }

    [Fact]
    public async Task A_tab_without_the_folders_settings_has_no_style_to_keep_there()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        tab.State.WithoutProjectSettings = true;

        await ((IRemoteControlHost)tab).RestartSessionAsync();
        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 2, "the restart");

        Assert.False(tab.HasOutputStyles);
    }
}
