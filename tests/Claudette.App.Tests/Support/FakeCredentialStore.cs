using Claudette.Core.Credentials;

namespace Claudette.App.Tests.Support;

/// <summary>An in-memory OS credential store.</summary>
internal sealed class FakeCredentialStore : ICredentialStore
{
    public Dictionary<string, string> Secrets { get; } = [];

    public Dictionary<string, string> Labels { get; } = [];

    public string Name => "Test Keychain";

    public bool IsAvailable { get; set; } = true;

    public string? UnavailableReason => IsAvailable ? null : "Not available in this test.";

    public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (Secrets)
        {
            return Task.FromResult(Secrets.GetValueOrDefault(key));
        }
    }

    public Task WriteAsync(string key, string label, string secret, CancellationToken cancellationToken = default)
    {
        lock (Secrets)
        {
            Secrets[key] = secret;
            Labels[key] = label;
        }
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (Secrets)
        {
            return Task.FromResult(Secrets.Remove(key));
        }
    }
}
