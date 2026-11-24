using Ample.Audio.Engine;

namespace Ample.Core.Tests;

/// <summary>
/// WAV files with odd headers (security findings 58 to 60): a sample rate of 1 Hz made the resampling write n * 65,536 samples (1.3 GB for 10,000 samples),
/// a chunk size near 2^31 overflowed an int in the walk over the chunks, and the data size of the header was used as the size of an array.
/// </summary>
[Trait("Category", "Sampler")]
public sealed class HostileWavTests
{
    private static byte[] Wav(int rate, int samples, int? extraChunkSize = null, int? dataSizeClaim = null)
    {
        using MemoryStream ms = new();
        using BinaryWriter w = new(ms);
        int dataBytes = samples * 2;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes + (extraChunkSize is null ? 0 : 8));
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        if (extraChunkSize is { } size)
        {
            w.Write("LIST"u8.ToArray());
            w.Write(size);   // a size that is far more than the file has
        }

        w.Write("data"u8.ToArray());
        w.Write(dataSizeClaim ?? dataBytes);
        for (int i = 0; i < samples; i++)
        {
            w.Write((short)(i % 200 * 100));
        }

        return ms.ToArray();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(999)]
    [InlineData(768001)]
    [InlineData(int.MaxValue)]
    public void A_sample_rate_that_no_sample_has_is_refused_before_anything_is_resampled(int rate)
    {
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<InvalidDataException>(() => SampleSet.ReadWav(Wav(rate, 10_000), 44100));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed.TotalSeconds:0.0} s");
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(8000)]
    [InlineData(22050)]
    [InlineData(48000)]
    [InlineData(96000)]
    public void The_rates_of_real_files_are_still_read_and_resampled(int rate)
    {
        short[] read = SampleSet.ReadWav(Wav(rate, 4000), 44100);
        Assert.NotEmpty(read);
        Assert.Equal(rate, SampleSet.NativeRateOf(Wav(rate, 4000)));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MaxValue - 1)]
    [InlineData(-1)]
    public void A_chunk_with_a_size_near_two_to_the_power_31_does_not_throw_an_overflow(int size)
    {
        byte[] wav = Wav(44100, 100, extraChunkSize: size);
        _ = SampleSet.NativeRateOf(wav);   // returned 0 or the rate, never an ArgumentOutOfRangeException
        // the data chunk lies behind the odd chunk, so it is not reached: the file is not a playable WAV, and that is an InvalidDataException like any other
        Assert.Throws<InvalidDataException>(() => SampleSet.ReadWav(wav, 44100));
        Assert.Throws<InvalidDataException>(() => StereoWav.Decode(wav));   // a mono file: "not a stereo WAV", not an overflow
    }

    [Fact]
    public void A_data_size_in_the_header_that_claims_more_than_the_file_has_does_not_allocate_the_claim()
    {
        byte[] wav = Wav(44100, 100, dataSizeClaim: 1_800_000_000);
        (short[] samples, int rate) = Ample.Audio.Engine.Wav.Decode(wav);
        Assert.Equal(44100, rate);
        Assert.True(samples.Length <= 100, $"{samples.Length} samples from a file of 100");
        (short[] negative, _) = Ample.Audio.Engine.Wav.Decode(Wav(44100, 100, dataSizeClaim: -5));
        Assert.Empty(negative);
    }
}
