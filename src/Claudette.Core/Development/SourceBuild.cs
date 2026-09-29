namespace Claudette.Core.Development;

/// <summary>
/// Claudette running from its own build output in a source checkout, for working on Claudette from Claudette
/// (DESIGN.md §9, "Working on Claudette").
/// </summary>
/// <param name="OutputDirectory">The app's build output, such as <c>src/Claudette.App/bin/Debug/net10.0</c>.</param>
/// <param name="RepositoryRoot">The checkout: the folder with <c>Claudette.slnx</c>.</param>
public sealed record SourceBuild(string OutputDirectory, string RepositoryRoot)
{
    public const string SolutionFileName = "Claudette.slnx";

    private const int MaxDepth = 8;

    /// <summary>
    /// The source build <paramref name="appDirectory"/> is part of: a folder inside a <c>bin</c> folder, below a folder
    /// with <c>Claudette.slnx</c>. Null for an installed Claudette.
    /// </summary>
    public static SourceBuild? Detect(string appDirectory)
    {
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory));
        var insideBin = false;
        var folder = output;
        for (var depth = 0; depth < MaxDepth && !string.IsNullOrEmpty(folder); depth++)
        {
            if (string.Equals(Path.GetFileName(folder), "bin", StringComparison.OrdinalIgnoreCase))
            {
                insideBin = true;
            }
            else if (insideBin && File.Exists(Path.Combine(folder, SolutionFileName)))
            {
                return new SourceBuild(output, folder);
            }
            folder = Path.GetDirectoryName(folder);
        }
        return null;
    }
}

/// <summary>A source build of Claudette that's running now, and where its new builds appear.</summary>
/// <param name="SourceOutput">The build output that's watched for new builds.</param>
/// <param name="RunningDirectory">The folder this Claudette runs from: a copy of a build, or the build output itself.</param>
/// <param name="RunningStamp">When the running build was written: only a newer build counts as new.</param>
public sealed record DevelopmentBuild(string SourceOutput, string RunningDirectory, DateTime RunningStamp);
