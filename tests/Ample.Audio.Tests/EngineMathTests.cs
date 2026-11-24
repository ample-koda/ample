using System.Text.RegularExpressions;
using Ample.Audio.Engine;

namespace Ample.Audio.Tests;

/// <summary>
/// The effects make their coefficients and waves with <see cref="EngineMath"/>, never with the platform's maths library (Studio bug hunt 2026-11-24, bug 30): .NET on
/// Windows and V8 disagreed in the last bit for 842 of 19981 filter tangents, and a glibc or arm64 server can differ again, so the same song could give other bytes.
/// </summary>
public sealed partial class EngineMathTests
{
    private static readonly string[] EffectFiles = ["ResonantFilter.cs", "Effects.cs", "ExtraTremolo.cs", "ExtraPeq.cs", "DelayLlama.cs", "ExtraChop.cs"];

    private static string FindRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "spec-tests")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("AMPLE folder not found");
    }

    [GeneratedRegex(@"(?<![A-Za-z.])Math\.(Sin|Cos|Tan|Tanh|Pow|Exp|Log|Log10|Log2|Atan|Atan2|Sinh|Cosh|Asin|Acos|Cbrt)\(")]
    private static partial Regex PlatformMath();

    [Fact]
    public void The_effects_call_no_function_of_the_platform_maths_library()
    {
        string folder = Path.Combine(FindRoot(), "src", "Ample.Audio", "Engine");
        List<string> found = [];
        foreach (string file in EffectFiles)
        {
            string[] lines = File.ReadAllLines(Path.Combine(folder, file));
            for (int i = 0; i < lines.Length; i++)
            {
                if (PlatformMath().IsMatch(lines[i])) found.Add($"{file}:{i + 1}");
            }
        }

        Assert.Empty(found);
    }

    [Theory]
    [InlineData(0.001)]
    [InlineData(0.5)]
    [InlineData(1.2)]
    public void The_tangent_is_the_tangent(double x) => Assert.Equal(Math.Tan(x), EngineMath.Tan(x), 1e-14);

    [Theory]
    [InlineData(1e-6)]
    [InlineData(0.3)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(44100)]
    [InlineData(1e300)]
    public void The_logarithm_is_the_logarithm(double x) => Assert.Equal(Math.Log(x), EngineMath.Log(x), Math.Abs(Math.Log(x)) * 1e-14 + 1e-15);

    [Fact]
    public void Powers_tanh_and_exp_keep_their_edges()
    {
        Assert.Equal(Math.Pow(0.01 / 0.30, 0.37), EngineMath.Pow(0.01 / 0.30, 0.37), 1e-14);
        Assert.Equal(1, EngineMath.Pow(5, 0));
        Assert.Equal(0, EngineMath.Pow(0, 2));
        Assert.Equal(Math.Tanh(0.7), EngineMath.Tanh(0.7), 1e-14);
        Assert.Equal(-1, EngineMath.Tanh(-50));
        Assert.Equal(0, EngineMath.Exp(-5000));   // a decay far past its end is silence, not an exception
    }

    [Fact]
    public void Sine_cosine_and_exp_are_within_a_few_units_of_the_library_everywhere_they_are_used()
    {
        for (double x = -200; x <= 200; x += 0.0137)
        {
            Assert.Equal(Math.Sin(x), EngineMath.Sin(x), 1e-14);
            Assert.Equal(Math.Cos(x), EngineMath.Cos(x), 1e-14);
        }

        for (double x = -700; x <= 700; x += 0.731)
        {
            double expected = Math.Exp(x);
            Assert.True(Math.Abs(EngineMath.Exp(x) - expected) <= expected * 4e-16, $"exp({x})");
        }

        Assert.Equal(Math.Pow(10, -6 / 20.0), EngineMath.Exp10(-6 / 20.0), 1e-15);
        Assert.Equal(Math.Pow(2, 1.5), EngineMath.Exp2(1.5), 1e-14);
    }
}
