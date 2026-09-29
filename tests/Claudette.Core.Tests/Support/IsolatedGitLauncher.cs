using System.Collections;
using Claudette.Core.Diffs;
using Claudette.Core.Processes;

namespace Claudette.Core.Tests.Support;

/// <summary>
/// Starts git with no global or system config, no inherited <c>GIT_*</c> variables, and no looking for a repository
/// above the test's folder.
/// </summary>
internal sealed class IsolatedGitLauncher : IProcessLauncher
{
    public static readonly bool GitInstalled = FileProbe.Instance.FindOnPath(OperatingSystem.IsWindows() ? "git.exe" : "git") is not null;

    private readonly ProcessLauncher _inner = new();
    private readonly Dictionary<string, string> _environment;

    public IsolatedGitLauncher(string root)
    {
        var globalConfig = Path.Combine(root, "empty.gitconfig");
        File.WriteAllText(globalConfig, "");
        _environment = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value && !key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
            {
                _environment[key] = value;
            }
        }
        _environment["GIT_CONFIG_GLOBAL"] = globalConfig;
        _environment["GIT_CONFIG_NOSYSTEM"] = "1";
        _environment["GIT_CEILING_DIRECTORIES"] = root;
    }

    public IRunningProcess Start(ProcessStartSpec spec) => _inner.Start(spec with { Environment = _environment });

    /// <summary>Runs git in <paramref name="folder"/> and fails the test if it fails.</summary>
    public async Task RunAsync(string folder, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync(
            this, new ProcessStartSpec("git", arguments) { WorkingDirectory = folder }, TimeSpan.FromSeconds(30), TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
    }
}
