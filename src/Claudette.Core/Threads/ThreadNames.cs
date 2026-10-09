namespace Claudette.Core.Threads;

/// <summary>
/// The names a thread's Claude knows its sub-threads by (DESIGN.md §18, "Threads"): their tabs' names, made unique
/// within the thread.
/// </summary>
public static class ThreadNames
{
    /// <summary>
    /// A name for each of <paramref name="tabNames"/>, in order: the tab's own, with <c>(2)</c>, <c>(3)</c> and so on
    /// after one an earlier tab already has, ignoring case.
    /// </summary>
    public static IReadOnlyList<string> Unique(IReadOnlyList<string> tabNames)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(tabNames.Count);
        foreach (var tabName in tabNames)
        {
            var own = string.IsNullOrWhiteSpace(tabName) ? "Tab" : tabName.Trim();
            var name = own;
            for (var n = 2; !taken.Add(name); n++)
            {
                name = $"{own} ({n})";
            }
            names.Add(name);
        }
        return names;
    }

    /// <summary>Which of <paramref name="names"/> <paramref name="recipient"/> means, ignoring case; null for none.</summary>
    public static int? Find(IReadOnlyList<string> names, string recipient)
    {
        for (var i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], recipient, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return null;
    }
}
