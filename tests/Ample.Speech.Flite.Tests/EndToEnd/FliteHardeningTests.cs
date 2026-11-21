using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Xunit.Abstractions;

namespace Ample.Speech.Flite.Tests.EndToEnd;

/// <summary>
/// Checks that the corpus of the C program cannot see: the float arithmetic of the durations and the pitch must give the same bits on every processor (the JIT of .NET never
/// fuses a multiply and an add, and every float step is cast as in C), and a huge text must be refused or finished in bounded time.
/// </summary>
public class FliteHardeningTests(ITestOutputHelper output)
{
    private static FliteEngine Engine => FliteEngine.Default;

    /// <summary>
    /// Pins a few renderings as bit patterns: the end sample of every segment (the durations are float sums, cast to samples) and the SHA-256 of all samples (the F0 targets are float
    /// and double expressions, the pitch marks follow from them). The values were made on x64 and equal the C program
    /// (the sample digests were checked against `flite -t` of the Ubuntu package, kal16); an arm64 machine that gives other values has a float
    /// step that the JIT or the hardware does differently, and this test names the text.
    /// </summary>
    [Theory]
    [InlineData("Hello world.", 1.1f, 95f, 11f, "EFC8257209DED1B8", "EFFD93A6355D273C")]
    [InlineData("The quick brown fox jumps over the lazy dog, 3 times at 10:45 on 12/25/2024.", 1.1f, 95f, 11f, "370137163D95C84F", "2971312D15E9B411")]
    [InlineData("Mister Jones paid $1,234.56 for 7 books; was it worth it?", 0.8f, 140f, 25f, "04092F0DC4056384", "2C12D3AF37CD466B")]
    public void Float_results_are_pinned_as_bit_patterns(string text, float stretch, float mean, float stddev, string segmentsHash, string sampleHash)
    {
        FliteRenderResult result = Engine.Render(text, new FliteOptions { DurationStretch = stretch, F0Mean = mean, F0Stddev = stddev });
        string segments = string.Join(' ', result.Segments.Select(s => s.Phone + ":" + s.EndSample.ToString(CultureInfo.InvariantCulture)));
        string segmentDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(segments)))[..16];
        byte[] bytes = new byte[result.Samples.Length * 2];
        Buffer.BlockCopy(result.Samples, 0, bytes, 0, bytes.Length);
        string samplesDigest = Convert.ToHexString(SHA256.HashData(bytes))[..16];
        output.WriteLine($"{text}\n  segments {segmentDigest}\n  samples  {samplesDigest}");
        Assert.Equal(segmentsHash, segmentDigest);
        Assert.Equal(sampleHash, samplesDigest);
    }

    /// <summary>
    /// A text far above what the allowed waveform can hold is refused before the text analysis walks through it (a megabyte took about 24 s to analyse and was refused at the end), and a
    /// text at the edge of what fits is still spoken.
    /// </summary>
    [Fact]
    public void A_text_beyond_the_limit_is_refused_at_once_and_a_cancelled_one_stops()
    {
        string huge = string.Concat(Enumerable.Repeat("word, ", FliteOptions.DefaultMaxTextBytes / 6));
        Stopwatch clock = Stopwatch.StartNew();
        Assert.Throws<SpeechResourceLimitException>(() => FliteSpeechEngine.Render(new SpeechRequest(huge, 16_000, 1, 95) { Engine = "flite" }));
        output.WriteLine($"refused after {clock.Elapsed.TotalSeconds:F2} s");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed.TotalSeconds:F1} s");

        using CancellationTokenSource cancel = new();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Engine.Render(huge[..60_000], cancellation: cancel.Token));
    }

    /// <summary>
    /// <c>atoi</c> of glibc saturates a number that does not fit a long (<c>strtol</c>): the numbers of twenty digits or more in a fraction or a range must be compared like the C program does,
    /// not wrapped. The digests are those of <c>flite -t</c> of the Ubuntu package (kal16).
    /// </summary>
    [Theory]
    [InlineData("Share 10000000000000000000000/5 of it.", "ea290a3db565fcb6")]
    [InlineData("Share 5/10000000000000000000000 of it.", "67d5507dce981345")]
    [InlineData("Share 1/99999999999999999999999 of it.", "7a110ad59135b9ed")]
    [InlineData("Years 99999999999999999999-5 are odd.", "245a85c2b8a662fb")]
    [InlineData("Years 5-99999999999999999999 are odd.", "deb2e247f42b20ce")]
    [InlineData("Years 18446744073709551617/18446744073709551618 rise.", "0b4f12946b8eade2")]
    public void A_number_beyond_the_long_range_is_compared_like_glibc_does(string text, string expected)
    {
        FliteRenderResult result = Engine.Render(text);
        byte[] bytes = new byte[result.Samples.Length * 2];
        Buffer.BlockCopy(result.Samples, 0, bytes, 0, bytes.Length);
        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant());
    }

    [Theory]
    [InlineData("  42abc", 42)]
    [InlineData("-7", -7)]
    [InlineData("+9223372036854775807", -1)]
    [InlineData("9223372036854775808", -1)]
    [InlineData("18446744073709551617", -1)]
    [InlineData("-9223372036854775808", 0)]
    [InlineData("-9223372036854775809", 0)]
    [InlineData("4294967297", 1)]
    [InlineData("", 0)]
    public void Atoi_follows_strtol_and_keeps_the_low_32_bits(string text, int expected) => Assert.Equal(expected, Ample.Speech.Flite.Text.ByteText.Atoi(text));
}
