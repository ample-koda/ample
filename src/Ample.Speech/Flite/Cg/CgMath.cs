namespace Ample.Speech.Flite.Cg;

// Port of ARM optimized-routines (commit 20859d3fd9e797dde592c489c8ef7d383f778620), math/exp.c and math/log.c, with the data of exp_data.c and log_data.c (CgMathTables.cs).
// Copyright (c) 2018-2026, Arm Limited. SPDX-License-Identifier: MIT OR Apache-2.0 WITH LLVM-exception (the MIT licence text is in LICENSES/arm-optimized-routines.txt).
// Translated to C# and modified: only the double precision exp and log for the build without fused multiply-add (128 table entries), only the results; no error numbers, no exceptions.
// These are the algorithms that glibc 2.28 and later use for exp and log, so the C program of Flite on a glibc system without FMA gives the same bits.

/// <summary>The exp and log of the clustergen vocoder, made from <c>+ - * /</c>, comparisons and tables only: never the maths library of the platform
/// (its last bits differ between libraries, processors and operating systems, and the same song must give the same bytes everywhere).</summary>
internal static partial class CgMath
{
    private const int ExpTableSize = 128;
    private const int LogTableSize = 128;
    private const ulong LogOff = 0x3fe6000000000000UL;

    private static double AsDouble(ulong bits) => BitConverter.UInt64BitsToDouble(bits);

    private static ulong AsUInt64(double value) => BitConverter.DoubleToUInt64Bits(value);

    // the values are made in the static constructor: the arrays of bit patterns are fields of the other part of this class,
    // and the order in which the field initializers of the parts run is not defined
    private static readonly double ExpInvLn2NValue;
    private static readonly double ExpNegLn2HiNValue;
    private static readonly double ExpNegLn2LoNValue;
    private static readonly double ExpShiftValue;
    private static readonly double[] ExpPoly;
    private static readonly double[] LogPoly;
    private static readonly double[] LogPoly1;
    private static readonly double Ln2Hi;
    private static readonly double Ln2Lo;
    private static readonly double[] InvC;
    private static readonly double[] LogC;
    private static readonly double[] Chi;
    private static readonly double[] Clo;

    static CgMath()
    {
        ExpInvLn2NValue = AsDouble(ExpInvLn2N);
        ExpNegLn2HiNValue = AsDouble(ExpNegLn2HiN);
        ExpNegLn2LoNValue = AsDouble(ExpNegLn2LoN);
        ExpShiftValue = AsDouble(ExpShift);
        ExpPoly = [.. ExpPolyBits.Select(AsDouble)];
        LogPoly = [.. LogPolyBits.Select(AsDouble)];
        LogPoly1 = [.. LogPoly1Bits.Select(AsDouble)];
        Ln2Hi = AsDouble(LogLn2Hi);
        Ln2Lo = AsDouble(LogLn2Lo);
        InvC = new double[LogTableSize];
        LogC = new double[LogTableSize];
        Chi = new double[LogTableSize];
        Clo = new double[LogTableSize];
        for (int i = 0; i < LogTableSize; i++)
        {
            InvC[i] = AsDouble(LogTableBits[2 * i]);
            LogC[i] = AsDouble(LogTableBits[(2 * i) + 1]);
            Chi[i] = AsDouble(LogTable2Bits[2 * i]);
            Clo[i] = AsDouble(LogTable2Bits[(2 * i) + 1]);
        }
    }

    /// <summary>e to the power <paramref name="x"/>.</summary>
    /// <param name="x">The exponent.</param>
    public static double Exp(double x)
    {
        ulong xBits = AsUInt64(x);
        uint abstop = (uint)((xBits >> 52) & 0x7ff);
        // top12(0x1p-54) = 0x3c9, top12(512.0) = 0x408, top12(1024.0) = 0x409, top12(infinity) = 0x7ff
        if (unchecked(abstop - 0x3c9u) >= 0x408u - 0x3c9u)
        {
            if (unchecked(abstop - 0x3c9u) >= 0x80000000u) return 1.0 + x;   // tiny x: avoid a spurious underflow
            if (abstop >= 0x409u)
            {
                if (xBits == 0xfff0000000000000UL) return 0.0;   // minus infinity
                if (abstop >= 0x7ffu) return 1.0 + x;            // infinity or not a number
                return (xBits >> 63) != 0 ? 0.0 : double.PositiveInfinity;
            }

            abstop = 0;   // a large x is handled in the special case below
        }

        // exp(x) = 2^(k/N) * exp(r), with exp(r) in [2^(-1/2N), 2^(1/2N)]; x = ln2/N * k + r, with an integer k and r in [-ln2/2N, ln2/2N]
        double z = ExpInvLn2NValue * x;
        double kd = z + ExpShiftValue;
        ulong ki = AsUInt64(kd);
        kd -= ExpShiftValue;
        double r = x + (kd * ExpNegLn2HiNValue) + (kd * ExpNegLn2LoNValue);
        ulong idx = 2 * (ki % ExpTableSize);
        ulong top = ki << (52 - 7);
        double tail = AsDouble(ExpTableBits[(int)idx]);
        ulong sbits = ExpTableBits[(int)idx + 1] + top;
        double r2 = r * r;
        double tmp = tail + r + (r2 * (ExpPoly[0] + (r * ExpPoly[1]))) + (r2 * r2 * (ExpPoly[2] + (r * ExpPoly[3])));
        if (abstop == 0) return SpecialCase(tmp, sbits, ki);
        double scale = AsDouble(sbits);
        return scale + (scale * tmp);
    }

    /// <summary>The result of <c>scale * (1 + tmp)</c> where the intermediate value may overflow or underflow (the case of a large or very negative x).</summary>
    private static double SpecialCase(double tmp, ulong sbits, ulong ki)
    {
        double twoPow1009 = AsDouble((1009UL + 1023UL) << 52);       // 0x1p1009
        double twoPowMinus1022 = AsDouble(1UL << 52);                // 0x1p-1022
        double scale, y;
        if ((ki & 0x80000000UL) == 0)
        {
            // k > 0: the exponent of the scale may have overflowed by at most 460
            sbits -= 1009UL << 52;
            scale = AsDouble(sbits);
            y = twoPow1009 * (scale + (scale * tmp));
            return y;
        }

        // k < 0: take care in the subnormal range
        sbits += 1022UL << 52;
        scale = AsDouble(sbits);
        y = scale + (scale * tmp);
        if (y < 1.0)
        {
            // round y to the right precision before scaling it into the subnormal range, to avoid a double rounding
            double lo = scale - y + (scale * tmp);
            double hi = 1.0 + y;
            lo = 1.0 - hi + y + lo;
            y = (hi + lo) - 1.0;
            if (y == 0.0) y = 0.0;   // avoid minus zero
        }

        y = twoPowMinus1022 * y;
        return y;
    }

    /// <summary>The natural logarithm of <paramref name="x"/>.</summary>
    /// <param name="x">The argument.</param>
    public static double Log(double x)
    {
        ulong ix = AsUInt64(x);
        uint top = (uint)(ix >> 48);
        // the inputs close to 1.0: LO = bits of 1.0 - 0x1p-4, HI = bits of 1.0 + 0x1.09p-4
        const ulong Lo = 0x3fee000000000000UL;
        const ulong Hi = 0x3ff1090000000000UL;
        if (unchecked(ix - Lo) < Hi - Lo)
        {
            if (ix == 0x3ff0000000000000UL) return 0;
            double rr = x - 1.0;
            double rr2 = rr * rr;
            double rr3 = rr * rr2;
            double[] b = LogPoly1;
            double y1 = rr3 * (b[1] + (rr * b[2]) + (rr2 * b[3]) + (rr3 * (b[4] + (rr * b[5]) + (rr2 * b[6]) + (rr3 * (b[7] + (rr * b[8]) + (rr2 * b[9]) + (rr3 * b[10]))))));
            // worst-case error 0.507 ulp (N > 64)
            double w1 = rr * 134217728.0;   // 0x1p27
            double rhi = rr + w1 - w1;
            double rlo = rr - rhi;
            w1 = rhi * rhi * b[0];   // b[0] == -0.5
            double hi1 = rr + w1;
            double lo1 = rr - hi1 + w1;
            lo1 += b[0] * rlo * (rhi + rr);
            y1 += lo1;
            y1 += hi1;
            return y1;
        }

        if (unchecked(top - 0x0010u) >= 0x7ff0u - 0x0010u)
        {
            // x < 0x1p-1022, infinity or not a number
            if (ix * 2 == 0) return double.NegativeInfinity;
            if (ix == 0x7ff0000000000000UL) return x;
            if ((top & 0x8000) != 0 || (top & 0x7ff0) == 0x7ff0) return double.NaN;
            // x is subnormal: normalize it
            ix = AsUInt64(x * 4503599627370496.0);   // 0x1p52
            ix -= 52UL << 52;
        }

        // x = 2^k z, with z in [OFF, 2*OFF) and exact; the range is split into N subintervals, c is near the center of the one that holds z
        ulong tmp = ix - LogOff;
        int i = (int)((tmp >> (52 - 7)) % LogTableSize);
        int k = (int)((long)tmp >> 52);   // arithmetic shift
        ulong iz = ix - (tmp & (0xfffUL << 52));
        double invc = InvC[i];
        double logc = LogC[i];
        double z = AsDouble(iz);

        // log(x) = log1p(z/c - 1) + log(c) + k * Ln2; r ~= z/c - 1, |r| < 1/(2N)
        double r = (z - Chi[i] - Clo[i]) * invc;
        double kd = k;
        double w = (kd * Ln2Hi) + logc;
        double hi = w + r;
        double lo = w - hi + r + (kd * Ln2Lo);
        double r2 = r * r;
        double[] a = LogPoly;
        return lo + (r2 * a[0]) + (r * r2 * (a[1] + (r * a[2]) + (r2 * (a[3] + (r * a[4]))))) + hi;
    }
}
