using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Connector.Updates;

/// <summary>
/// The product version as the release pipeline produces it: exactly three decimal parts
/// (<c>build_clean_connector_release.ps1</c> accepts only <c>^\d+\.\d+\.\d+$</c>, and MSI keeps the same three).
/// No pre-release suffixes, no leading zeros, no signs: a version string has one spelling.
/// </summary>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    private const int MaximumPart = 65535;

    public static bool TryParse([NotNullWhen(true)] string? value, out ReleaseVersion version)
    {
        version = default;
        if (value is null || value.Length is 0 or > 17) return false;
        var parts = value.Split('.');
        if (parts.Length != 3) return false;
        Span<int> numbers = stackalloc int[3];
        for (var index = 0; index < 3; index++)
        {
            var part = parts[index];
            if (part.Length is 0 or > 5 || (part.Length > 1 && part[0] == '0')) return false;
            foreach (var character in part)
                if (character is < '0' or > '9') return false;
            var number = int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture);
            if (number > MaximumPart) return false;
            numbers[index] = number;
        }
        version = new ReleaseVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    public static ReleaseVersion Parse(string value) =>
        TryParse(value, out var version) ? version : throw new FormatException("Invalid release version.");

    public int CompareTo(ReleaseVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0) return major;
        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}
