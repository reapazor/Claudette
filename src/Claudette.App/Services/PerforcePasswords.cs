using Claudette.Core.Credentials;
using Claudette.Core.Perforce;

namespace Claudette.App.Services;

/// <summary>What the user typed in the Perforce password prompt.</summary>
/// <param name="Save">Save it in the OS credential store once it has worked ("Stored by Claudette").</param>
public sealed record PerforcePasswordAnswer(string Password, bool Save);

/// <summary>
/// Supplies a tab's Perforce passwords from Settings → Perforce → Password source (DESIGN.md §18, "Where the password
/// comes from"). Nothing here keeps a password beyond the login it's for.
/// </summary>
/// <param name="ask">
/// Shows the tab's password prompt; null when the user cancels. The flag says whether to offer saving it.
/// </param>
public sealed class PerforcePasswords(PerforceService perforce, Func<PerforcePasswordRequest, bool, CancellationToken, Task<PerforcePasswordAnswer?>> ask)
    : IPerforcePasswordProvider
{
    private bool _saveOnSuccess;

    public async Task<string?> GetPasswordAsync(PerforcePasswordRequest request, CancellationToken cancellationToken)
    {
        _saveOnSuccess = false;
        var store = perforce.Credentials;
        switch (perforce.Settings.PasswordSource)
        {
            case PerforcePasswordSource.PerforceConfig:
                // Claudette only reads P4PASSWD; once Perforce has refused it, there's nothing else to try.
                return request.Attempt == 1
                    ? await perforce.Client.GetSettingAsync(request.Target.Folder, "P4PASSWD", cancellationToken).ConfigureAwait(false)
                    : null;

            case PerforcePasswordSource.AskEachTime:
                return (await ask(request, false, cancellationToken).ConfigureAwait(false))?.Password;

            default:
                if (request.Attempt == 1 && store.IsAvailable)
                {
                    try
                    {
                        if (await store.ReadAsync(PerforceService.CredentialKey(request.Server, request.User), cancellationToken).ConfigureAwait(false) is { } stored)
                        {
                            return stored;
                        }
                    }
                    catch (CredentialStoreException)
                    {
                        // Ask instead; saving will say what's wrong with the store.
                    }
                }
                // Nothing stored yet, or Perforce refused the stored one: ask, and offer to save the new one.
                var answer = await ask(request, store.IsAvailable, cancellationToken).ConfigureAwait(false);
                _saveOnSuccess = answer?.Save == true;
                return answer?.Password;
        }
    }

    public async Task OnLoginSucceededAsync(PerforcePasswordRequest request, string password, CancellationToken cancellationToken)
    {
        if (_saveOnSuccess && perforce.Settings.PasswordSource == PerforcePasswordSource.Stored && perforce.Credentials.IsAvailable)
        {
            _saveOnSuccess = false;
            await perforce.Credentials.WriteAsync(
                PerforceService.CredentialKey(request.Server, request.User),
                PerforceService.CredentialLabel(request.Server, request.User),
                password,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
