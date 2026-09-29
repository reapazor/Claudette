namespace Claudette.Core.Credentials;

/// <summary>A credential store operation failed, for example the keychain is locked or the Secret Service isn't running.</summary>
public sealed class CredentialStoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The OS credential store: Windows Credential Manager, the macOS Keychain, or the Secret Service on Linux. Claudette
/// keeps secrets it must store, such as a Perforce password (DESIGN.md §18), only here: never in its settings files,
/// never synced, never logged. The implementations are in Claudette.Platform.
/// </summary>
public interface ICredentialStore
{
    /// <summary>The store's name as the user knows it, such as "macOS Keychain".</summary>
    string Name { get; }

    /// <summary>False when this machine has no store Claudette can use, for example Linux without <c>secret-tool</c>.</summary>
    bool IsAvailable { get; }

    /// <summary>Why <see cref="IsAvailable"/> is false, for Settings.</summary>
    string? UnavailableReason { get; }

    /// <summary>The secret saved under <paramref name="key"/>, or null when there's none.</summary>
    /// <exception cref="CredentialStoreException">The store couldn't be read.</exception>
    Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Saves <paramref name="secret"/> under <paramref name="key"/>, replacing any saved before.</summary>
    /// <param name="label">What the OS shows for it, such as "Claudette: Perforce matt @ ssl:perforce:1666".</param>
    /// <exception cref="CredentialStoreException">The store couldn't be written.</exception>
    Task WriteAsync(string key, string label, string secret, CancellationToken cancellationToken = default);

    /// <summary>Removes the secret saved under <paramref name="key"/>. Returns whether there was one.</summary>
    /// <exception cref="CredentialStoreException">The store couldn't be changed.</exception>
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>No credential store: tests, and machines without one.</summary>
public sealed class UnavailableCredentialStore(string name = "Credential store", string reason = "No credential store is available on this machine.") : ICredentialStore
{
    public string Name => name;

    public bool IsAvailable => false;

    public string? UnavailableReason => reason;

    public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public Task WriteAsync(string key, string label, string secret, CancellationToken cancellationToken = default) =>
        Task.FromException(new CredentialStoreException(reason));

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(false);
}
