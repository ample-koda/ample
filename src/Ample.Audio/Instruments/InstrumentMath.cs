namespace Ample.Audio.Instruments;

/// <summary>Integer-only pitch and time conversions on top of the generated tables. Table lookups and shifts only.</summary>
internal static class InstrumentMath
{
    /// <summary>Pitch ratio 2^(cents/1200) in Q16 (65536 = 1.0). Cents are clamped to +-12000 (ten octaves).</summary>
    public static long CentsToRatioQ16(long cents)
    {
        cents = Math.Clamp(cents, -12000, 12000);
        var oct = Math.DivRem(cents, 1200, out var rem);
        if (rem < 0) { rem += 1200; oct--; }
        long v = InstrumentTables.CentsQ16[rem];
        return oct >= 0 ? v << (int)oct : RoundShift(v, (int)-oct);
    }

    /// <summary>SF2 timecents (1200 per doubling of one second) to microseconds. Clamped to the SF2 range -12000..8000.</summary>
    public static long TimecentsToMicroseconds(long timecents)
    {
        timecents = Math.Clamp(timecents, -12000, 8000);
        var oct = Math.DivRem(timecents, 1200, out var rem);
        if (rem < 0) { rem += 1200; oct--; }
        var v = (long)InstrumentTables.CentsQ16[rem] * 1_000_000;
        return oct >= 0 ? (v << (int)oct) >> 16 : RoundShift(v, 16 + (int)-oct);
    }

    private static long RoundShift(long v, int bits) => (v + (1L << (bits - 1))) >> bits;
}
