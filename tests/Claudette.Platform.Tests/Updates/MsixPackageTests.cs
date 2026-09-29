using System.IO.Compression;
using System.Runtime.InteropServices;
using Claudette.Core.Updates;
using Claudette.Platform.Updates.Windows;

namespace Claudette.Platform.Tests.Updates;

/// <summary>Checking a downloaded MSIX before Windows installs it (DESIGN.md §2, "Updating Claudette").</summary>
public sealed class MsixPackageTests : IDisposable
{
    private const string Publisher = "CN=Matthew Davey, O=Matthew Davey, C=GB";

    private readonly string _folder = Directory.CreateTempSubdirectory("claudette-msix-").FullName;

    private string MakePackage(string name = "reapazor.Claudette", string publisher = Publisher, string version = "1.3.0.0", string architecture = "x64")
    {
        var path = Path.Combine(_folder, $"{Guid.NewGuid():N}.msix");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("AppxManifest.xml").Open()))
        {
            writer.Write($"""
                <?xml version="1.0" encoding="utf-8"?>
                <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
                  <Identity Name="{name}" Publisher="{publisher}" Version="{version}" ProcessorArchitecture="{architecture}" />
                  <Properties><DisplayName>Claudette</DisplayName></Properties>
                </Package>
                """);
        }
        zip.CreateEntry("Claudette.exe");
        return path;
    }

    private static string Family(string publisher = Publisher) => $"reapazor.Claudette_{MsixPackage.ComputePublisherId(publisher)}";

    [Fact]
    public void The_publisher_id_is_computed_as_Windows_does() =>
        Assert.Equal("8wekyb3d8bbwe", MsixPackage.ComputePublisherId("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"));

    [Fact]
    public void The_identity_is_read_from_the_manifest()
    {
        var package = MsixPackage.Read(MakePackage());

        Assert.Equal(new MsixPackage("reapazor.Claudette", Publisher, "1.3.0.0", "x64"), package);
        Assert.Equal(Family(), package.FamilyName);
    }

    [Fact]
    public void Something_that_is_not_a_package_is_refused()
    {
        var path = Path.Combine(_folder, "not.msix");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            zip.CreateEntry("readme.txt");
        }

        Assert.Throws<InvalidDataException>(() => MsixPackage.Read(path));
    }

    [Fact]
    public void A_package_for_this_install_version_and_architecture_passes() =>
        MsixPackage.Read(MakePackage()).CheckUpdates(Family(), new AppVersion(1, 3, 0), Architecture.X64);

    [Fact]
    public void A_pre_release_matches_its_package_version_without_the_label() =>
        MsixPackage.Read(MakePackage()).CheckUpdates(Family(), new AppVersion(1, 3, 0, "beta.2"), Architecture.X64);

    [Fact]
    public void A_package_from_another_publisher_would_install_beside_this_one_so_it_is_refused()
    {
        var error = Assert.Throws<AppInstallException>(() =>
            MsixPackage.Read(MakePackage(publisher: "CN=Someone Else")).CheckUpdates(Family(), new AppVersion(1, 3, 0), Architecture.X64));

        Assert.Contains("different publisher (CN=Someone Else)", error.Message);
    }

    [Fact]
    public void Another_app_the_wrong_version_or_the_wrong_architecture_is_refused()
    {
        Assert.Equal("The download is Contoso.Other, not Claudette.", Assert.Throws<AppInstallException>(() =>
            MsixPackage.Read(MakePackage(name: "Contoso.Other")).CheckUpdates(Family(), new AppVersion(1, 3, 0), Architecture.X64)).Message);
        Assert.Equal("The package is version 1.2.9.0, not 1.3.0.0 as the release says.", Assert.Throws<AppInstallException>(() =>
            MsixPackage.Read(MakePackage(version: "1.2.9.0")).CheckUpdates(Family(), new AppVersion(1, 3, 0), Architecture.X64)).Message);
        Assert.Equal("The package is for arm64, and this Claudette runs on x64.", Assert.Throws<AppInstallException>(() =>
            MsixPackage.Read(MakePackage(architecture: "arm64")).CheckUpdates(Family(), new AppVersion(1, 3, 0), Architecture.X64)).Message);
    }

    [Theory]
    [InlineData(unchecked((int)0x800B0109), "Windows doesn't trust the certificate the update is signed with.")]
    [InlineData(unchecked((int)0x80073D06), "A newer version of Claudette is already installed.")]
    [InlineData(unchecked((int)0x80004005), "Windows couldn't install the update (error 0x80004005).")]
    public void Deployment_errors_are_put_in_words(int hresult, string expected) => Assert.Equal(expected, MsixPackage.DescribeError(hresult));

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
