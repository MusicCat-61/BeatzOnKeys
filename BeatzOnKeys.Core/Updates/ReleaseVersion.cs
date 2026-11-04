namespace BeatzOnKeys.Core.Updates;

/// <summary>Comparable GitHub release version normalized to four numeric components.</summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    private const int ComponentCount = 4;
    private readonly int[] _components;

    private ReleaseVersion(int[] components, string? prerelease)
    {
        _components = components;
        Prerelease = prerelease;
    }

    public string Normalized => string.Join('.', _components);

    public string? Prerelease { get; }

    public string Display
    {
        get
        {
            var count = _components[3] == 0 ? 3 : 4;
            var text = string.Join('.', _components.Take(count));
            return Prerelease is null ? text : $"{text}-{Prerelease}";
        }
    }

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            value = value[..plus];
        }

        string? prerelease = null;
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = value[(dash + 1)..];
            value = value[..dash];
            if (string.IsNullOrWhiteSpace(prerelease))
            {
                return false;
            }
        }

        var pieces = value.Split('.', StringSplitOptions.TrimEntries);
        if (pieces.Length is < 1 or > ComponentCount)
        {
            return false;
        }

        var components = new int[ComponentCount];
        for (var index = 0; index < pieces.Length; index++)
        {
            if (!int.TryParse(pieces[index], out var component) || component < 0)
            {
                return false;
            }

            components[index] = component;
        }

        version = new ReleaseVersion(components, prerelease);
        return true;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        for (var index = 0; index < ComponentCount; index++)
        {
            var difference = _components[index].CompareTo(other._components[index]);
            if (difference != 0)
            {
                return difference;
            }
        }

        if (Prerelease is null && other.Prerelease is not null)
        {
            return 1;
        }

        if (Prerelease is not null && other.Prerelease is null)
        {
            return -1;
        }

        return string.CompareOrdinal(Prerelease, other.Prerelease);
    }

    public bool Equals(ReleaseVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? value) => value is ReleaseVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Normalized, Prerelease);

    public override string ToString() => Display;

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;

    public static bool operator ==(ReleaseVersion? left, ReleaseVersion? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(ReleaseVersion? left, ReleaseVersion? right) => !(left == right);
}
