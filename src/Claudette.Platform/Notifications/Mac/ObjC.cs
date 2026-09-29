using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Claudette.Platform.Notifications.Mac;

/// <summary>
/// Just enough of the Objective-C runtime to talk to AppKit and UserNotifications without a binding library.
/// <c>objc_msgSend</c> is called through function pointers of the exact signature, which arm64 requires.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class ObjC
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";

    private static readonly nint MsgSendPointer = NativeLibrary.GetExport(NativeLibrary.Load(LibObjC), "objc_msgSend");
    private static readonly ConcurrentDictionary<string, nint> Selectors = new(StringComparer.Ordinal);

    [LibraryImport(LibObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GetClass(string name);

    [LibraryImport(LibObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint RegisterSelector(string name);

    [LibraryImport(LibObjC, EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint AllocateClassPair(nint superclass, string name, nuint extraBytes);

    [LibraryImport(LibObjC, EntryPoint = "objc_registerClassPair")]
    public static partial void RegisterClassPair(nint cls);

    [LibraryImport(LibObjC, EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool AddMethod(nint cls, nint selector, nint implementation, string types);

    [LibraryImport(LibObjC, EntryPoint = "objc_getProtocol", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GetProtocol(string name);

    [LibraryImport(LibObjC, EntryPoint = "class_addProtocol")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool AddProtocol(nint cls, nint protocol);

    [LibraryImport(LibObjC, EntryPoint = "objc_autoreleasePoolPush")]
    public static partial nint AutoreleasePoolPush();

    [LibraryImport(LibObjC, EntryPoint = "objc_autoreleasePoolPop")]
    public static partial void AutoreleasePoolPop(nint pool);

    public static nint Sel(string name) => Selectors.GetOrAdd(name, RegisterSelector);

    public static nint Send(nint receiver, string selector) =>
        ((delegate* unmanaged<nint, nint, nint>)MsgSendPointer)(receiver, Sel(selector));

    public static nint Send(nint receiver, string selector, nint a) =>
        ((delegate* unmanaged<nint, nint, nint, nint>)MsgSendPointer)(receiver, Sel(selector), a);

    public static nint Send(nint receiver, string selector, nint a, nint b) =>
        ((delegate* unmanaged<nint, nint, nint, nint, nint>)MsgSendPointer)(receiver, Sel(selector), a, b);

    public static nint Send(nint receiver, string selector, nint a, nint b, nint c) =>
        ((delegate* unmanaged<nint, nint, nint, nint, nint, nint>)MsgSendPointer)(receiver, Sel(selector), a, b, c);

    /// <summary>For selectors whose first argument is an NSUInteger, such as an options mask.</summary>
    public static nint Send(nint receiver, string selector, nuint a, nint b) =>
        ((delegate* unmanaged<nint, nint, nuint, nint, nint>)MsgSendPointer)(receiver, Sel(selector), a, b);

    /// <summary><c>[[cls alloc] init]</c>: an owned (+1) instance.</summary>
    public static nint New(string className) => Send(Send(GetClass(className), "alloc"), "init");

    public static void Release(nint instance)
    {
        if (instance != 0)
        {
            Send(instance, "release");
        }
    }

    /// <summary>An owned (+1) NSString; release it when done.</summary>
    public static nint NSString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return Send(Send(GetClass("NSString"), "alloc"), "initWithUTF8String:", utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    public static string? ToManagedString(nint nsString) =>
        nsString == 0 ? null : Marshal.PtrToStringUTF8(Send(nsString, "UTF8String"));

    // ---- Blocks -----------------------------------------------------------------------------------------------

    private const int BlockIsGlobal = 1 << 28;
    private const int BlockHasSignature = 1 << 30;

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public nint Invoke;
        public BlockDescriptor* Descriptor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
        public nint Signature;
    }

    /// <summary>
    /// A global block (one that captures nothing) calling <paramref name="invoke"/>, for completion handlers. It lives
    /// for the rest of the process.
    /// </summary>
    /// <param name="signature">The block's Objective-C type encoding, such as <c>v@?B@</c>.</param>
    public static nint GlobalBlock(nint invoke, string signature)
    {
        var descriptor = (BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
        descriptor->Size = (nuint)sizeof(BlockLiteral);
        descriptor->Signature = Marshal.StringToCoTaskMemUTF8(signature);
        var block = (BlockLiteral*)NativeMemory.AllocZeroed((nuint)sizeof(BlockLiteral));
        block->Isa = NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_NSConcreteGlobalBlock");
        block->Flags = BlockIsGlobal | BlockHasSignature;
        block->Invoke = invoke;
        block->Descriptor = descriptor;
        return (nint)block;
    }

    /// <summary>Calls a <c>void (^)(void)</c> block that Objective-C code handed us.</summary>
    public static void CallBlock(nint block)
    {
        if (block != 0)
        {
            ((delegate* unmanaged<nint, void>)((BlockLiteral*)block)->Invoke)(block);
        }
    }

    /// <summary>Calls a <c>void (^)(NSUInteger)</c> block that Objective-C code handed us.</summary>
    public static void CallBlock(nint block, nuint argument)
    {
        if (block != 0)
        {
            ((delegate* unmanaged<nint, nuint, void>)((BlockLiteral*)block)->Invoke)(block, argument);
        }
    }
}
