using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace CodexSlotPing.Update;

public sealed class SemVersion : IComparable<SemVersion>
{
    private static readonly Regex Pattern = new(
        "^(?<major>0|[1-9][0-9]*)\\.(?<minor>0|[1-9][0-9]*)\\.(?<patch>0|[1-9][0-9]*)(?:-(?<prerelease>[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?(?:\\+(?<build>[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private SemVersion(BigInteger major, BigInteger minor, BigInteger patch, string? prerelease, string? buildMetadata)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
        BuildMetadata = buildMetadata;
    }

    public BigInteger Major { get; }
    public BigInteger Minor { get; }
    public BigInteger Patch { get; }
    public string? Prerelease { get; }
    public string? BuildMetadata { get; }
    public bool IsStable => Prerelease is null;

    public static SemVersion Parse(string value) =>
        TryParse(value, out var parsed) ? parsed! : throw new FormatException($"'{value}' is not a valid SemVer 2.0 version.");

    public static bool TryParse(string? value, out SemVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var match = Pattern.Match(value);
        if (!match.Success)
        {
            return false;
        }

        var prerelease = match.Groups["prerelease"].Success ? match.Groups["prerelease"].Value : null;
        if (prerelease is not null && prerelease.Split('.').Any(IsNumericWithLeadingZero))
        {
            return false;
        }

        version = new SemVersion(
            BigInteger.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture),
            BigInteger.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture),
            BigInteger.Parse(match.Groups["patch"].Value, CultureInfo.InvariantCulture),
            prerelease,
            match.Groups["build"].Success ? match.Groups["build"].Value : null);
        return true;
    }

    public static bool TryParseTag(string? tagName, out SemVersion? version)
    {
        var candidate = tagName is not null && tagName.StartsWith('v') ? tagName[1..] : tagName;
        return TryParse(candidate, out version);
    }

    public int CompareTo(SemVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var comparison = Major.CompareTo(other.Major);
        if (comparison != 0) return comparison;
        comparison = Minor.CompareTo(other.Minor);
        if (comparison != 0) return comparison;
        comparison = Patch.CompareTo(other.Patch);
        if (comparison != 0) return comparison;

        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;

        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            if (string.Equals(left[index], right[index], StringComparison.Ordinal))
            {
                continue;
            }

            var leftNumeric = IsNumeric(left[index]);
            var rightNumeric = IsNumeric(right[index]);
            if (leftNumeric && rightNumeric)
            {
                comparison = CompareNumericIdentifiers(left[index], right[index]);
            }
            else if (leftNumeric != rightNumeric)
            {
                comparison = leftNumeric ? -1 : 1;
            }
            else
            {
                comparison = string.CompareOrdinal(left[index], right[index]);
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Length.CompareTo(right.Length) switch
        {
            < 0 => -1,
            > 0 => 1,
            _ => 0
        };
    }

    public override string ToString()
    {
        var value = string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
        if (Prerelease is not null) value += "-" + Prerelease;
        if (BuildMetadata is not null) value += "+" + BuildMetadata;
        return value;
    }

    private static bool IsNumericWithLeadingZero(string identifier) =>
        IsNumeric(identifier) && identifier.Length > 1 && identifier[0] == '0';

    private static bool IsNumeric(string identifier) =>
        identifier.Length > 0 && identifier.All(character => character is >= '0' and <= '9');

    private static int CompareNumericIdentifiers(string left, string right)
    {
        var lengthComparison = left.Length.CompareTo(right.Length);
        return lengthComparison != 0 ? lengthComparison : string.CompareOrdinal(left, right);
    }
}
