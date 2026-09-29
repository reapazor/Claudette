using System.Runtime.Versioning;
using Claudette.Core.Credentials;
using Claudette.Core.Diffs;
using Claudette.Platform.Credentials;
using Claudette.Platform.Credentials.Linux;
using Claudette.Platform.Credentials.Mac;
using Claudette.Platform.Credentials.Windows;
using Claudette.Platform.Tests.Support;

namespace Claudette.Platform.Tests.Credentials;

/// <summary>
/// The OS credential stores that keep a stored Perforce password (DESIGN.md §18). <c>secret-tool</c> is faked; the
/// Windows store is used for real on Windows, and the macOS Keychain only when <c>CLAUDETTE_TEST_KEYCHAIN=1</c>, since
/// a locked keychain would ask.
/// </summary>
public class CredentialStoreTests
{
    private const string Key = "perforce/ssl:perforce:1666/matt";

    private readonly FakeLauncher _launcher = new();

    private SecretToolCredentialStore SecretTool => new("/usr/bin/secret-tool", _launcher, TimeProvider.System);

    [Fact]
    public void Secret_tool_is_used_when_installed()
    {
        Assert.NotNull(SecretToolCredentialStore.TryCreate(_launcher, TimeProvider.System, new Probe("/usr/bin/secret-tool")));
        Assert.Null(SecretToolCredentialStore.TryCreate(_launcher, TimeProvider.System, new Probe(null)));
    }

    [Fact]
    public async Task Secret_tool_gets_the_secret_on_standard_input()
    {
        var write = SecretTool.WriteAsync(Key, "Claudette: Perforce matt @ ssl:perforce:1666", "s3cret", TestContext.Current.CancellationToken);
        var process = await Started(0);

        Assert.Equal(["store", "--label=Claudette: Perforce matt @ ssl:perforce:1666", "application", "claudette", "key", Key], _launcher.Specs[0].Arguments);
        Assert.Equal(["s3cret"], process.Input);
        Assert.DoesNotContain(_launcher.Specs[0].Arguments, a => a.Contains("s3cret", StringComparison.Ordinal));
        process.Exit(0);
        await write;
    }

    [Fact]
    public async Task Secret_tool_reads_a_stored_secret()
    {
        var read = SecretTool.ReadAsync(Key, TestContext.Current.CancellationToken);
        var process = await Started(0);
        process.WriteOutput("s3cret");
        process.Exit(0);

        Assert.Equal("s3cret", await read);
        Assert.Equal(["lookup", "application", "claudette", "key", Key], _launcher.Specs[0].Arguments);
    }

    [Fact]
    public async Task Secret_tool_not_finding_one_is_null_and_failing_is_an_error()
    {
        var missing = SecretTool.ReadAsync(Key, TestContext.Current.CancellationToken);
        (await Started(0)).Exit(1);
        Assert.Null(await missing);

        var broken = SecretTool.ReadAsync(Key, TestContext.Current.CancellationToken);
        var process = await Started(1);
        process.WriteError("secret-tool: Cannot autolaunch D-Bus without X11 $DISPLAY");
        process.Exit(1);
        var error = await Assert.ThrowsAsync<CredentialStoreException>(() => broken);
        Assert.Contains("D-Bus", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secret_tool_clears_and_says_whether_there_was_one()
    {
        var delete = SecretTool.DeleteAsync(Key, TestContext.Current.CancellationToken);
        var lookup = await Started(0);
        lookup.WriteOutput("s3cret");
        lookup.Exit(0);
        (await Started(1)).Exit(0);

        Assert.True(await delete);
        Assert.Equal(["clear", "application", "claudette", "key", Key], _launcher.Specs[1].Arguments);
    }

    [Fact]
    public void Every_OS_gets_a_store_or_says_why_not()
    {
        var store = CredentialStores.CreateForCurrentOS(_launcher, TimeProvider.System);

        Assert.False(string.IsNullOrEmpty(store.Name));
        Assert.True(store.IsAvailable || !string.IsNullOrEmpty(store.UnavailableReason));
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.True(store.IsAvailable);
        }
    }

    [Fact]
    public async Task An_unavailable_store_reads_nothing_and_refuses_to_save()
    {
        var store = new UnavailableCredentialStore("Secret Service", SecretToolCredentialStore.MissingReason);

        Assert.Null(await store.ReadAsync(Key, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CredentialStoreException>(() => store.WriteAsync(Key, "label", "s3cret", TestContext.Current.CancellationToken));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Windows_credential_manager_round_trips()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var store = new WindowsCredentialStore();
        var key = $"test/{Guid.NewGuid():N}";
        try
        {
            await store.WriteAsync(key, "Claudette test", "first", TestContext.Current.CancellationToken);
            await store.WriteAsync(key, "Claudette test", "pässwörd ✓", TestContext.Current.CancellationToken);

            Assert.Equal("pässwörd ✓", await store.ReadAsync(key, TestContext.Current.CancellationToken));
        }
        finally
        {
            Assert.True(await store.DeleteAsync(key, TestContext.Current.CancellationToken));
        }
        Assert.Null(await store.ReadAsync(key, TestContext.Current.CancellationToken));
        Assert.False(await store.DeleteAsync(key, TestContext.Current.CancellationToken));
    }

    /// <summary>Loads Security.framework's constants and builds a query, without touching the keychain.</summary>
    [Fact]
    [SupportedOSPlatform("macos")]
    public void Mac_keychain_queries_can_be_built()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS only.");

        Assert.NotEqual(0, Security.Class);
        Assert.NotEqual(0, Security.ClassGenericPassword);
        Assert.NotEqual(0, Security.MatchLimitOne);
        using var query = CF.Dictionary(
            (Security.Class, Security.ClassGenericPassword),
            (Security.AttrService, CF.String(MacKeychain.Service)),
            (Security.ValueData, CF.Data([1, 2, 3])));
        Assert.NotEqual(0, query.Handle);
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task Mac_keychain_round_trips()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("CLAUDETTE_TEST_KEYCHAIN") == "1", "macOS with CLAUDETTE_TEST_KEYCHAIN=1 only.");
        var store = new MacKeychain();
        var key = $"test/{Guid.NewGuid():N}";
        try
        {
            await store.WriteAsync(key, "Claudette test", "first", TestContext.Current.CancellationToken);
            await store.WriteAsync(key, "Claudette test", "pässwörd ✓", TestContext.Current.CancellationToken);

            Assert.Equal("pässwörd ✓", await store.ReadAsync(key, TestContext.Current.CancellationToken));
        }
        finally
        {
            Assert.True(await store.DeleteAsync(key, TestContext.Current.CancellationToken));
        }
        Assert.Null(await store.ReadAsync(key, TestContext.Current.CancellationToken));
    }

    /// <summary>The <paramref name="index"/>th process, once it has had its input.</summary>
    private async Task<FakeRunningProcess> Started(int index)
    {
        for (var i = 0; i < 500 && !(_launcher.Started.Count > index && _launcher.Started[index].InputClosed); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(_launcher.Started.Count > index, "The process wasn't started.");
        return _launcher.Started[index];
    }

    private sealed class Probe(string? secretTool) : IFileProbe
    {
        public bool FileExists(string path) => path == secretTool;

        public string? FindOnPath(string fileName) => fileName == "secret-tool" ? secretTool : null;

        public string ExpandEnvironmentVariables(string path) => path;
    }
}
