using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ample.Speech.Flite.Tests.EndToEnd;

/// <summary>
/// Bit-pattern pins for the places where a float or double step could differ between processors or between runtime libraries (the machines that matter: x64 and arm64, Windows and
/// Linux): the extremes of the duration and the pitch (checked against the C program), and the speech layer around the exact path: the resampling, the brightness shelf, the gain and
/// the pitch of a note. The speech layer uses <see cref="DeterministicMath"/> and only additions, multiplications and divisions, so its pins are the same on every machine; a
/// machine that gives other digests names the case that differs.
/// </summary>
public class FliteFloatPinTests
{
    private static string Digest(short[] samples)
    {
        byte[] bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
    }

    /// <summary>
    /// The extremes of the settings against the Ubuntu <c>flite</c> (kal16, <c>--setf duration_stretch=…</c>, <c>int_f0_target_mean=…</c>, <c>int_f0_target_stddev=…</c>): the shortest and
    /// the longest stretch, the lowest and the highest mean pitch, a wide and a flat contour. These are the float expressions of the durations and the F0 targets in their widest range.
    /// </summary>
    [Theory]
    [InlineData("Hello world.", 0.55f, 50f, 0f, 17280, "2587a0e1d48bc165")]
    [InlineData("Hello world.", 2.2f, 480f, 0f, 69332, "0a38afc5436e8fb4")]
    [InlineData("A long sentence, with several clauses; numbers like 1999 and 2.5 percent, and an abbreviation: Dr. Smith.", 1.1f, 95f, 60f, 288536, "5299f54d0fdf569a")]
    [InlineData("Round and round the rugged rock the ragged rascal ran.", 1.5f, 300f, 5f, 120062, "86a86ef4416fcc77")]
    [InlineData("One two three four five six seven eight nine ten.", 1.0f, 140f, 0f, 87084, "eae881124ca5b275")]
    public void The_extremes_of_the_settings_equal_the_C_program(string text, float stretch, float mean, float stddev, int bytes, string expected)
    {
        FliteRenderResult result = FliteEngine.Default.Render(text, new FliteOptions { DurationStretch = stretch, F0Mean = mean, F0Stddev = stddev });
        Assert.Equal(bytes, result.Samples.Length * 2);   // the wav of the C program has 44 bytes more
        Assert.Equal(expected, Digest(result.Samples));
    }

    private const string Sentence = "Hello world, this is the flite voice speaking a longer sentence.";

    /// <summary>
    /// The resampling from 16 kHz to the rate of the block: the position of every output sample is a double division, the sample a rounded interpolation, so the digests are the
    /// same on every machine.
    /// </summary>
    [Theory]
    [InlineData(22050, 92110, "2bee1311b084efdb")]
    [InlineData(44100, 184219, "cb6f4e54f589b378")]
    [InlineData(48000, 200511, "29e60c692b4a7fdd")]
    public void The_resampling_gives_the_same_samples_on_every_machine(int rate, int length, string expected)
    {
        SpeechResult made = FliteSpeechEngine.Render(new SpeechRequest(Sentence, rate, 1, 95) { Engine = "flite" });
        Assert.Equal(length, made.Samples.Length);
        Assert.Equal(expected, Digest(made.Samples));
    }

    /// <summary>
    /// The brightness shelf: its coefficients need a power, a sine and a cosine; <see cref="DeterministicMath"/> makes them without a library, so the rounded samples are the same on
    /// every machine (the sine of the angle at 32 kHz is one that the C library of Windows does not round correctly).
    /// </summary>
    [Theory]
    [InlineData(16000, 6.0, "4efcfef1523edde9")]
    [InlineData(22050, 6.0, "df9592d2fa0a8d6d")]
    [InlineData(44100, 12.0, "69caec378dc224a1")]
    [InlineData(48000, 3.5, "db1b58ca0d1e96b8")]
    public void The_brightness_shelf_gives_the_same_samples_on_every_machine(int rate, double gainDb, string expected)
    {
        short[] samples = (short[])FliteEngine.Default.Render(Sentence).Samples.Clone();
        FliteShaping.Brighten(samples, rate, gainDb);
        Assert.Equal(expected, Digest(samples));
    }

    /// <summary>The pitch of a note in words mode: 2 to the power of the distance in octaves, made without a library function; the 128 values are pinned by one digest and two are exact.</summary>
    [Fact]
    public void The_pitch_of_a_note_is_the_same_on_every_machine()
    {
        StringBuilder all = new();
        for (int midi = 0; midi <= 127; midi++) all.Append(BitConverter.DoubleToInt64Bits(FliteSpeechEngine.NotePitchHz(midi, 57, 220)).ToString("X16", CultureInfo.InvariantCulture));
        Assert.Equal("79d3eb02f355c694", Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(all.ToString())))[..16].ToLowerInvariant());
        Assert.Equal(0x40705A0250C2B956L, BitConverter.DoubleToInt64Bits(FliteSpeechEngine.NotePitchHz(60, 57, 220)));   // C4 above A3: 220 Hz times 2^(3/12)
        Assert.Equal(0x407B800000000000L, BitConverter.DoubleToInt64Bits(FliteSpeechEngine.NotePitchHz(69, 57, 220)));   // an octave up: exactly 440 Hz
        Assert.Equal(700.0, FliteSpeechEngine.NotePitchHz(127, 57, 220));   // kept from 45 to 700 Hz, as the mapper of Humanico does
        Assert.Equal(45.0, FliteSpeechEngine.NotePitchHz(0, 57, 220));
    }

    /// <summary>The pitch of a note is the one the mapper of Humanico makes, to the last bit or one bit away (the mapper uses the library function): the deterministic one is the right one.</summary>
    [Fact]
    public void The_pitch_of_a_note_stays_within_one_bit_of_the_mapper_of_humanico()
    {
        for (int midi = 0; midi <= 127; midi++)
        {
            double mapped = HumanicoNotePitchMapper.Map(new SpeechNoteEvent(0, midi, 100, 0, midi), 57, 220).FundamentalHz;
            double ours = FliteSpeechEngine.NotePitchHz(midi, 57, 220);
            Assert.True(Math.Abs(BitConverter.DoubleToInt64Bits(mapped) - BitConverter.DoubleToInt64Bits(ours)) <= 1, $"note {midi}: {mapped:R} against {ours:R}");
        }
    }
}
