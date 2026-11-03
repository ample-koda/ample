using Ample.Audio.Engine;
using Ample.Plugins.Analysis;

namespace Ample.Plugins.Tests;

[Trait("Category", "Analysis")]
public sealed class AudioInputTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-audio-input-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Fact]
    public void A_generated_pcm_wav_reports_its_exact_sample_timeline()
    {
        string path = Path.Combine(folder, "tone.wav");
        File.WriteAllBytes(path, Wav.Encode(new short[22050], 22050));
        Assert.Equal(new AudioInputInfo(22050, 1, 22050, 16, 1), AudioInput.ValidateWav(path, 1024 * 1024, 60));
    }

    [Theory]
    [InlineData("riff-size")]
    [InlineData("chunk-size")]
    [InlineData("sample-rate-overflow")]
    [InlineData("byte-rate-overflow")]
    [InlineData("duration")]
    public void Malformed_or_overlong_generated_wavs_are_refused(string fault)
    {
        byte[] wav = Wav.Encode(new short[22050], 22050);
        if (fault == "riff-size") wav[4] = 0;
        if (fault == "chunk-size") wav[40] = 0xff;
        if (fault == "sample-rate-overflow") Array.Fill(wav, byte.MaxValue, 24, 4);
        if (fault == "byte-rate-overflow") Array.Fill(wav, byte.MaxValue, 28, 4);
        string path = Path.Combine(folder, fault + ".wav");
        File.WriteAllBytes(path, wav);
        int maxSeconds = fault == "duration" ? 0 : 60;
        Assert.Throws<InvalidDataException>(() => AudioInput.ValidateWav(path, 1024 * 1024, maxSeconds));
    }

    [Fact]
    public void Files_over_the_upload_bound_are_refused_before_parsing()
    {
        string path = Path.Combine(folder, "large.wav");
        File.WriteAllBytes(path, Wav.Encode(new short[128], 22050));
        Assert.Throws<InvalidDataException>(() => AudioInput.ValidateWav(path, 44, 60));
    }
}
