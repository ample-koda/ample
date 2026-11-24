namespace Ample.Audio.Engine;

/// <summary>
/// The functions the effects need that must give the same bits on every machine (bug 30 of the Studio review, 2026-11-24): <see cref="Math.Tan(double)"/>,
/// <see cref="Math.Pow(double, double)"/>, <see cref="Math.Sin(double)"/> and the others call the C library of the platform, and Windows, glibc and an arm64 machine
/// differ in the last bit; a filter coefficient one bit away can round a sample the other way, and the same song then gives other bytes.
/// Everything here is made from additions, subtractions, multiplications and divisions (exact IEEE operations on every processor; the JIT of .NET does not fuse a
/// multiplication and an addition) and from operations that are exact by nature (rounding to a whole number, scaling by a power of 2). The results are within a few
/// units in the last place of the true value, not always correctly rounded: that is not needed, only that they are the same everywhere. They are about as fast as the
/// library functions (the correctly rounded <c>DeterministicMath</c> of the speech layer was so slow per sample that the Delay Llama demo took more than 90 seconds).
/// </summary>
internal static class EngineMath
{
    // pi / 2 in three parts (Cody and Waite), so k * pi / 2 is taken off exactly enough for |x| up to about a million radians
    private const double PiOver2High = 1.5707963267341256;
    private const double PiOver2Mid = 6.077100506506192e-11;
    private const double PiOver2Low = 2.0222662487959506e-21;
    private const double TwoOverPi = 0.6366197723675814;

    // ln 2 in two parts: k * ln 2 is exact in the high part for |k| < 2^20
    private const double Ln2High = 0.6931471803691238;
    private const double Ln2Low = 1.9082149292705877e-10;
    private const double OneOverLn2 = 1.4426950408889634;
    private const double Ln2 = 0.6931471805599453;
    private const double Ln10 = 2.302585092994046;

    /// <summary>The sine of <paramref name="x"/> (radians).</summary>
    internal static double Sin(double x) => SinCos(x).Sin;

    /// <summary>The cosine of <paramref name="x"/> (radians).</summary>
    internal static double Cos(double x) => SinCos(x).Cos;

    /// <summary>The tangent of <paramref name="x"/> (radians): the sine over the cosine.</summary>
    internal static double Tan(double x)
    {
        (double s, double c) = SinCos(x);
        return s / c;
    }

    /// <summary>The sine and the cosine of <paramref name="x"/> (radians) together.</summary>
    internal static (double Sin, double Cos) SinCos(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) return (double.NaN, double.NaN);
        double k = Math.Round(x * TwoOverPi);
        double r = ((x - (k * PiOver2High)) - (k * PiOver2Mid)) - (k * PiOver2Low);   // |r| <= pi / 4
        double s = SinPoly(r), c = CosPoly(r);
        int quadrant = (int)(((long)k % 4 + 4) % 4);
        return quadrant switch
        {
            0 => (s, c),
            1 => (c, -s),
            2 => (-s, -c),
            _ => (-c, s),
        };
    }

    // Taylor series of sin to degree 17 and of cos to degree 18, in Horner form: on |r| <= pi / 4 the first left-out term is below 1e-18
    private static double SinPoly(double r)
    {
        double r2 = r * r;
        double p = -1.0 / 355687428096000;   // -1/17!
        p = (p * r2) + (1.0 / 1307674368000);   // 1/15!
        p = (p * r2) - (1.0 / 6227020800);   // 1/13!
        p = (p * r2) + (1.0 / 39916800);   // 1/11!
        p = (p * r2) - (1.0 / 362880);   // 1/9!
        p = (p * r2) + (1.0 / 5040);   // 1/7!
        p = (p * r2) - (1.0 / 120);   // 1/5!
        p = (p * r2) + (1.0 / 6);   // 1/3!
        return r - (r * r2 * p);
    }

    private static double CosPoly(double r)
    {
        double r2 = r * r;
        double p = -1.0 / 6402373705728000;   // 1/18!
        p = (p * r2) + (1.0 / 20922789888000);   // 1/16!
        p = (p * r2) - (1.0 / 87178291200);   // 1/14!
        p = (p * r2) + (1.0 / 479001600);   // 1/12!
        p = (p * r2) - (1.0 / 3628800);   // 1/10!
        p = (p * r2) + (1.0 / 40320);   // 1/8!
        p = (p * r2) - (1.0 / 720);   // 1/6!
        p = (p * r2) + (1.0 / 24);   // 1/4!
        p = (p * r2) - 0.5;   // 1/2!
        return 1 + (r2 * p);
    }

    /// <summary>e to the power <paramref name="x"/>; 0 below -745 and infinity above 709.</summary>
    internal static double Exp(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        if (x < -745) return 0;
        if (x > 709) return double.PositiveInfinity;
        double k = Math.Round(x * OneOverLn2);
        double r = (x - (k * Ln2High)) - (k * Ln2Low);   // |r| <= ln 2 / 2
        // e^r by its Taylor series to degree 13 (the next term is below 3e-18 on |r| <= 0.35)
        double p = 1.0 / 6227020800;
        p = (p * r) + (1.0 / 479001600);
        p = (p * r) + (1.0 / 39916800);
        p = (p * r) + (1.0 / 3628800);
        p = (p * r) + (1.0 / 362880);
        p = (p * r) + (1.0 / 40320);
        p = (p * r) + (1.0 / 5040);
        p = (p * r) + (1.0 / 720);
        p = (p * r) + (1.0 / 120);
        p = (p * r) + (1.0 / 24);
        p = (p * r) + (1.0 / 6);
        p = (p * r) + 0.5;
        p = (p * r) + 1;
        p = (p * r) + 1;
        return Math.ScaleB(p, (int)k);
    }

    /// <summary>10 to the power <paramref name="x"/> (a level in decibels is 10^(dB / 20)).</summary>
    internal static double Exp10(double x) => Exp(x * Ln10);

    /// <summary>2 to the power <paramref name="x"/> (octaves).</summary>
    internal static double Exp2(double x) => Exp(x * Ln2);

    /// <summary>The hyperbolic tangent: (e^2x - 1) / (e^2x + 1), with the sign kept and large values at ±1.</summary>
    internal static double Tanh(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        double a = Math.Abs(x);
        if (a > 20) return Math.Sign(x);
        double e = Exp(2 * a);
        return Math.Sign(x) * ((e - 1) / (e + 1));
    }

    /// <summary>
    /// The natural logarithm of a positive <paramref name="x"/>: the exponent of 2 is taken apart exactly, and Newton's method on <see cref="Exp"/> finds the rest
    /// (a fixed number of steps, so the same steps run on every machine). Not a number for less than zero.
    /// </summary>
    internal static double Log(double x)
    {
        if (double.IsNaN(x) || x < 0) return double.NaN;
        if (x == 0) return double.NegativeInfinity;
        if (double.IsPositiveInfinity(x)) return double.PositiveInfinity;
        int exponent = Math.ILogB(x);
        double m = Math.ScaleB(x, -exponent);   // 1 <= m < 2, exact
        double y = (m - 1) - ((m - 1) * (m - 1) / 2);   // a start close to ln m
        for (int step = 0; step < 5; step++) y = y - 1 + (m / Exp(y));
        return y + (exponent * Ln2);
    }

    /// <summary><paramref name="a"/> to the power <paramref name="b"/> for a positive base: e^(b ln a); 1 when b is 0, 0 for a base of 0 and a positive power.</summary>
    internal static double Pow(double a, double b)
    {
        if (b == 0) return 1;
        if (a == 0) return b > 0 ? 0 : double.PositiveInfinity;
        if (a < 0) return double.NaN;
        return Exp(b * Log(a));
    }
}
