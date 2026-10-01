namespace Claudette.App.Views;

/// <summary>
/// Whether the conversation follows new output (DESIGN.md §5), from each change to its scroll
/// viewer: what the reader did and what the layout did can arrive in the same change.
/// </summary>
internal static class FollowOutput
{
    /// <summary>How near the bottom still counts as at it.</summary>
    public const double Threshold = 40;

    /// <param name="following">Following before this change.</param>
    /// <param name="readerScrollingUp">The reader is scrolling up (the wheel, a scroll bar, a key, a swipe).</param>
    /// <param name="offsetDelta">How far the view moved; negative is up.</param>
    /// <param name="extentDelta">How much the content's height changed.</param>
    /// <param name="fromBottom">How far the view is from the bottom now.</param>
    /// <returns>Following after it, and whether to scroll to the end now.</returns>
    public static (bool Following, bool ScrollToEnd) Update(bool following, bool readerScrollingUp, double offsetDelta, double extentDelta, double fromBottom)
    {
        // The reader moving up leaves the bottom, even when new output arrived in the same layout pass. Otherwise a move
        // that comes with growth is the list keeping its place (it corrects its estimate of the messages above), and
        // only a move on its own says where the view is.
        if (readerScrollingUp && offsetDelta < 0 || offsetDelta != 0 && extentDelta == 0)
        {
            following = fromBottom <= Threshold;
        }
        return (following, extentDelta != 0 && following);
    }
}
