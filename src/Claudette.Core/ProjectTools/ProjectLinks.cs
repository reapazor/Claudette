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

    public static ResolvedLink Resolve(ProjectLink link, LinkValues values)
    {
        var template = link.Url.Trim();
        if (SchemePattern().Match(template) is { Success: true } scheme && !AllowedSchemes.Contains(scheme.Groups[1].Value))
        {
            return Blocked($"\"{scheme.Groups[1].Value}:\" links don't open from Claudette; only https, http and mailto do.");
        }
        string? missing = null;
        var filled = Placeholder().Replace(template, match =>
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
            return Blocked(missing);
        }
        if (!Uri.TryCreate(filled, UriKind.Absolute, out var uri))
        {
            return Blocked($"{filled} isn't a full web address, such as https://example.com.");
        }
        if (!AllowedSchemes.Contains(uri.Scheme))
        {
            return Blocked($"\"{uri.Scheme}:\" links don't open from Claudette; only https, http and mailto do.");
        }
        return new ResolvedLink(link.Name, filled, null, link.Scope);

        string? Missing(string reason)
        {
            missing ??= reason;
            return null;
        }

        ResolvedLink Blocked(string reason) => new(link.Name, null, reason, link.Scope);
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9+.\-]*):")]
    private static partial Regex SchemePattern();
}
