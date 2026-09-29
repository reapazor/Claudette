using System.Runtime.Versioning;
using Claudette.Core.LoginItems;
using Claudette.Core.Updates;
using Claudette.Platform.LoginItems.Windows;
using Claudette.Platform.Tests.Support;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Win32;

namespace Claudette.Platform.Tests.LoginItems;

/// <summary>
/// The Run key's value that starts Claudette at login on Windows (DESIGN.md §9, "Starting at login"). It writes to a key
/// of its own under HKCU, never the real Run key. The MSIX's startup task needs the package, so it isn't tested here.
/// </summary>
[SupportedOSPlatform("windows10.0.16299")]
public sealed class WindowsLoginItemsTests : IDisposable
{
    private readonly string _key = $@"Software\Claudette.Tests.LoginItems.{Guid.NewGuid():N}";
    private readonly string _root = Directory.CreateTempSubdirectory("claudette-run-").FullName;

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            Registry.CurrentUser.DeleteSubKeyTree(_key, throwOnMissingSubKey: false);
        }
        Directory.Delete(_root, recursive: true);
    }

    private string RunKey => _key + @"\Run";

    private string ApprovedKey => _key + @"\StartupApproved";

    private WindowsLoginItems Items() => new(new FakeLauncher(), new FakeTimeProvider(), RunKey, ApprovedKey);

    [Fact]
    public void The_Run_value_starts_the_build_with_login_and_goes_with_Task_Managers_note()
    {
        Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299), "Windows only.");
        var build = Directory.CreateDirectory(Path.Combine(_root, "Program Files", "Claudette")).FullName;
        File.WriteAllText(Path.Combine(build, "Claudette.exe"), "");
        var items = Items();
        Assert.Null(items.PackageTask);
        Assert.Equal(LoginEntryState.Missing, items.ReadEntry());

        items.WriteEntry(new ClaudetteCopy(AppInstallKind.Other, build, "0.3.0"));

        Assert.Equal($"\"{Path.Combine(build, "Claudette.exe")}\" --login", items.ReadCommand());
        Assert.Equal(LoginEntryState.Enabled, items.ReadEntry());

        // Task Manager turns it off with an odd first byte, and on again with an even one.
        using (var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey))
        {
            approved.SetValue(WindowsLoginItems.ValueName, new byte[] { 3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, RegistryValueKind.Binary);
        }
        Assert.Equal(LoginEntryState.DisabledByUser, items.ReadEntry());
        using (var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey))
        {
            approved.SetValue(WindowsLoginItems.ValueName, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        }
        Assert.Equal(LoginEntryState.Enabled, items.ReadEntry());

        items.DeleteEntry();

        Assert.Equal(LoginEntryState.Missing, items.ReadEntry());
        using var left = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        Assert.Null(left?.GetValue(WindowsLoginItems.ValueName));
    }

    [Fact]
    public void A_build_run_by_dotnet_is_started_by_dotnet()
    {
        Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299), "Windows only.");
        var items = Items();

        items.WriteEntry(new ClaudetteCopy(AppInstallKind.SourceBuild, _root, "0.3.0"));

        Assert.EndsWith($" {WindowsLoginItems.Quote(Path.Combine(_root, "Claudette.dll"))} --login", items.ReadCommand(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_MSIX_that_isnt_installed_isnt_there()
    {
        Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299), "Windows only.");

        Assert.False(Items().Exists(new ClaudetteCopy(AppInstallKind.Msix, "reapazor.ClaudetteTests_0000000000000", "0.3.0")));
    }

    [Fact]
    [SupportedOSPlatform("windows10.0.17763")]
    public void A_Claudette_outside_the_package_wasnt_started_by_its_startup_task()
    {
        Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763), "Windows only.");

        Assert.False(WindowsStartupActivation.StartedByStartupTask());
    }

    [Theory]
    [InlineData("--login", "--login")]
    [InlineData(@"C:\Program Files\Claudette\Claudette.dll", "\"C:\\Program Files\\Claudette\\Claudette.dll\"")]
    [InlineData(@"C:\My Folder\", "\"C:\\My Folder\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("", "\"\"")]
    public void Arguments_are_quoted_so_Windows_reads_them_back(string argument, string expected)
    {
        Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299), "Windows only.");

        Assert.Equal(expected, WindowsLoginItems.Quote(argument));
    }
}
