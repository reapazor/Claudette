using Claudette.Core.Library;

namespace Claudette.Core.Settings;

/// <summary>
/// An icon the user puts on a tab to mean something to them (DESIGN.md §4, "Marks"). Claudette gives it no meaning.
/// </summary>
public enum TabMark
{
    Check,
    Cross,
    Question,
    Star,
    Flag,
    Pause,
}

/// <summary>
/// How a <see cref="TabMark"/> is stored: as a word, in the tab's state, this machine's state and the session
/// library's record. A word a later Claudette adds reads as no mark here, rather than failing the whole file as an
/// unknown enum value would.
/// </summary>
public static class TabMarks
{
    /// <summary>Every mark, in the order the tab's menu lists them.</summary>
    public static IReadOnlyList<TabMark> All { get; } = Enum.GetValues<TabMark>();

    public static string Key(TabMark mark) => mark switch
    {
        TabMark.Check => "check",
        TabMark.Cross => "cross",
        TabMark.Question => "question",
        TabMark.Star => "star",
        TabMark.Flag => "flag",
        TabMark.Pause => "pause",
        _ => throw new ArgumentOutOfRangeException(nameof(mark), mark, null),
    };

    /// <summary>The mark a stored word names; null for none, or for a word this Claudette doesn't know.</summary>
    public static TabMark? Parse(string? key)
    {
        foreach (var mark in All)
        {
            if (string.Equals(Key(mark), key, StringComparison.OrdinalIgnoreCase))
            {
                return mark;
            }
        }
        return null;
    }

    /// <summary>
    /// A past session's mark, as stored: whichever was written last of its library record's and this machine's. The
    /// record has the latest from every machine that syncs it, but a tab that stopped syncing never writes it again, so
    /// a mark set here since wins.
    /// </summary>
    public static string? ForSession(SessionRecord? record, SessionMark? local) =>
        local is not null && (record is null || local.At > record.LastUsed) ? local.Mark : record?.Mark;
}

/// <summary>A session's mark on this machine, and when it was set (DESIGN.md §4, "Marks").</summary>
public sealed class SessionMark
{
    /// <summary>
    /// As <see cref="TabMarks.Key"/> writes it; null once the mark was cleared here, so an older library record's mark
    /// doesn't come back.
    /// </summary>
    public string? Mark { get; set; }

    public DateTimeOffset At { get; set; }
}
