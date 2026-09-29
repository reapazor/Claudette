namespace Claudette.Core.Perforce;

/// <summary>
/// Reads <c>p4 -ztag</c> output: records of <c>... name value</c> lines, separated by blank lines. Tolerant: lines it
/// doesn't recognize are ignored, and a line that doesn't start a field continues the previous field's value.
/// </summary>
public static class ZTag
{
    private const string FieldPrefix = "... ";

    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Parse(string output)
    {
        var records = new List<IReadOnlyDictionary<string, string>>();
        Dictionary<string, string>? current = null;
        string? lastField = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                Close();
                continue;
            }
            if (line.StartsWith(FieldPrefix, StringComparison.Ordinal))
            {
                var field = line[FieldPrefix.Length..];
                var space = field.IndexOf(' ');
                var name = space < 0 ? field : field[..space];
                if (name.Length == 0)
                {
                    continue;
                }
                current ??= new Dictionary<string, string>(StringComparer.Ordinal);
                current[name] = space < 0 ? "" : field[(space + 1)..];
                lastField = name;
            }
            else if (current is not null && lastField is not null)
            {
                // A multi-line value, such as a changelist description.
                current[lastField] += "\n" + line;
            }
        }
        Close();
        return records;

        void Close()
        {
            if (current is { Count: > 0 })
            {
                records.Add(current);
            }
            current = null;
            lastField = null;
        }
    }

    /// <summary>The first record's fields, or an empty record.</summary>
    public static IReadOnlyDictionary<string, string> ParseSingle(string output) =>
        Parse(output) is { Count: > 0 } records ? records[0] : new Dictionary<string, string>(StringComparer.Ordinal);
}
