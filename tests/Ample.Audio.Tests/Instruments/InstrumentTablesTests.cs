using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Ample.Audio.Instruments;

namespace Ample.Core.Tests.Instruments;

[Trait("Category", "Instruments")]
public sealed class InstrumentTablesTests
{
    // Regenerate with: node ample/tools/gen-instrument-tables.mjs (then update this pin on purpose).
    private const string PinnedHash = "27d2279f9e1100e2b3e07ddf4fe08218c2b1da3cd31b3076c099f64d4d591fb2";

    [Fact]
    public void TableHashIsPinned()
    {
        var t = InstrumentTables.CentsQ16;
        Assert.Equal(1200, t.Length);
        var bytes = new byte[t.Length * 4];
        for (var i = 0; i < t.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), t[i]);
        Assert.Equal(PinnedHash, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.Equal(PinnedHash, InstrumentTables.CentsQ16Sha256);
    }

    [Theory]
    [InlineData(0, 65536)]
    [InlineData(1200, 131072)]
    [InlineData(-1200, 32768)]
    [InlineData(600, 92682)]
    [InlineData(2400, 262144)]
    [InlineData(-2400, 16384)]
    [InlineData(100, 69433)]
    public void CentsToRatio(long cents, long expected) => Assert.Equal(expected, InstrumentMath.CentsToRatioQ16(cents));

    [Fact]
    public void CentsAreMonotonicAndClamped()
    {
        long prev = 0;
        for (var c = -13000; c <= 13000; c += 7)
        {
            var r = InstrumentMath.CentsToRatioQ16(c);
            Assert.True(r >= prev);
            prev = r;
        }
        Assert.Equal(InstrumentMath.CentsToRatioQ16(12000), InstrumentMath.CentsToRatioQ16(99999));
    }

    [Theory]
    [InlineData(0, 1_000_000)]
    [InlineData(1200, 2_000_000)]
    [InlineData(-1200, 500_000)]
    [InlineData(-12000, 977)]
    public void TimecentsToMicroseconds(long tc, long expected) => Assert.Equal(expected, InstrumentMath.TimecentsToMicroseconds(tc));

    [Fact]
    public void LargestTimecentsIsAboutOneHundredSeconds()
    {
        var us = InstrumentMath.TimecentsToMicroseconds(8000);
        Assert.InRange(us, 101_590_000, 101_600_000);
        Assert.Equal(us, InstrumentMath.TimecentsToMicroseconds(30000));
    }

    [Fact]
    public void InstrumentSourcesHaveNoFloatingPointOrPow()
    {
        var dir = AppContext.BaseDirectory;
        string? src = null;
        for (var d = new DirectoryInfo(dir); d != null && src == null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "src", "Ample.Audio", "Instruments");
            if (Directory.Exists(candidate)) src = candidate;
        }
        Assert.NotNull(src);
        var files = Directory.GetFiles(src, "*.cs");
        Assert.NotEmpty(files);
        var bad = new Regex(@"\b(double|float|decimal|Math\.Pow|Math\.Exp|MathF)\b|\d\.\d+[fFdDmM]\b");
        foreach (var f in files)
            Assert.False(bad.IsMatch(File.ReadAllText(f)), Path.GetFileName(f));
    }
}
