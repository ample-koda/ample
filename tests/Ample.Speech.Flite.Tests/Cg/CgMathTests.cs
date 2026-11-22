using System.Globalization;
using Ample.Speech.Flite.Cg;

namespace Ample.Speech.Flite.Tests.Cg;

/// <summary>Milestone C4 of the clustergen voices: the exp, log and rand of the vocoder are our own and equal the GNU C library (without fused multiply-add) bit for bit.
/// plan/speech/flite/reference/cg/math/mathprobe.c writes the digests of the C library over deterministic inputs (splitmix64, FNV-1a over the 8 bytes of each result).</summary>
public sealed class CgMathTests
{
    private static ulong state;

    private static ulong Next64()
    {
        unchecked
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    private static double Uniform() => (Next64() >> 11) * (1.0 / 9007199254740992.0);

    private static ulong Fnv(ulong hash, ulong value)
    {
        unchecked
        {
            for (int i = 0; i < 8; i++)
            {
                hash ^= (value >> (8 * i)) & 255;
                hash *= 1099511628211UL;
            }

            return hash;
        }
    }

    private static Dictionary<string, string> Expected()
    {
        Dictionary<string, string> expected = new(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "flite-cg-math-digests.txt")))
        {
            string[] parts = line.Split(' ');
            expected[parts[0]] = string.Join(' ', parts.Skip(1));
        }

        return expected;
    }

    private static string Digest(ulong hash, int count) => hash.ToString("x16", CultureInfo.InvariantCulture) + " " + count.ToString(CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("exp-30..30", -30.0, 30.0, 1000000)]
    [InlineData("exp-700..700", -700.0, 700.0, 1000000)]
    [InlineData("exp-745.5..-700", -745.5, -700.0, 500000)]
    [InlineData("exp700..710", 700.0, 710.0, 200000)]
    [InlineData("exp-1e-5..1e-5", -1e-5, 1e-5, 200000)]
    [InlineData("exp-12..2", -12.0, 2.0, 1000000)]
    public void Exp_equals_the_C_library_without_fma(string name, double a, double b, int count)
    {
        state = 12345;
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < count; i++)
        {
            double x = a + ((b - a) * Uniform());
            hash = Fnv(hash, BitConverter.DoubleToUInt64Bits(CgMath.Exp(x)));
        }

        Assert.Equal(Expected()[name], Digest(hash, count));
    }

    [Theory]
    [InlineData("log0.001..1000", 0.001, 1000.0, 1000000)]
    [InlineData("log0.9..1.1", 0.9, 1.1, 1000000)]
    [InlineData("log1e-300..1e300", 1e-300, 1e300, 200000)]
    public void Log_equals_the_C_library_without_fma(string name, double a, double b, int count)
    {
        state = 777;
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < count; i++)
        {
            double x = a + ((b - a) * Uniform());
            hash = Fnv(hash, BitConverter.DoubleToUInt64Bits(CgMath.Log(x)));
        }

        Assert.Equal(Expected()[name], Digest(hash, count));
    }

    [Theory]
    [InlineData("log-normal-bits", false, 1000000)]
    [InlineData("log-subnormal-bits", true, 200000)]
    public void Log_equals_the_C_library_on_random_bit_patterns(string name, bool subnormal, int count)
    {
        state = 99;
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < count; i++)
        {
            ulong r = Next64();
            ulong bits = subnormal ? (r % 0x000fffffffffffffUL) + 1 : 0x0010000000000000UL + (r % 0x7fe0000000000000UL);
            hash = Fnv(hash, BitConverter.DoubleToUInt64Bits(CgMath.Log(BitConverter.UInt64BitsToDouble(bits))));
        }

        Assert.Equal(Expected()[name], Digest(hash, count));
    }

    [Theory]
    [InlineData(1u, 1000000)]
    [InlineData(42u, 1000000)]
    [InlineData(4294967295u, 500000)]
    public void Rand_equals_the_sequence_of_the_C_library(uint seed, int count)
    {
        GlibcRandom random = new(seed);
        ulong hash = 14695981039346656037UL;
        int[] first = new int[3];
        for (int i = 0; i < count; i++)
        {
            int value = random.Next();
            if (i < 3) first[i] = value;
            hash = Fnv(hash, (uint)value);
        }

        string expected = Expected()[$"rand-{seed}"];
        Assert.StartsWith(hash.ToString("x16", CultureInfo.InvariantCulture) + " " + count.ToString(CultureInfo.InvariantCulture), expected, StringComparison.Ordinal);
        Assert.EndsWith($"first {first[0]} {first[1]} {first[2]}", expected, StringComparison.Ordinal);
    }

    [Fact]
    public void The_special_arguments_give_the_values_of_the_C_library()
    {
        Assert.Equal(1.0, CgMath.Exp(0.0));
        Assert.Equal(0.0, CgMath.Exp(double.NegativeInfinity));
        Assert.Equal(double.PositiveInfinity, CgMath.Exp(double.PositiveInfinity));
        Assert.Equal(double.PositiveInfinity, CgMath.Exp(800.0));
        Assert.Equal(0.0, CgMath.Exp(-800.0));
        Assert.True(double.IsNaN(CgMath.Exp(double.NaN)));
        Assert.Equal(0.0, CgMath.Log(1.0));
        Assert.Equal(double.NegativeInfinity, CgMath.Log(0.0));
        Assert.True(double.IsNaN(CgMath.Log(-1.0)));
        Assert.Equal(double.PositiveInfinity, CgMath.Log(double.PositiveInfinity));
    }
}
