using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Claudette.Platform.Processes.Windows;

/// <summary>A kernel handle closed with <c>CloseHandle</c>: a Job Object or a Toolhelp snapshot.</summary>
[SupportedOSPlatform("windows")]
internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeKernelHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => WindowsNative.CloseHandle(handle);
}

[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowsNative
{
    public const uint ProcessTerminate = 0x0001;
    public const uint ProcessSetQuota = 0x0100;
    public const uint ProcessVmRead = 0x0010;
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint Synchronize = 0x0010_0000;

    public const int ErrorMoreData = 234;
    public const uint StillActive = 259;

    public const int JobObjectBasicProcessIdList = 3;

    public const int ProcessBasicInformationClass = 0;
    public const int ProcessCommandLineInformationClass = 60;
    public const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    public const int StatusBufferTooSmall = unchecked((int)0xC0000023);

    public const uint Th32csSnapProcess = 0x0000_0002;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeKernelHandle CreateJobObject(nint jobAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AssignProcessToJobObject(SafeKernelHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateJobObject(SafeKernelHandle job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryInformationJobObject(SafeKernelHandle job, int infoClass, void* info, uint length, out uint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsProcessInJob(SafeProcessHandle process, SafeKernelHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    /// <summary>All four times are FILETIMEs: creation and exit are absolute, kernel and user are durations.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessMemoryInfo(SafeProcessHandle process, ProcessMemoryCounters* counters, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* buffer, ref uint size);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQueryInformationProcess(SafeProcessHandle process, int infoClass, void* info, uint length, out uint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeKernelHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Process32First(SafeKernelHandle snapshot, ProcessEntry32* entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Process32Next(SafeKernelHandle snapshot, ProcessEntry32* entry);

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessMemoryCounters
    {
        public uint Cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    /// <summary>
    /// PROCESS_BASIC_INFORMATION. Every field is pointer-sized once padding is counted, so this layout fits both 32- and
    /// 64-bit processes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public fixed char ExeFile[260];
    }
}
