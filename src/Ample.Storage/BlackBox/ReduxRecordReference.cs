using System.Globalization;

namespace Ample.Storage.BlackBox;

/// <summary>Formats stable AMPLE Redux record references from the journal sequence.</summary>
public static class ReduxRecordReference
{
    /// <summary>The largest sequence that fits in the fixed sixteen-digit reference.</summary>
    public const long MaximumSequence = 9_999_999_999_999_999;

    /// <summary>Returns the fixed, copyable reference such as <c>AR-0000000000000001</c>.</summary>
    public static string Padded(long sequence)
    {
        Validate(sequence);
        return "AR-" + sequence.ToString("D16", CultureInfo.InvariantCulture);
    }

    /// <summary>Returns the compact UI reference such as <c>AR-1</c>.</summary>
    public static string Compact(long sequence)
    {
        Validate(sequence);
        return "AR-" + sequence.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Parses a padded or compact AR reference, without accepting non-ASCII digits.</summary>
    public static bool TryParse(string reference, out long sequence)
    {
        sequence = 0;
        if (reference.Length < 4 || !reference.StartsWith("AR-", StringComparison.OrdinalIgnoreCase)) return false;
        ReadOnlySpan<char> digits = reference.AsSpan(3);
        if (digits.Length is < 1 or > 16) return false;
        foreach (char digit in digits) if (digit is < '0' or > '9') return false;
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out sequence) && sequence is > 0 and <= MaximumSequence;
    }

    private static void Validate(long sequence)
    {
        if (sequence is < 1 or > MaximumSequence) throw new ArgumentOutOfRangeException(nameof(sequence));
    }
}
