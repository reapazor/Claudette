using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Claudette.Platform.Processes.Unix;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal static partial class LibC
{
    // The same on Linux and macOS. SIGSTOP and SIGCONT differ, so each tree supplies its own.
    public const int SigKill = 9;
    public const int SigTerm = 15;
    public const int Eperm = 1;

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    public static partial int Kill(int pid, int signal);

    [LibraryImport("libc", EntryPoint = "sysconf", SetLastError = true)]
    public static partial nint SysConf(int name);
}
