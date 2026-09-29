using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Claudette.Core.Credentials;

namespace Claudette.Platform.Credentials.Windows;

/// <summary>
/// Windows Credential Manager: generic credentials named <c>Claudette/&lt;key&gt;</c>, kept for this user on this
/// machine (<c>CRED_PERSIST_LOCAL_MACHINE</c>, so they don't roam with a domain profile). The secret is UTF-16.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore : ICredentialStore
{
    public string Name => "Windows Credential Manager";

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public static string TargetName(string key) => $"Claudette/{key}";

    public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Read(key));

    public Task WriteAsync(string key, string label, string secret, CancellationToken cancellationToken = default)
    {
        Write(key, label, secret);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        if (CredentialNative.CredDelete(TargetName(key), CredentialNative.CredTypeGeneric, 0))
        {
            return Task.FromResult(true);
        }
        var error = Marshal.GetLastPInvokeError();
        return error == CredentialNative.ErrorNotFound
            ? Task.FromResult(false)
            : Task.FromException<bool>(new CredentialStoreException($"Couldn't remove the password from Windows Credential Manager (error {error})."));
    }

    private static unsafe string? Read(string key)
    {
        if (!CredentialNative.CredRead(TargetName(key), CredentialNative.CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == CredentialNative.ErrorNotFound
                ? null
                : throw new CredentialStoreException($"Couldn't read Windows Credential Manager (error {error}).");
        }
        try
        {
            var credential = (CredentialNative.Credential*)pointer;
            return credential->CredentialBlobSize == 0
                ? ""
                : Encoding.Unicode.GetString(credential->CredentialBlob, (int)credential->CredentialBlobSize);
        }
        finally
        {
            CredentialNative.CredFree(pointer);
        }
    }

    private static unsafe void Write(string key, string label, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        if (blob.Length > CredentialNative.MaxBlobSize)
        {
            throw new CredentialStoreException("The password is too long for Windows Credential Manager.");
        }
        fixed (char* target = TargetName(key))
        fixed (char* comment = label)
        fixed (char* user = key)
        fixed (byte* data = blob)
        {
            var credential = new CredentialNative.Credential
            {
                Type = CredentialNative.CredTypeGeneric,
                TargetName = target,
                Comment = comment,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = data,
                Persist = CredentialNative.CredPersistLocalMachine,
                UserName = user,
            };
            try
            {
                if (!CredentialNative.CredWrite(&credential, 0))
                {
                    throw new CredentialStoreException($"Couldn't save the password in Windows Credential Manager (error {Marshal.GetLastPInvokeError()}).");
                }
            }
            finally
            {
                Array.Clear(blob);
            }
        }
    }
}

[SupportedOSPlatform("windows")]
internal static unsafe partial class CredentialNative
{
    public const uint CredTypeGeneric = 1;
    public const uint CredPersistLocalMachine = 2;
    public const int ErrorNotFound = 1168;

    /// <summary>CRED_MAX_CREDENTIAL_BLOB_SIZE.</summary>
    public const int MaxBlobSize = 5 * 512;

    /// <summary>CREDENTIALW.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Credential
    {
        public uint Flags;
        public uint Type;
        public char* TargetName;
        public char* Comment;
        public uint LastWrittenLow;
        public uint LastWrittenHigh;
        public uint CredentialBlobSize;
        public byte* CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public char* TargetAlias;
        public char* UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CredRead(string targetName, uint type, uint flags, out nint credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CredWrite(Credential* credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CredDelete(string targetName, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    public static partial void CredFree(nint buffer);
}
