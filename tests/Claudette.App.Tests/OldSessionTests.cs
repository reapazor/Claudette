using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>A restored tab whose transcript is gone from this machine and the library (DESIGN.md §9, "Old sessions").</summary>
public class OldSessionTests
{
    [Fact]
    public async Task A_tab_whose_session_is_gone_says_so_and_offers_a_new_session()
    {
        await using var h = new TabTestHarness();
        Directory.CreateDirectory(h.WorkFolder);
        h.Services.State.Tabs = [new TabState { Folder = h.WorkFolder, IsPinned = true, SessionId = "cleaned-up", UserName = "old work" }];
        h.Shell.Restore(null);
        var tab = h.Shell.AllTabs.Single();

        await tab.EnsureStartedAsync();

        Assert.True(tab.IsSessionMissing);
        Assert.Equal(TabStatus.Error, tab.Status);
        Assert.Equal("Its earlier conversation is gone", tab.RowDetail);
        Assert.False(tab.CanRestart);
        Assert.Empty(h.Factory.Launches);
        // Selecting it again doesn't start one behind the user's back.
        await tab.EnsureStartedAsync();
        Assert.Empty(h.Factory.Launches);

        await tab.StartNewSessionCommand.ExecuteAsync(null);

        Assert.False(tab.IsSessionMissing);
        Assert.Null(h.Factory.Launches.Single().Resume);
        Assert.Equal("old work", tab.DisplayName);
        Assert.True(tab.IsPinned);
        Assert.Equal(TabStatus.Idle, tab.Status);
    }
}
