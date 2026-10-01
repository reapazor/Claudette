using Claudette.App.Views;

namespace Claudette.App.Tests;

/// <summary>Whether the conversation follows new output (DESIGN.md §5), change by change.</summary>
public class FollowOutputTests
{
    [Fact]
    public void Output_arriving_at_the_bottom_is_followed()
    {
        Assert.Equal((true, true), FollowOutput.Update(following: true, readerScrollingUp: false, offsetDelta: 0, extentDelta: 120, fromBottom: 120));
    }

    [Fact]
    public void Scrolling_up_as_output_arrives_leaves_the_bottom()
    {
        // The reader moved up a screen in the same layout pass as a reply grew.
        Assert.Equal((false, false), FollowOutput.Update(following: true, readerScrollingUp: true, offsetDelta: -600, extentDelta: 80, fromBottom: 680));
    }

    [Fact]
    public void The_list_correcting_its_estimate_while_following_keeps_following()
    {
        // Not the reader: the list moved the view up to keep its place as its estimate of the messages above changed,
        // and a new reply grew below.
        Assert.Equal((true, true), FollowOutput.Update(following: true, readerScrollingUp: false, offsetDelta: -30, extentDelta: 150, fromBottom: 180));
    }

    [Fact]
    public void A_move_on_its_own_says_where_the_view_is()
    {
        Assert.Equal((false, false), FollowOutput.Update(following: true, readerScrollingUp: false, offsetDelta: -300, extentDelta: 0, fromBottom: 300));
        Assert.Equal((true, false), FollowOutput.Update(following: false, readerScrollingUp: false, offsetDelta: 300, extentDelta: 0, fromBottom: 10));
    }

    [Fact]
    public void Output_arriving_while_scrolled_up_leaves_the_reader_there()
    {
        Assert.Equal((false, false), FollowOutput.Update(following: false, readerScrollingUp: false, offsetDelta: 0, extentDelta: 200, fromBottom: 900));
    }
}
