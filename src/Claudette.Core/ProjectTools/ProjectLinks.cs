using System.Text.RegularExpressions;

namespace Claudette.Core.ProjectTools;

/// <summary>What a link's placeholders are filled with, from the selected tab.</summary>
/// <param name="Branch">The git branch; null outside a repository or on a detached HEAD.</param>
/// <param name="Changelist">The Perforce changelist Claude is working in, when there is one.</param>
public sealed record LinkValues(string? Branch, string? Changelist, string FolderName);

/// <summary>A link ready to show: its address, or why it can't be opened.</summary>
public sealed record ResolvedLink(string Name, string? Url, string? Problem, ProjectFileScope Scope)
{
    public bool IsEnabled => Url is not null;

    /// <summary>The tooltip: the address it opens, or why it can't.</summary>
    public string Tip => Url ?? Problem ?? "";
}

/// <summary>
/// Fills in and checks the links of a folder's project files (DESIGN.md §18, "Links"). Placeholders are
/// <c>{branch}</c>, <c>{changelist}</c> and <c>{folderName}</c>, URL-escaped. Only <c>https</c>, <c>http</c> and
/// <c>mailto</c> links open; anything else (<c>file:</c>, <c>javascript:</c>, an app's own scheme) is shown disabled.
/// </summary>
public static partial class ProjectLinks
{
    public static readonly IReadOnlySet<string> AllowedSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "https", "http", "mailto" };

    /// <summary>The placeholders a link can use, for hints: <c>{branch}</c>, <c>{changelist}</c> and <c>{folderName}</c>.</summary>
    public const string PlaceholderHint = "{branch}, {changelist} and {folderName} are filled in from the tab: its git branch, its Perforce changelist and its folder's name.";

    /// <summary>Stand-ins for the placeholders when checking a link that's being edited, whatever tab it's for.</summary>
    private static readonly LinkValues SampleValues = new("main", "12345", "folder");

    public static ResolvedLink Resolve(ProjectLink link, LinkValues values) =>
        Fill(link.Url.Trim(), values, showFilled: true, out var filled) is { } problem
            ? new ResolvedLink(link.Name, null, problem, link.Scope)
            : new ResolvedLink(link.Name, filled, null, link.Scope);

    /// <summary>
    /// Why an address typed into Settings' Links page can't be saved, by the rules the project's menu opens links with: only
    /// https, http and mailto, a full address, and only the known placeholders. Null when it's fine.
    /// </summary>
    public static string? Validate(string url)
    {
        var template = url.Trim();
        return template.Length == 0
            ? "Type the address to open, such as https://example.com."
            : Fill(template, SampleValues, showFilled: false, out _);
    }

    /// <summary>Fills in a link's placeholders; returns why it can't open, or null.</summary>
    /// <param name="showFilled">Name the filled-in address in a problem, rather than the one as written.</param>
    private static string? Fill(string template, LinkValues values, bool showFilled, out string filled)
    {
        filled = template;
        if (SchemePattern().Match(template) is { Success: true } scheme && !AllowedSchemes.Contains(scheme.Groups[1].Value))
        {
            return NotAllowed(scheme.Groups[1].Value);
        }
        string? missing = null;
        filled = Placeholder().Replace(template, match =>
        {
            var value = match.Groups[1].Value.ToLowerInvariant() switch
            {
                "branch" => values.Branch ?? Missing("No git branch"),
                "changelist" => values.Changelist ?? Missing("No Perforce changelist"),
                "foldername" => values.FolderName,
                _ => Missing($"Unknown placeholder {match.Value}: use {{branch}}, {{changelist}} or {{folderName}}"),
            };
            return value is null ? match.Value : Uri.EscapeDataString(value);
        });
        if (missing is not null)
        {
            return missing;
        }
        if (!Uri.TryCreate(filled, UriKind.Absolute, out var uri))
        {
            return $"{(showFilled ? filled : template)} isn't a full web address, such as https://example.com.";
        }
        if (!AllowedSchemes.Contains(uri.Scheme))
        {
            return NotAllowed(uri.Scheme);
        }
        return null;

        string? Missing(string reason)
        {
            missing ??= reason;
            return null;
        }
    }

    private static string NotAllowed(string scheme) => $"\"{scheme}:\" links don't open from Claudette; only https, http and mailto do.";

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9+.\-]*):")]
    private static partial Regex SchemePattern();
}
