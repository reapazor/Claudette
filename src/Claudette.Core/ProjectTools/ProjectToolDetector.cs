namespace Claudette.Core.ProjectTools;

/// <summary>
/// Runs every provider on a tab's folder and picks the project to use (DESIGN.md §18, "Project tools"): the one the
/// user picked for this folder, when there are several, else the nearest.
/// </summary>
public sealed class ProjectToolDetector(IReadOnlyList<IProjectToolProvider> providers)
{
    public IReadOnlyList<IProjectToolProvider> Providers { get; } = providers;

    /// <param name="chosenPath">The project the user picked for this folder, if any.</param>
    public ProjectDetection Detect(string folder, ProjectToolContext context, string? chosenPath = null)
    {
        var candidates = new List<ProjectCandidate>();
        foreach (var provider in Providers)
        {
            try
            {
                candidates.AddRange(provider.Find(folder, context));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A provider that can't look is as good as one that found nothing.
            }
        }
        if (candidates.Count == 0)
        {
            return ProjectDetection.None;
        }
        var comparison = context.OS.PathComparison();
        var chosen = candidates.FirstOrDefault(c => chosenPath is not null && string.Equals(c.Path, chosenPath, comparison)) ?? candidates[0];
        return new ProjectDetection(candidates, Describe(chosen, context));
    }

    /// <summary>Reads one project. A provider that fails still gives a project with the problem, and no actions.</summary>
    public ProjectInfo Describe(ProjectCandidate candidate, ProjectToolContext context)
    {
        var provider = Providers.First(p => p.Kind == candidate.Kind);
        try
        {
            return provider.Describe(candidate, context);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or FormatException)
        {
            return new ProjectInfo
            {
                Kind = candidate.Kind,
                KindName = candidate.Kind,
                Name = candidate.Name,
                Root = Path.GetDirectoryName(candidate.Path) ?? candidate.Path,
                ProjectPath = candidate.Path,
                Problem = $"Couldn't read the project: {ex.Message}",
            };
        }
    }
}
