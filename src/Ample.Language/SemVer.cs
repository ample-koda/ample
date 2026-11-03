using System.Globalization;
using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>
/// A semantic version of a component (<c>1.2.3</c>, <c>4.0.0-beta.1</c>): major, minor, patch and the pre-release identifiers. A written version with fewer parts (<c>1.8</c>) keeps how many parts were given,
/// so that a request can mean "the 1.8 line" (see <see cref="VersionRequest"/>). Ordering follows semver.org: a pre-release is lower than its release, identifiers compare numerically when both are numbers.
/// </summary>
/// <param name="Major">The major version.</param>
/// <param name="Minor">The minor version.</param>
/// <param name="Patch">The patch version.</param>
/// <param name="Pre">The pre-release identifiers (empty for a release).</param>
/// <param name="Parts">How many numbers were written (1 to 3).</param>
public sealed record SemVer(int Major, int Minor, int Patch, IReadOnlyList<string> Pre, int Parts = 3) : IComparable<SemVer>
{
    private static readonly Regex Pattern = new(@"^v?(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>Gets whether this is a pre-release (<c>4.0.0-beta.1</c>).</summary>
    public bool IsPrerelease => Pre.Count > 0;

    /// <summary>Reads a version.</summary>
    /// <param name="text">The text, for example <c>1.8</c>, <c>2.3.1</c> or <c>4.0.0-beta.1</c>.</param>
    /// <param name="version">The version.</param>
    /// <returns>True when the text is a version.</returns>
    public static bool TryParse(string text, out SemVer version)
    {
        version = new SemVer(0, 0, 0, []);
        Match m = Pattern.Match(text.Trim().Trim('"'));
        if (!m.Success) return false;
        int parts = m.Groups[3].Success ? 3 : m.Groups[2].Success ? 2 : 1;
        static int N(Group g) => g.Success && int.TryParse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;
        version = new SemVer(N(m.Groups[1]), N(m.Groups[2]), N(m.Groups[3]), m.Groups[4].Success ? [.. m.Groups[4].Value.Split('.')] : [], parts);
        return true;
    }

    /// <summary>Reads a version that must be well formed.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The version.</returns>
    public static SemVer Parse(string text) => TryParse(text, out SemVer v) ? v : throw new FormatException($"\"{text}\" is not a version (1.2.3, 1.2 or 4.0.0-beta.1).");

    /// <summary>Compares two versions by the rules of semver.org (the number of parts written does not count).</summary>
    /// <param name="other">The other version.</param>
    /// <returns>Negative when this is lower.</returns>
    public int CompareTo(SemVer? other)
    {
        if (other is null) return 1;
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (Pre.Count == 0 && other.Pre.Count == 0) return 0;
        if (Pre.Count == 0) return 1;
        if (other.Pre.Count == 0) return -1;
        for (int i = 0; i < Math.Min(Pre.Count, other.Pre.Count); i++)
        {
            bool aNumber = int.TryParse(Pre[i], NumberStyles.None, CultureInfo.InvariantCulture, out int a), bNumber = int.TryParse(other.Pre[i], NumberStyles.None, CultureInfo.InvariantCulture, out int b);
            int d = aNumber && bNumber ? a.CompareTo(b) : aNumber ? -1 : bNumber ? 1 : string.CompareOrdinal(Pre[i], other.Pre[i]);
            if (d != 0) return d;
        }

        return Pre.Count.CompareTo(other.Pre.Count);
    }

    /// <summary>Whether two versions are the same version (the number of parts written does not count).</summary>
    /// <param name="other">The other version.</param>
    /// <returns>True when equal.</returns>
    public bool SameAs(SemVer other) => CompareTo(other) == 0;

    /// <summary>Gets the version as <c>major.minor.patch[-pre]</c>, always with three numbers.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => $"{Major.ToString(CultureInfo.InvariantCulture)}.{Minor.ToString(CultureInfo.InvariantCulture)}.{Patch.ToString(CultureInfo.InvariantCulture)}" + (Pre.Count == 0 ? string.Empty : "-" + string.Join('.', Pre));

    /// <summary>Whether the left version is lower.</summary>
    /// <param name="left">The left version.</param>
    /// <param name="right">The right version.</param>
    /// <returns>True when lower.</returns>
    public static bool operator <(SemVer? left, SemVer? right) => left is null ? right is not null : left.CompareTo(right) < 0;

    /// <summary>Whether the left version is lower or the same.</summary>
    /// <param name="left">The left version.</param>
    /// <param name="right">The right version.</param>
    /// <returns>True when lower or the same.</returns>
    public static bool operator <=(SemVer? left, SemVer? right) => left is null || left.CompareTo(right) <= 0;

    /// <summary>Whether the left version is higher.</summary>
    /// <param name="left">The left version.</param>
    /// <param name="right">The right version.</param>
    /// <returns>True when higher.</returns>
    public static bool operator >(SemVer? left, SemVer? right) => left is not null && left.CompareTo(right) > 0;

    /// <summary>Whether the left version is higher or the same.</summary>
    /// <param name="left">The left version.</param>
    /// <param name="right">The right version.</param>
    /// <returns>True when higher or the same.</returns>
    public static bool operator >=(SemVer? left, SemVer? right) => left is null ? right is null : left.CompareTo(right) >= 0;

    /// <inheritdoc/>
    public bool Equals(SemVer? other) => other is not null && Major == other.Major && Minor == other.Minor && Patch == other.Patch && Pre.SequenceEqual(other.Pre);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', Pre));
}

/// <summary>
/// Which versions of a component a reference asks for (the part after the <c>@</c> of an instance header): nothing (the default: the <c>current</c> one, else the highest release), an exact version (<c>1.8.0</c>,
/// <c>4.0.0-beta.1</c>), a line (<c>1.8</c> is any <c>1.8.x</c> release, <c>1</c> any <c>1.x.x</c> release), a compatible range (<c>^1.2</c>: from 1.2.0 up to, not including, 2.0.0; for 0.x up to the next minor) or a
/// patch range (<c>~1.2</c>: from 1.2.0 up to, not including, 1.3.0). A pre-release is only matched by a request that names that very pre-release.
/// </summary>
public sealed record VersionRequest
{
    private static readonly Regex Pattern = new(@"^(?<op>[\^~]?)(?<v>.+)$", RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>Gets the text as written (empty for the default).</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Gets the operator: empty, <c>^</c> or <c>~</c>.</summary>
    public string Operator { get; init; } = string.Empty;

    /// <summary>Gets the version asked for, or null for the default.</summary>
    public SemVer? Version { get; init; }

    /// <summary>Gets whether the default is asked for (no version written).</summary>
    public bool IsDefault => Version is null;

    /// <summary>Reads what follows the <c>@</c>.</summary>
    /// <param name="text">The text, or empty.</param>
    /// <param name="request">The request.</param>
    /// <returns>True when it is a good request (empty is the default and is good).</returns>
    public static bool TryParse(string text, out VersionRequest request)
    {
        request = new VersionRequest();
        string t = text.Trim().Trim('"');
        if (t.Length == 0) return true;
        Match m = Pattern.Match(t);
        if (!m.Success || !SemVer.TryParse(m.Groups["v"].Value, out SemVer v)) return false;
        request = new VersionRequest { Text = t, Operator = m.Groups["op"].Value, Version = v };
        return true;
    }

    /// <summary>Whether a version is one the request accepts (the default accepts every release; the choice among them is made by the resolver).</summary>
    /// <param name="v">The version.</param>
    /// <returns>True when it matches.</returns>
    public bool Matches(SemVer v)
    {
        if (Version is null) return !v.IsPrerelease;
        SemVer w = Version;
        if (v.IsPrerelease) return Operator.Length == 0 && w.IsPrerelease && w.Parts == 3 && v.SameAs(w);
        if (Operator == "^")
        {
            if (v.CompareTo(new SemVer(w.Major, w.Minor, w.Patch, [])) < 0) return false;
            return w.Major > 0 ? v.Major == w.Major : w.Minor > 0 ? v.Major == 0 && v.Minor == w.Minor : v.Major == 0 && v.Minor == 0 && v.Patch == w.Patch;
        }

        if (Operator == "~") return v.CompareTo(new SemVer(w.Major, w.Minor, w.Patch, [])) >= 0 && v.Major == w.Major && v.Minor == w.Minor;
        if (w.Parts == 3) return v.SameAs(w);
        if (w.Parts == 2) return v.Major == w.Major && v.Minor == w.Minor;
        return v.Major == w.Major;
    }

    /// <summary>Gets the request as written (<c>default</c> for none).</summary>
    /// <returns>The text.</returns>
    public override string ToString() => Version is null ? "default" : Text;
}
