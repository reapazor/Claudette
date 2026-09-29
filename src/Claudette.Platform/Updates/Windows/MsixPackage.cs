using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Claudette.Core.Updates;

namespace Claudette.Platform.Updates.Windows;

/// <summary>
/// The identity an MSIX package declares in its <c>AppxManifest.xml</c>: what Windows matches to decide whether it
/// updates an installed package or installs a second one beside it.
/// </summary>
public sealed record MsixPackage(string Name, string Publisher, string Version, string Architecture)
{
    /// <summary>The 13-character publisher id in package family names (<c>Name_publisherid</c>).</summary>
    public string PublisherId => ComputePublisherId(Publisher);

    public string FamilyName => $"{Name}_{PublisherId}";

    /// <summary>Reads the manifest from a package file. Throws <see cref="InvalidDataException"/> for anything that isn't one.</summary>
    public static MsixPackage Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("AppxManifest.xml") ?? throw new InvalidDataException("The file isn't an MSIX package: it has no AppxManifest.xml.");
        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        var identity = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Identity")
            ?? throw new InvalidDataException("The package's manifest has no identity.");
        string Attribute(string name) => identity.Attribute(name)?.Value ?? "";
        return new MsixPackage(Attribute("Name"), Attribute("Publisher"), Attribute("Version"), Attribute("ProcessorArchitecture"));
    }

    /// <summary>
    /// Checks that the package updates this installation: the same package family (name and publisher), the release's
    /// version, and this process's architecture. A different publisher would install a second Claudette beside this one.
    /// </summary>
    public void CheckUpdates(string? currentFamily, AppVersion version, System.Runtime.InteropServices.Architecture architecture)
    {
        if (currentFamily is not null && !string.Equals(FamilyName, currentFamily, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppInstallException(Name.Equals(currentFamily.Split('_')[0], StringComparison.OrdinalIgnoreCase)
                ? $"The update is signed by a different publisher ({Publisher}) than this copy of Claudette, so Windows would install it beside this one instead of updating it."
                : $"The download is {Name}, not Claudette.");
        }
        if (Version != version.ToPackageVersion())
        {
            throw new AppInstallException($"The package is version {Version}, not {version.ToPackageVersion()} as the release says.");
        }
        var arch = architecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        if (!string.Equals(Architecture, arch, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppInstallException($"The package is for {Architecture}, and this Claudette runs on {arch}.");
        }
    }

    /// <summary>The deployment errors a user can act on, in words; anything else with its code.</summary>
    public static string DescribeError(int hresult) => unchecked((uint)hresult) switch
    {
        0x800B0109 => "Windows doesn't trust the certificate the update is signed with.",
        0x800B0100 => "The update isn't signed, and Windows only installs signed packages.",
        0x80073D06 => "A newer version of Claudette is already installed.",
        0x80073CFB => "This version is already installed with different contents.",
        0x80073D02 => "Claudette's files were in use, so Windows couldn't replace them. Close Claudette and install the update by hand.",
        0x80073CF0 => "Windows couldn't open the update package.",
        0x80070070 => "There isn't enough disk space to install the update.",
        var code => $"Windows couldn't install the update (error 0x{code:X8}).",
    };

    /// <summary>
    /// Windows' publisher id: the first 8 bytes of the SHA-256 of the publisher name in UTF-16, as 13 characters of
    /// Crockford's base 32 (the 64 bits padded with one zero bit).
    /// </summary>
    public static string ComputePublisherId(string publisher)
    {
        const string alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
        var hash = SHA256.HashData(Encoding.Unicode.GetBytes(publisher));
        var bits = (ulong)hash[0] << 56 | (ulong)hash[1] << 48 | (ulong)hash[2] << 40 | (ulong)hash[3] << 32
            | (ulong)hash[4] << 24 | (ulong)hash[5] << 16 | (ulong)hash[6] << 8 | hash[7];
        var id = new StringBuilder(13);
        for (var i = 0; i < 13; i++)
        {
            // 65 bits: the last group is the lowest 4 bits followed by the zero pad.
            var shift = 59 - i * 5;
            var group = shift >= 0 ? (int)(bits >> shift) & 0x1F : (int)(bits << -shift) & 0x1F;
            id.Append(alphabet[group]);
        }
        return id.ToString();
    }
}
