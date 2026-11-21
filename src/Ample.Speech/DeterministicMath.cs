namespace Ample.Speech;

/// <summary>
/// The functions of the speech layer that must give the same bits on every machine: powers of 2 and 10 (a gain in dB, a note to a pitch) and the sine and cosine (the
/// coefficients of a filter). <see cref="Math.Pow(double, double)"/>, <see cref="Math.Sin(double)"/> and the others call the C library of the platform, and the libraries
/// of Windows, glibc and an arm64 machine may differ in the last bit. Here every result is made with additions, subtractions, multiplications and divisions only, in
/// double-double arithmetic (about 106 bits), and then rounded once: those four operations are exact IEEE operations on every processor (the JIT of .NET does not fuse
/// a multiplication and an addition), so the result is the correctly rounded value of the function, on any machine, and it never depends on a library.
/// </summary>
public static class DeterministicMath
{
    private const double Split = 134217729.0;   // 2^27 + 1: Dekker's split of a double in two halves of 26 bits

    private static readonly Dd Ln2 = new(0.6931471805599453, 2.3190468138462996e-17);
    private static readonly Dd Ln10 = new(2.302585092994046, -2.1707562233822494e-16);
    private static readonly Dd TwoPi = new(6.283185307179586, 2.4492935982947064e-16);

    /// <summary>Gets ln 2 as a double-double (high and low part), for the tests that check the constants.</summary>
    internal static (double Hi, double Lo) Ln2Constant => (Ln2.Hi, Ln2.Lo);

    /// <summary>Gets ln 10 as a double-double (high and low part), for the tests that check the constants.</summary>
    internal static (double Hi, double Lo) Ln10Constant => (Ln10.Hi, Ln10.Lo);

    /// <summary>Gets 2 pi as a double-double (high and low part), for the tests that check the constants.</summary>
    internal static (double Hi, double Lo) TwoPiConstant => (TwoPi.Hi, TwoPi.Lo);

    /// <summary>The correctly rounded value of e to the power <paramref name="x"/>.</summary>
    /// <param name="x">The exponent, a number from -700 to 700.</param>
    /// <returns>e<sup>x</sup>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The exponent is not a number, or is beyond 700.</exception>
    public static double Exp(double x)
    {
        CheckExponent(x);
        return ExpOf(new Dd(x, 0));
    }

    /// <summary>The correctly rounded value of 2 to the power <paramref name="x"/> (a note that is <c>x</c> octaves away).</summary>
    /// <param name="x">The exponent, a number from -1000 to 1000.</param>
    /// <returns>2<sup>x</sup>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The exponent is not a number, or is beyond 1000.</exception>
    public static double Exp2(double x)
    {
        CheckExponent(x * 0.6931471805599453);
        return ExpOf(MulD(Ln2, x));
    }

    /// <summary>The correctly rounded value of 10 to the power <paramref name="x"/> (a gain: 10<sup>dB/20</sup>).</summary>
    /// <param name="x">The exponent, a number from -300 to 300.</param>
    /// <returns>10<sup>x</sup>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The exponent is not a number, or is beyond 300.</exception>
    public static double Exp10(double x)
    {
        CheckExponent(x * 2.302585092994046);
        return ExpOf(MulD(Ln10, x));
    }

    /// <summary>The correctly rounded sine of <paramref name="x"/> radians.</summary>
    /// <param name="x">The angle, a number from -1,000,000 to 1,000,000.</param>
    /// <returns>sin x.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The angle is not a number, or is beyond a million radians.</exception>
    public static double Sin(double x) => SinCos(x).Sin;

    /// <summary>The correctly rounded cosine of <paramref name="x"/> radians.</summary>
    /// <param name="x">The angle, a number from -1,000,000 to 1,000,000.</param>
    /// <returns>cos x.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The angle is not a number, or is beyond a million radians.</exception>
    public static double Cos(double x) => SinCos(x).Cos;

    private static void CheckExponent(double x)
    {
        if (!double.IsFinite(x) || Math.Abs(x) > 700) throw new ArgumentOutOfRangeException(nameof(x), "The exponent must be a number whose power fits a double (from about -700 to 700 in e).");
    }

    /// <summary>e to the power of a double-double: the argument is reduced to x = k ln 2 + r with |r| at most 0.35, the series of r is summed, and the result is scaled by 2 to the k.</summary>
    private static double ExpOf(Dd x)
    {
        double k = Math.Round(x.Hi / Ln2.Hi);   // to even: an exact operation
        Dd r = Sub(x, MulD(Ln2, k));
        Dd term = new(1, 0), sum = new(1, 0);
        for (int n = 1; n < 60; n++)
        {
            term = DivD(Mul(term, r), n);
            sum = Add(sum, term);
            if (Math.Abs(term.Hi) < 1e-40) break;
        }

        return Math.ScaleB(sum.Hi, (int)k);   // the sum is normalised: its high part is the rounded value of both parts; ScaleB only moves the exponent
    }

    /// <summary>The sine and the cosine: the angle is reduced to -pi..pi, and both series are summed in double-double.</summary>
    private static (double Sin, double Cos) SinCos(double x)
    {
        if (!double.IsFinite(x) || Math.Abs(x) > 1_000_000) throw new ArgumentOutOfRangeException(nameof(x), "The angle must be a number from -1,000,000 to 1,000,000 radians.");
        double turns = Math.Round(x / TwoPi.Hi);
        Dd r = Sub(new Dd(x, 0), MulD(TwoPi, turns));
        Dd r2 = Mul(r, r);
        Dd sinTerm = r, sinSum = r, cosTerm = new(1, 0), cosSum = new(1, 0);
        for (int k = 1; k < 80; k++)
        {
            cosTerm = Neg(DivD(Mul(cosTerm, r2), (2.0 * k - 1) * (2.0 * k)));
            cosSum = Add(cosSum, cosTerm);
            sinTerm = Neg(DivD(Mul(sinTerm, r2), (2.0 * k) * (2.0 * k + 1)));
            sinSum = Add(sinSum, sinTerm);
            if (Math.Abs(cosTerm.Hi) < 1e-40 && Math.Abs(sinTerm.Hi) < 1e-40) break;
        }

        return (sinSum.Hi, cosSum.Hi);
    }

    /// <summary>A number as the sum of two doubles, the second at most half a unit of the last place of the first.</summary>
    /// <param name="Hi">The high part: the number rounded to a double.</param>
    /// <param name="Lo">The low part: what the high part leaves out.</param>
    private readonly record struct Dd(double Hi, double Lo);

    private static Dd TwoSum(double a, double b)
    {
        double s = a + b;
        double bb = s - a;
        return new Dd(s, (a - (s - bb)) + (b - bb));
    }

    private static Dd QuickTwoSum(double a, double b)
    {
        double s = a + b;
        return new Dd(s, b - (s - a));
    }

    private static Dd TwoProd(double a, double b)
    {
        double p = a * b;
        double ta = Split * a;
        double ah = ta - (ta - a);
        double al = a - ah;
        double tb = Split * b;
        double bh = tb - (tb - b);
        double bl = b - bh;
        double err = ((ah * bh - p) + ah * bl + al * bh) + al * bl;
        return new Dd(p, err);
    }

    private static Dd Neg(Dd a) => new(-a.Hi, -a.Lo);

    private static Dd Add(Dd a, Dd b)
    {
        Dd s = TwoSum(a.Hi, b.Hi);
        Dd t = TwoSum(a.Lo, b.Lo);
        s = QuickTwoSum(s.Hi, s.Lo + t.Hi);
        return QuickTwoSum(s.Hi, s.Lo + t.Lo);
    }

    private static Dd Sub(Dd a, Dd b) => Add(a, Neg(b));

    private static Dd Mul(Dd a, Dd b)
    {
        Dd p = TwoProd(a.Hi, b.Hi);
        return QuickTwoSum(p.Hi, p.Lo + (a.Hi * b.Lo + a.Lo * b.Hi));
    }

    private static Dd MulD(Dd a, double b)
    {
        Dd p = TwoProd(a.Hi, b);
        return QuickTwoSum(p.Hi, p.Lo + a.Lo * b);
    }

    private static Dd DivD(Dd a, double b)
    {
        double q1 = a.Hi / b;
        Dd r = Sub(a, TwoProd(q1, b));
        double q2 = r.Hi / b;
        r = Sub(r, TwoProd(q2, b));
        double q3 = r.Hi / b;
        return Add(QuickTwoSum(q1, q2), new Dd(q3, 0));
    }
}
