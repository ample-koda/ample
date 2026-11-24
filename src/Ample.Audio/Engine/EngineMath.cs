using Ample.Speech;

namespace Ample.Audio.Engine;

/// <summary>
/// The functions the effects need that must give the same bits on every machine (Studio bug hunt 2026-11-24, bug 30): <see cref="Math.Tan(double)"/>,
/// <see cref="Math.Pow(double, double)"/>, <see cref="Math.Sin(double)"/> and the others call the C library of the platform, and Windows, glibc and an arm64 machine
/// differ in the last bit; a filter coefficient one bit away can round a sample the other way. Everything here is made from <see cref="DeterministicMath"/> (correctly
/// rounded exp, sine and cosine) and the four exact IEEE operations, so it is the same everywhere. The results are not always correctly rounded (a tangent is a sine over
/// a cosine), but they never depend on a library.
/// </summary>
internal static class EngineMath
{
    /// <summary>The sine of <paramref name="x"/> (radians).</summary>
    internal static double Sin(double x) => DeterministicMath.Sin(x);

    /// <summary>The cosine of <paramref name="x"/> (radians).</summary>
    internal static double Cos(double x) => DeterministicMath.Cos(x);

    /// <summary>The tangent of <paramref name="x"/> (radians): the sine over the cosine.</summary>
    internal static double Tan(double x) => DeterministicMath.Sin(x) / DeterministicMath.Cos(x);

    /// <summary>e to the power <paramref name="x"/>; 0 below -700 and the largest double above 700 (the range of <see cref="DeterministicMath.Exp"/>).</summary>
    internal static double Exp(double x) => double.IsNaN(x) ? double.NaN : x < -700 ? 0 : x > 700 ? double.MaxValue : DeterministicMath.Exp(x);

    /// <summary>10 to the power <paramref name="x"/> (a level in decibels is 10^(dB / 20)).</summary>
    internal static double Exp10(double x) => DeterministicMath.Exp10(Math.Clamp(x, -300, 300));

    /// <summary>2 to the power <paramref name="x"/> (octaves).</summary>
    internal static double Exp2(double x) => DeterministicMath.Exp2(Math.Clamp(x, -1000, 1000));

    /// <summary>The hyperbolic tangent: (e^2x - 1) / (e^2x + 1), with the sign kept and large values at ±1.</summary>
    internal static double Tanh(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        double a = Math.Abs(x);
        if (a > 20) return Math.Sign(x);
        double e = DeterministicMath.Exp(2 * a);
        return Math.Sign(x) * ((e - 1) / (e + 1));
    }

    /// <summary>
    /// The natural logarithm of a positive <paramref name="x"/>: the exponent of 2 is taken apart exactly, and Newton's method on <see cref="DeterministicMath.Exp"/>
    /// finds the rest (a fixed number of steps, so the same steps run on every machine). Not a number for zero or less.
    /// </summary>
    internal static double Log(double x)
    {
        if (!(x > 0) || double.IsInfinity(x)) return x == 0 ? double.NegativeInfinity : double.IsPositiveInfinity(x) ? double.PositiveInfinity : double.NaN;
        int exponent = Math.ILogB(x);
        double m = Math.ScaleB(x, -exponent);   // 1 <= m < 2, exact
        double ln2 = 0.6931471805599453;
        double y = (m - 1) - ((m - 1) * (m - 1) / 2);   // a start close to ln m
        for (int step = 0; step < 6; step++) y = y - 1 + (m / DeterministicMath.Exp(y));
        return y + (exponent * ln2);
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
