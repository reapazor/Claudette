using Claudette.Core.Credentials;
using Claudette.Core.Processes;
using Claudette.Platform.Credentials.Linux;
using Claudette.Platform.Credentials.Mac;
using Claudette.Platform.Credentials.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudette.Platform.Credentials;

public static class CredentialStores
{
    /// <summary>
    /// The credential store for the OS Claudette is running on: Windows Credential Manager, the macOS Keychain, or the
    /// Secret Service through <c>secret-tool</c> on Linux. Anything that fails to set up gives an unavailable store.
    /// </summary>
    public static ICredentialStore CreateForCurrentOS(IProcessLauncher launcher, TimeProvider timeProvider, ILoggerFactory? loggerFactory = null)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return new WindowsCredentialStore();
            }
            if (OperatingSystem.IsMacOS())
            {
                return new MacKeychain();
            }
            if (OperatingSystem.IsLinux())
            {
                return SecretToolCredentialStore.TryCreate(launcher, timeProvider)
                    ?? (ICredentialStore)new UnavailableCredentialStore("Secret Service", SecretToolCredentialStore.MissingReason);
            }
        }
        catch (Exception ex)
        {
            (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger(typeof(CredentialStores)).LogWarning(ex, "The OS credential store isn't available.");
        }
        return new UnavailableCredentialStore();
    }
}
