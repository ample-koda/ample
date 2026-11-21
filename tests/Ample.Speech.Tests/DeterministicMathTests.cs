using System.Numerics;
using Xunit.Abstractions;

namespace Ample.Speech.Tests;

/// <summary>
/// <see cref="DeterministicMath"/> must give the correctly rounded value on every machine. The reference here is made without any library: the functions are summed in 320-bit
/// fixed point with <see cref="BigInteger"/> and rounded to a double once, to even. A result that equals it equals it on Windows, Linux and arm64 alike, because the reference is
/// a mathematical value and not the answer of a C library.
/// </summary>
public class DeterministicMathTests(ITestOutputHelper output)
{
    private const int F = 320;   // fractional bits of the reference
    private static readonly BigInteger One = BigInteger.One << F;

    // the reference constants, summed from series
    private static readonly BigInteger RefLn2 = 2 * AtanhInverse(3);
    private static readonly BigInteger RefLn10 = (3 * RefLn2) + (2 * AtanhInverse(9));
    private static readonly BigInteger RefPi = (16 * AtanInverse(5)) - (4 * AtanInverse(239));

    /// <summary>atanh(1/n) in fixed point.</summary>
    private static BigInteger AtanhInverse(int n)
    {
        BigInteger power = One / n, square = n * n, sum = 0;
        for (int k = 0; power != 0; k++)
        {
            sum += power / (2 * k + 1);
            power /= square;
        }

        return sum;
    }

    /// <summary>atan(1/n) in fixed point.</summary>
    private static BigInteger AtanInverse(int n)
    {
        BigInteger power = One / n, square = n * n, sum = 0;
        for (int k = 0; power != 0; k++)
        {
            BigInteger term = power / (2 * k + 1);
            sum += k % 2 == 0 ? term : -term;
            power /= square;
        }

        return sum;
    }

    /// <summary>A double as an exact fixed-point number.</summary>
    private static BigInteger Fixed(double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        int exponent = (int)((bits >> 52) & 0x7FF);
        long fraction = bits & 0xFFFFFFFFFFFFFL;
        BigInteger mantissa = exponent == 0 ? fraction : fraction | (1L << 52);
        int shift = (exponent == 0 ? -1074 : exponent - 1075) + F;
        BigInteger result = shift >= 0 ? mantissa << shift : mantissa >> -shift;
        Assert.True(shift >= 0 || (mantissa & ((BigInteger.One << -shift) - 1)) == 0, "the double is exact in the fixed point");
        return bits < 0 ? -result : result;
    }

    /// <summary>The double nearest to mantissa times 2 to the power exponent (ties to even).</summary>
    private static double ToDouble(BigInteger mantissa, int exponent)
    {
        if (mantissa.IsZero) return 0;
        bool negative = mantissa.Sign < 0;
        BigInteger magnitude = BigInteger.Abs(mantissa);
        int length = (int)magnitude.GetBitLength();
        if (length > 53)
        {
            int shift = length - 53;
            BigInteger quotient = magnitude >> shift;
            BigInteger rest = magnitude - (quotient << shift);
            BigInteger half = BigInteger.One << (shift - 1);
            if (rest > half || (rest == half && !quotient.IsEven)) quotient += 1;
            magnitude = quotient;
            exponent += shift;
        }

        double value = Math.ScaleB((double)magnitude, exponent);
        return negative ? -value : value;
    }

    /// <summary>The quotient rounded down (BigInteger division truncates towards zero).</summary>
    private static BigInteger FloorDiv(BigInteger a, BigInteger b)
    {
        BigInteger q = BigInteger.DivRem(a, b, out BigInteger rest);
        return !rest.IsZero && (a.Sign < 0) != (b.Sign < 0) ? q - 1 : q;
    }

    /// <summary>e to the power of a fixed-point number, as a double.</summary>
    private static double RefExp(BigInteger x)
    {
        BigInteger k = FloorDiv((x * 2) + RefLn2, 2 * RefLn2);   // round(x / ln 2)
        BigInteger r = x - k * RefLn2;
        BigInteger term = One, sum = One;
        for (int n = 1; term != 0; n++)
        {
            term = (term * r >> F) / n;
            sum += term;
        }

        return ToDouble(sum, (int)k - F);
    }

    private static (double Sin, double Cos) RefSinCos(double angle)
    {
        BigInteger x = Fixed(angle), x2 = x * x >> F;
        BigInteger sinTerm = x, sinSum = x, cosTerm = One, cosSum = One;
        for (int k = 1; sinTerm != 0 || cosTerm != 0; k++)
        {
            cosTerm = -(cosTerm * x2 >> F) / ((2 * k - 1) * (2 * k));
            cosSum += cosTerm;
            sinTerm = -(sinTerm * x2 >> F) / ((2 * k) * (2 * k + 1));
            sinSum += sinTerm;
        }

        return (ToDouble(sinSum, -F), ToDouble(cosSum, -F));
    }

    private static string Bits(double value) => BitConverter.DoubleToInt64Bits(value).ToString("X16", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void The_constants_are_ln_2_ln_10_and_two_pi_to_106_bits()
    {
        Check("ln 2", DeterministicMath.Ln2Constant, RefLn2);
        Check("ln 10", DeterministicMath.Ln10Constant, RefLn10);
        Check("2 pi", DeterministicMath.TwoPiConstant, 2 * RefPi);

        static void Check(string name, (double Hi, double Lo) constant, BigInteger reference)
        {
            Assert.Equal(ToDouble(reference, -F), constant.Hi);   // the high part is the nearest double
            Assert.Equal(ToDouble(reference - Fixed(constant.Hi), -F), constant.Lo);   // the low part is the nearest double of what is left
            BigInteger error = BigInteger.Abs(Fixed(constant.Hi) + Fixed(constant.Lo) - reference);
            Assert.True(error < (One >> 104), $"{name} is off by more than 2^-104");
        }
    }

    [Fact]
    public void Exp10_is_the_correctly_rounded_gain_for_every_tenth_of_a_decibel()
    {
        int different = 0, total = 0;
        for (int tenth = -600; tenth <= 120; tenth++)
        {
            foreach (double divisor in new[] { 20.0, 40.0 })
            {
                double x = tenth / 10.0 / divisor;   // as ApplyGain (dB / 20) and the brightness shelf (dB / 40) make it
                double expected = RefExp(Fixed(x) * RefLn10 >> F);
                double actual = DeterministicMath.Exp10(x);
                Assert.True(expected == actual, $"10^{x:R}: expected {Bits(expected)}, got {Bits(actual)}");
                total++;
                if (Math.Pow(10, x) != actual) different++;
            }
        }

        output.WriteLine($"Math.Pow(10, x) is not the correctly rounded value in {different} of {total} cases on this machine");
    }

    [Fact]
    public void Exp2_is_the_correctly_rounded_pitch_of_every_note_distance()
    {
        int different = 0, total = 0;
        for (int distance = -127; distance <= 127; distance++)
        {
            double x = distance / 12d;   // as the pitch of a note is made
            double expected = RefExp(Fixed(x) * RefLn2 >> F);
            double actual = DeterministicMath.Exp2(x);
            Assert.True(expected == actual, $"2^({distance}/12): expected {Bits(expected)}, got {Bits(actual)}");
            total++;
            if (Math.Pow(2, x) != actual) different++;
        }

        for (int cents = -1200; cents <= 1200; cents += 7)
        {
            double x = cents / 1200d;
            Assert.Equal(RefExp(Fixed(x) * RefLn2 >> F), DeterministicMath.Exp2(x));
        }

        output.WriteLine($"Math.Pow(2, x) is not the correctly rounded value in {different} of {total} cases on this machine");
    }

    [Fact]
    public void Exp_is_the_correctly_rounded_value_over_a_sweep()
    {
        for (int i = -4000; i <= 4000; i += 13)
        {
            double x = i / 97.0;
            Assert.Equal(RefExp(Fixed(x)), DeterministicMath.Exp(x));
        }
    }

    [Fact]
    public void Sine_and_cosine_are_correctly_rounded_for_the_shelf_of_every_sample_rate_and_over_a_sweep()
    {
        List<double> angles = [];
        foreach (int rate in new[] { 8000, 11025, 16000, 22050, 32000, 44100, 48000, 88200, 96000 }) angles.Add(2 * Math.PI * 4000 / rate);   // as the shelf makes w0
        for (int i = 1; i <= 460; i++) angles.Add(i * 0.0137);
        int different = 0;
        foreach (double angle in angles)
        {
            (double sin, double cos) = RefSinCos(angle);
            Assert.True(sin == DeterministicMath.Sin(angle), $"sin {angle:R}: expected {Bits(sin)}, got {Bits(DeterministicMath.Sin(angle))}");
            Assert.True(cos == DeterministicMath.Cos(angle), $"cos {angle:R}: expected {Bits(cos)}, got {Bits(DeterministicMath.Cos(angle))}");
            if (Math.Sin(angle) != sin || Math.Cos(angle) != cos) different++;
        }

        output.WriteLine($"Math.Sin or Math.Cos is not the correctly rounded value in {different} of {angles.Count} angles on this machine");
    }

    [Fact]
    public void Exact_cases_stay_exact_and_a_reduced_angle_matches_the_unreduced_one()
    {
        Assert.Equal(1.0, DeterministicMath.Exp(0));
        Assert.Equal(1.0, DeterministicMath.Exp10(0));
        Assert.Equal(1.0, DeterministicMath.Exp2(0));
        for (int octave = -20; octave <= 20; octave++) Assert.Equal(Math.ScaleB(1.0, octave), DeterministicMath.Exp2(octave));
        Assert.Equal(0.0, DeterministicMath.Sin(0));
        Assert.Equal(1.0, DeterministicMath.Cos(0));
        Assert.Equal(10.0, DeterministicMath.Exp10(1));
        Assert.Equal(100.0, DeterministicMath.Exp10(2));
        // the angle 100 radians is reduced by whole turns: the reference sums the series of the unreduced angle
        (double sin, double cos) = RefSinCos(100);
        Assert.Equal(sin, DeterministicMath.Sin(100));
        Assert.Equal(cos, DeterministicMath.Cos(100));
    }

    [Fact]
    public void An_argument_outside_the_domain_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DeterministicMath.Exp(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeterministicMath.Exp(701));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeterministicMath.Exp2(1010));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeterministicMath.Exp10(305));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeterministicMath.Sin(double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeterministicMath.Cos(2_000_000));
    }
}
