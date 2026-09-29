using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Claudette.Core.Processes;
using Claudette.Core.ProjectTools;
using Claudette.Platform.Processes.Unix;
using Claudette.Platform.Processes.Windows;
using Microsoft.Win32.SafeHandles;
using static Claudette.Platform.Processes.Windows.WindowsNative;

namespace Claudette.Platform.Processes;

/// <summary>
/// Every running process by name (DESIGN.md §18, "Project tools"), with the process monitor's own ways of reading them:
/// a Toolhelp snapshot and <c>NtQueryInformationProcess</c> on Windows, <c>/proc</c> on Linux, and one <c>ps</c> run
/// on macOS. Killing a tree uses .NET's <see cref="Process.Kill(bool)"/>, which finds the children on each OS.
/// </summary>
public sealed class SystemProcesses : ISystemProcesses
{
    private static readonly TimeSpan PsTimeout = TimeSpan.FromSeconds(5);

    private readonly IProcessLauncher _launcher;
    private readonly TimeProvider _time;
    private readonly int _self = Environment.ProcessId;

    /// <param name="launcher">Runs <c>ps</c> on macOS.</param>
    public SystemProcesses(IProcessLauncher launcher, TimeProvider time)
    {
        _launcher = launcher;
        _time = time;
    }

    public IReadOnlyList<SystemProcess> Find(IReadOnlyCollection<string> names)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return FindOnWindows(names);
            }
            if (OperatingSystem.IsLinux())
            {
                return FindOnLinux(names);
            }
            if (OperatingSystem.IsMacOS())
            {
                return FindOnMac(names);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or TimeoutException)
        {
            // Can't tell: say none, rather than guess.
        }
        return [];
    }

    public void KillTree(int pid)
    {
        if (pid == _self)
        {
            return;
        }
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already exited, or not ours to end.
        }
    }

    [SupportedOSPlatform("windows")]
    private unsafe List<SystemProcess> FindOnWindows(IReadOnlyCollection<string> names)
    {
        var found = new List<SystemProcess>();
        using var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot.IsInvalid)
        {
            return found;
        }
        var entry = new ProcessEntry32 { Size = (uint)sizeof(ProcessEntry32) };
        for (var more = Process32First(snapshot, &entry); more; more = Process32Next(snapshot, &entry))
        {
            var pid = (int)entry.ProcessId;
            var name = new string(entry.ExeFile);
            if (pid == _self || pid == 0 || !SystemProcessNames.Matches(name, names))
            {
                continue;
            }
            string? commandLine = null;
            using (SafeProcessHandle handle = OpenProcess(ProcessQueryLimitedInformation, false, pid))
            {
                if (!handle.IsInvalid)
                {
                    commandLine = WindowsProcessTree.CommandLine(handle);
                }
            }
            found.Add(new SystemProcess(pid, Bare(name), commandLine));
        }
        return found;
    }

    [SupportedOSPlatform("linux")]
    private List<SystemProcess> FindOnLinux(IReadOnlyCollection<string> names)
    {
        var found = new List<SystemProcess>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid == _self)
            {
                continue;
            }
            var arguments = ReadArguments($"/proc/{pid}/cmdline");
            var executable = ReadLink($"/proc/{pid}/exe");
            if (executable?.EndsWith(" (deleted)", StringComparison.Ordinal) == true)
            {
                executable = executable[..^" (deleted)".Length];
            }
            var name = executable is not null ? Path.GetFileName(executable)
                : arguments.Count > 0 ? Path.GetFileName(arguments[0])
                : ReadComm(pid);
            if (string.IsNullOrEmpty(name) || !SystemProcessNames.Matches(name, names))
            {
                continue;
            }
            found.Add(new SystemProcess(pid, name, arguments.Count > 0 ? string.Join(' ', arguments) : null));
        }
        return found;
    }

    [SupportedOSPlatform("macos")]
    private List<SystemProcess> FindOnMac(IReadOnlyCollection<string> names)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is string key && variable.Value is string value)
            {
                environment[key] = value;
            }
        }
        environment["LC_ALL"] = "C";
        var result = ProcessRunner.RunAsync(_launcher, new ProcessStartSpec("/bin/ps", PsOutput.Arguments) { Environment = environment }, PsTimeout, _time)
            .GetAwaiter()
            .GetResult();
        return result.ExitCode == 0 ? MatchPs(PsOutput.Parse(result.StandardOutput), names, _self, File.Exists) : [];
    }

    /// <summary>
    /// The <c>ps</c> entries whose program is named one of <paramref name="names"/>. <c>ps</c> gives the command line
    /// as one string, and programs often have spaces in their paths (<c>/Users/Shared/Epic Games/…</c>), so the
    /// program is the longest start of the line, ending before a space, that is a file.
    /// </summary>
    internal static List<SystemProcess> MatchPs(IEnumerable<PsEntry> entries, IReadOnlyCollection<string> names, int self, Func<string, bool> fileExists)
    {
        var found = new List<SystemProcess>();
        foreach (var entry in entries)
        {
            if (entry.Pid == self || !names.Any(n => entry.Args.Contains(n.TrimEnd('*'), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var program = entry.Program;
            for (var space = entry.Args.IndexOf(' ', StringComparison.Ordinal); space > 0; space = entry.Args.IndexOf(' ', space + 1))
            {
                if (fileExists(entry.Args[..space]))
                {
                    program = entry.Args[..space];
                }
            }
            if (fileExists(entry.Args))
            {
                program = entry.Args;
            }
            var name = Path.GetFileName(program);
            if (SystemProcessNames.Matches(name, names))
            {
                found.Add(new SystemProcess(entry.Pid, name, entry.Args));
            }
        }
        return found;
    }

    private static string Bare(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    /// <summary><c>/proc/&lt;pid&gt;/cmdline</c>: the arguments, separated by NULs.</summary>
    private static IReadOnlyList<string> ReadArguments(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            return Encoding.UTF8.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? ReadLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadComm(int pid)
    {
        try
        {
            return File.ReadAllText($"/proc/{pid}/comm").Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
