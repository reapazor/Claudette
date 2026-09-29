using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Claudette.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Claudette.Platform.Processes.Unix;

/// <summary>
/// A process tree on macOS (DESIGN.md §4, Process monitor). Each scan runs <c>ps</c> once for every process on the
/// system, which gives parent PIDs, resident size, CPU time, start time and command lines in one go. CPU % follows
/// Activity Monitor: 100% is one core.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacProcessTree(int rootPid, IProcessLauncher launcher, TimeProvider time, ILogger logger)
    : UnixProcessTree<PsEntry>(rootPid, time, logger)
{
    private const string PsPath = "/bin/ps";
    private static readonly TimeSpan PsTimeout = TimeSpan.FromSeconds(5);

    private readonly Lazy<IReadOnlyDictionary<string, string>> _environment = new(CEnvironment);

    protected override int SigStop => 17;

    protected override int SigCont => 19;

    protected override Dictionary<int, PsEntry>? ScanAll()
    {
        try
        {
            // Samples run on a background thread, so waiting here is fine.
            var result = ProcessRunner.RunAsync(
                    launcher,
                    new ProcessStartSpec(PsPath, PsOutput.Arguments) { Environment = _environment.Value },
                    PsTimeout,
                    Time)
                .GetAwaiter()
                .GetResult();
            if (result.ExitCode != 0)
            {
                Logger.LogWarning("ps exited with {ExitCode}: {Error}", result.ExitCode, result.StandardError.Trim());
                return null;
            }
            var all = new Dictionary<int, PsEntry>();
            foreach (var entry in PsOutput.Parse(result.StandardOutput))
            {
                all[entry.Pid] = entry;
            }
            return all;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Logger.LogWarning(ex, "Couldn't run ps.");
            return null;
        }
    }

    protected override RawProcess? Describe(PsEntry entry, bool includeCommandLines, bool isRoot, bool isDetached)
    {
        var program = entry.Program;
        return new RawProcess(
            entry.Pid,
            entry.ParentPid,
            entry.StartKey,
            program.Length > 0 ? Path.GetFileName(program) : $"PID {entry.Pid}",
            program.StartsWith('/') ? program : null,
            includeCommandLines ? entry.Args : null,
            entry.CpuTime,
            entry.RssKilobytes * 1024,
            entry.StartTime,
            isRoot,
            isDetached);
    }

    /// <summary>kill(pid, 0): the process exists (EPERM means it exists but isn't ours).</summary>
    protected override bool IsAlive(int pid, long startKey) =>
        LibC.Kill(pid, 0) == 0 || Marshal.GetLastPInvokeError() == LibC.Eperm;

    /// <summary>This process's environment with the C locale, so <c>lstart</c> has English month names.</summary>
    private static IReadOnlyDictionary<string, string> CEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is string key && variable.Value is string value)
            {
                environment[key] = value;
            }
        }
        environment["LC_ALL"] = "C";
        return environment;
    }
}
