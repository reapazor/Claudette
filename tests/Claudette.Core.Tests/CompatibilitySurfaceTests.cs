using System.Text.RegularExpressions;
using Claudette.Core.Installation;

namespace Claudette.Core.Tests;

/// <summary>Keeps compat/surface.yaml honest (DESIGN.md §16).</summary>
public partial class CompatibilitySurfaceTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string Surface = File.ReadAllText(Path.Combine(RepoRoot, "compat", "surface.yaml"));

    [Fact]
    public void Versions_match_the_code()
    {
        Assert.Equal(ClaudeLocator.MinimumVersion, Version.Parse(Field("minimum")));
        Assert.Equal(ClaudeLocator.LastTestedVersion, Version.Parse(Field("lastTested")));
    }

    [Fact]
    public void Every_used_in_file_exists()
    {
        var missing = UsedInPattern().Matches(Surface)
            .SelectMany(m => m.Groups[1].Value.Split(','))
            .Select(p => p.Trim())
            .Where(p => p.StartsWith("src/", StringComparison.Ordinal) || p.StartsWith("tests/", StringComparison.Ordinal) || p.StartsWith("tools/", StringComparison.Ordinal))
            .Where(p => !File.Exists(Path.Combine(RepoRoot, p)))
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void Undocumented_entries_have_a_fallback()
    {
        var entries = Surface.Split("\n  - id:").Skip(1);
        var withoutFallback = entries
            .Where(e => StatusPattern().Match(e) is { Success: true } s && s.Groups[1].Value is "undocumented" or "experimental")
            .Where(e => !e.Contains("fallback:", StringComparison.Ordinal))
            .Select(e => e.Split('\n')[0].Trim())
            .ToArray();

        Assert.Empty(withoutFallback);
    }

    private static string Field(string name) =>
        Regex.Match(Surface, $@"^\s*{name}:\s*([0-9.]+)", RegexOptions.Multiline).Groups[1].Value;

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Claudette.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Couldn't find the repository root.");
    }

    [GeneratedRegex(@"^\s*usedIn:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex UsedInPattern();

    [GeneratedRegex(@"^\s*status:\s*(\w+)", RegexOptions.Multiline)]
    private static partial Regex StatusPattern();
}
