namespace Ample.Audio.Random;

/// <summary>The only place of the random code that uses floating point: the exact conversion of a 53-bit integer to a unit fraction.</summary>
public sealed partial class StableRng
{
    /// <summary>Returns the next 53 bits as an integer in <c>[0, 2^53)</c>.</summary>
    public ulong NextUnitBits() => Next() >> 11;

    /// <summary>Returns a value in <c>[0, 1)</c>: the 53-bit integer divided by 2^53. Both are exactly representable, so the result is identical everywhere.</summary>
    public double NextUnit() => NextUnitBits() * (1.0 / 9007199254740992.0);
}
