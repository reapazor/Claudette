using System.ComponentModel;
using Claudette.Core.Credentials;
using Claudette.Core.Diffs;
using Claudette.Core.Processes;

namespace Claudette.Platform.Credentials.Linux;

/// <summary>
/// The Secret Service (GNOME Keyring, KWallet) through libsecret's <c>secret-tool</c>, with the attributes
/// <c>application claudette</c> and <c>key &lt;key&gt;</c>. The secret goes to <c>secret-tool store</c> on standard
/// input, never on the command line. Linux isn't a polished target (DESIGN.md §1), so this stays a command-line tool
/// rather than D-Bus calls.
/// </summary>
public sealed class SecretToolCredentialStore(string secretTool, IProcessLauncher launcher, TimeProvider timeProvider) : ICredentialStore
{
    /// <summary>Long enough for the keyring to ask the user to unlock it.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public const string Application = "claudette";

    public const string MissingReason = "Install secret-tool (the libsecret-tools package) to store passwords in the Secret Service.";

    public string Name => "Secret Service";

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    /// <summary>The store, or null when <c>secret-tool</c> isn't installed.</summary>
    public static SecretToolCredentialStore? TryCreate(IProcessLauncher launcher, TimeProvider timeProvider, IFileProbe? probe = null) =>
        (probe ?? FileProbe.Instance).FindOnPath("secret-tool") is { } path ? new SecretToolCredentialStore(path, launcher, timeProvider) : null;

    public static IReadOnlyList<string> Attributes(string key) => ["application", Application, "key", key];

    public async Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["lookup", .. Attributes(key)], null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            // Not found is exit 1 with nothing on standard error.
            return result.StandardError.Trim() is { Length: > 0 } error ? throw new CredentialStoreException($"secret-tool couldn't read the password: {error}") : null;
        }
        // The secret is printed as stored; a password is one line.
        return result.StandardOutput.Split('\n')[0].TrimEnd('\r');
    }

    public async Task WriteAsync(string key, string label, string secret, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["store", $"--label={label}", .. Attributes(key)], secret, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new CredentialStoreException($"secret-tool couldn't save the password: {Detail(result)}");
        }
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var existed = await ReadAsync(key, cancellationToken).ConfigureAwait(false) is not null;
        var result = await RunAsync(["clear", .. Attributes(key)], null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 && result.StandardError.Trim().Length > 0)
        {
            throw new CredentialStoreException($"secret-tool couldn't remove the password: {Detail(result)}");
        }
        return existed;
    }

    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, string? input, CancellationToken cancellationToken)
    {
        try
        {
            return await ProcessRunner.RunAsync(launcher, new ProcessStartSpec(secretTool, arguments), Timeout, timeProvider, cancellationToken, inputLine: input)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or Win32Exception or InvalidOperationException or IOException)
        {
            throw new CredentialStoreException($"Couldn't run secret-tool: {ex.Message}", ex);
        }
    }

    private static string Detail(ProcessResult result) =>
        result.StandardError.Trim() is { Length: > 0 } error ? error : $"exit code {result.ExitCode}";
}
