using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Claudette.Core.Credentials;

namespace Claudette.Platform.Credentials.Mac;

/// <summary>
/// The macOS Keychain: generic passwords with service <c>Claudette</c> and the key as the account, through
/// Security.framework's <c>SecItem</c> calls (the current API, not the deprecated <c>SecKeychain</c> one).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychain : ICredentialStore
{
    public const string Service = "Claudette";

    public string Name => "macOS Keychain";

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Read(key));

    public Task WriteAsync(string key, string label, string secret, CancellationToken cancellationToken = default)
    {
        Write(key, label, secret);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Delete(key));

    private static unsafe string? Read(string key)
    {
        using var query = CF.Dictionary(
            (Security.Class, Security.ClassGenericPassword),
            (Security.AttrService, CF.String(Service)),
            (Security.AttrAccount, CF.String(key)),
            (Security.ReturnData, CF.BooleanTrue),
            (Security.MatchLimit, Security.MatchLimitOne));
        var status = Security.SecItemCopyMatching(query.Handle, out var data);
        if (status == Security.ErrSecItemNotFound)
        {
            return null;
        }
        Check(status, "read the password from the Keychain");
        try
        {
            var length = CF.CFDataGetLength(data);
            return length == 0 ? "" : Encoding.UTF8.GetString(CF.CFDataGetBytePtr(data), (int)length);
        }
        finally
        {
            CF.CFRelease(data);
        }
    }

    private static unsafe void Write(string key, string label, string secret)
    {
        Delete(key);
        var bytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            using var attributes = CF.Dictionary(
                (Security.Class, Security.ClassGenericPassword),
                (Security.AttrService, CF.String(Service)),
                (Security.AttrAccount, CF.String(key)),
                (Security.AttrLabel, CF.String(label)),
                (Security.ValueData, CF.Data(bytes)));
            Check(Security.SecItemAdd(attributes.Handle, null), "save the password in the Keychain");
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    private static bool Delete(string key)
    {
        using var query = CF.Dictionary(
            (Security.Class, Security.ClassGenericPassword),
            (Security.AttrService, CF.String(Service)),
            (Security.AttrAccount, CF.String(key)));
        var status = Security.SecItemDelete(query.Handle);
        if (status == Security.ErrSecItemNotFound)
        {
            return false;
        }
        Check(status, "remove the password from the Keychain");
        return true;
    }

    private static void Check(int status, string what)
    {
        if (status != 0)
        {
            throw new CredentialStoreException(status == Security.ErrSecInteractionNotAllowed
                ? $"Couldn't {what}: the Keychain is locked."
                : $"Couldn't {what} (OSStatus {status}).");
        }
    }
}

/// <summary>Security.framework's keychain item calls and the constants they take.</summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class Security
{
    private const string Framework = "/System/Library/Frameworks/Security.framework/Security";

    public const int ErrSecItemNotFound = -25300;
    public const int ErrSecInteractionNotAllowed = -25308;

    private static readonly nint Library = NativeLibrary.Load(Framework);

    public static nint Class { get; } = Constant("kSecClass");

    public static nint ClassGenericPassword { get; } = Constant("kSecClassGenericPassword");

    public static nint AttrService { get; } = Constant("kSecAttrService");

    public static nint AttrAccount { get; } = Constant("kSecAttrAccount");

    public static nint AttrLabel { get; } = Constant("kSecAttrLabel");

    public static nint ValueData { get; } = Constant("kSecValueData");

    public static nint ReturnData { get; } = Constant("kSecReturnData");

    public static nint MatchLimit { get; } = Constant("kSecMatchLimit");

    public static nint MatchLimitOne { get; } = Constant("kSecMatchLimitOne");

    [LibraryImport(Framework)]
    public static partial int SecItemAdd(nint attributes, nint* result);

    [LibraryImport(Framework)]
    public static partial int SecItemCopyMatching(nint query, out nint result);

    [LibraryImport(Framework)]
    public static partial int SecItemDelete(nint query);

    /// <summary>The constants are exported <c>CFStringRef</c> variables: read the pointer stored at the symbol.</summary>
    private static nint Constant(string name) => *(nint*)NativeLibrary.GetExport(Library, name);
}

/// <summary>Just enough CoreFoundation to build the dictionaries the <c>SecItem</c> calls take.</summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class CF
{
    private const string Framework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint StringEncodingUtf8 = 0x0800_0100;

    private static readonly nint Library = NativeLibrary.Load(Framework);

    /// <summary><c>kCFBooleanTrue</c>. Not owned: <see cref="Owned"/> doesn't release it.</summary>
    public static Owned BooleanTrue => new(*(nint*)NativeLibrary.GetExport(Library, "kCFBooleanTrue"), owns: false);

    [LibraryImport(Framework)]
    private static partial nint CFStringCreateWithCString(nint allocator, byte* text, uint encoding);

    [LibraryImport(Framework)]
    private static partial nint CFDataCreate(nint allocator, byte* bytes, nint length);

    [LibraryImport(Framework)]
    public static partial byte* CFDataGetBytePtr(nint data);

    [LibraryImport(Framework)]
    public static partial nint CFDataGetLength(nint data);

    [LibraryImport(Framework)]
    private static partial nint CFDictionaryCreate(nint allocator, nint* keys, nint* values, nint count, nint keyCallbacks, nint valueCallbacks);

    [LibraryImport(Framework)]
    public static partial void CFRelease(nint value);

    public static Owned String(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* text = bytes)
        {
            return new Owned(CFStringCreateWithCString(0, text, StringEncodingUtf8));
        }
    }

    public static Owned Data(byte[] value)
    {
        fixed (byte* bytes = value)
        {
            return new Owned(CFDataCreate(0, bytes, value.Length));
        }
    }

    /// <summary>
    /// A dictionary of the given keys and values; it retains them, so the values passed in (from <see cref="String"/>
    /// and <see cref="Data"/>) are released here. The keys are constants.
    /// </summary>
    public static Owned Dictionary(params (nint Key, Owned Value)[] entries) => Dictionary(entries.Select(e => (e.Key, e.Value.Handle)).ToArray(), entries.Select(e => e.Value));

    private static Owned Dictionary((nint Key, nint Value)[] entries, IEnumerable<Owned> release)
    {
        try
        {
            var keys = stackalloc nint[entries.Length];
            var values = stackalloc nint[entries.Length];
            for (var i = 0; i < entries.Length; i++)
            {
                keys[i] = entries[i].Key;
                values[i] = entries[i].Value;
            }
            return new Owned(CFDictionaryCreate(
                0, keys, values, entries.Length,
                NativeLibrary.GetExport(Library, "kCFTypeDictionaryKeyCallBacks"),
                NativeLibrary.GetExport(Library, "kCFTypeDictionaryValueCallBacks")));
        }
        finally
        {
            foreach (var value in release)
            {
                value.Dispose();
            }
        }
    }

    /// <summary>A CoreFoundation object, released on dispose when owned.</summary>
    public readonly struct Owned : IDisposable
    {
        private readonly bool _owns;

        public Owned(nint handle, bool owns = true)
        {
            Handle = handle;
            _owns = owns;
        }

        public nint Handle { get; }

        public void Dispose()
        {
            if (_owns && Handle != 0)
            {
                CFRelease(Handle);
            }
        }

        public static implicit operator Owned(nint constant) => new(constant, owns: false);
    }
}
