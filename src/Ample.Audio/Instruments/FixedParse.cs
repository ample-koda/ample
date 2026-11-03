namespace Ample.Audio.Instruments;

/// <summary>Culture-free number text to scaled integer, with no floating point.</summary>
internal static class FixedParse
{
    /// <summary>Parses "-3.25" with scale 100 to -325 (extra digits truncate, huge values saturate). False on bad text.</summary>
    public static bool TryParse(string text, long scale, out long value)
    {
        value = 0;
        var s = text.Trim();
        if (s.Length == 0) return false;
        var i = 0;
        var neg = false;
        if (s[0] == '-' || s[0] == '+') { neg = s[0] == '-'; i++; }
        long whole = 0, frac = 0, fracScale = 1;
        var digits = 0;
        var sawDot = false;
        const long Cap = 1_000_000_000_000L;
        for (; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '.' && !sawDot) { sawDot = true; continue; }
            if (c < '0' || c > '9') return false;
            digits++;
            if (!sawDot) { if (whole < Cap) whole = (whole * 10) + (c - '0'); }
            else if (fracScale < scale) { frac = (frac * 10) + (c - '0'); fracScale *= 10; }
        }
        if (digits == 0) return false;
        while (fracScale < scale) { frac *= 10; fracScale *= 10; }
        var v = (Math.Min(whole, Cap) * scale) + frac;
        value = neg ? -v : v;
        return true;
    }
}
