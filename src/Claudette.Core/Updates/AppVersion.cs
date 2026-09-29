using System.Globalization;

namespace Claudette.Core.Updates;

/// <summary>
/// A Claudette version as releases are tagged (DESIGN.md §2, "Updating Claudette"): <c>1.2.3</c>, or a pre-release
/// such as <c>1.2.3-beta.2</c>, compared the way Semantic Versioning orders them. A leading <c>v</c> and build metadata
/// (<c>+abc123</c>, which .NET adds to the informational version) are ignored.
/// </summary>
public sealed record AppVersion(int Major, int Minor, int Patch, string? Prerelease = null) : IComparable<AppVersion>
{
    public bool IsPrerelease => Prerelease is not null;

    public static AppVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }
        if (value.IndexOf('+') is var plus and >= 0)
        {
            value = value[..plus];
        }
        string? prerelease = null;
        if (value.IndexOf('-') is var dash and >= 0)
        {
            prerelease = value[(dash + 1)..];
            value = value[..dash];
            if (prerelease.Length == 0 || prerelease.Split('.').Any(p => p.Length == 0))
            {
                return null;
            }
        }
        var parts = value.Split('.');
        if (parts.Length is < 1 or > 4 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit)))
        {
            return null;
        }
        // A fourth part is only allowed as the 0 that MSIX versions end with.
        if (parts.Length == 4 && parts[3].Trim('0').Length > 0)
        {
            return null;
        }
        int Part(int i) => i < parts.Length && int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return new AppVersion(Part(0), Part(1), Part(2), prerelease);
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null)
        {
            return 1;
        }
        var byNumber = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (byNumber != 0)
        {
            return byNumber;
        }
        return (Prerelease, other.Prerelease) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => ComparePrerelease(Prerelease!, other.Prerelease!),
        };
    }

    /// <summary>Dot-separated identifiers: numbers compare as numbers and sort before words; more identifiers win a tie.</summary>
    private static int ComparePrerelease(string a, string b)
    {
        var left = a.Split('.');
        var right = b.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var leftIsNumber = long.TryParse(left[i], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightIsNumber = long.TryParse(right[i], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
            var result = (leftIsNumber, rightIsNumber) switch
            {
                (true, true) => leftNumber.CompareTo(rightNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(left[i], right[i]),
            };
            if (result != 0)
            {
                return result;
            }
        }
        return left.Length.CompareTo(right.Length);
    }

    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;

    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;

    /// <summary>The four-part version an MSIX package carries: the pre-release label is dropped, and the last part is 0.</summary>
    public string ToPackageVersion() => $"{Major}.{Minor}.{Patch}.0";

    public override string ToString() => Prerelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}
