using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Claudette.Platform.LoginShell;

/// <summary>Whether this process has a controlling terminal: one started from a terminal does, one from the Dock or a desktop launcher doesn't.</summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal static partial class ControllingTerminal
{
    private const int ReadOnly = 0; // O_RDONLY, the same on Linux and macOS.

    /// <summary><c>/dev/tty</c> opens only for a process with a controlling terminal.</summary>
    public static bool Exists()
    {
        try
        {
            var fd = Open("/dev/tty", ReadOnly);
            if (fd < 0)
            {
                return false;
            }
            _ = Close(fd);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fd);
}
