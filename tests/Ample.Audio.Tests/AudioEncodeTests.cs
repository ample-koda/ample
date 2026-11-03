using Ample.Audio.Engine;

namespace Ample.Core.Tests;

public class AudioEncodeTests
{
    [Theory]
    [InlineData("a.mp3", "libmp3lame")]
    [InlineData("a.MP3", "libmp3lame")]
    [InlineData("a.flac", "flac")]
    public void The_extension_picks_the_encoder(string target, string codec) =>
        Assert.Contains(codec, AudioFile.EncodeArguments("in.wav", target)!);

    [Fact]
    public void A_wav_target_is_written_as_it_is()
    {
        Assert.Null(AudioFile.EncodeArguments("in.wav", "a.wav"));
        string target = Path.Combine(Path.GetTempPath(), "ample-enc-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            byte[] bytes = Wav.Encode(new short[100], 44100);
            AudioFile.WriteAs(bytes, target);
            Assert.Equal(bytes, File.ReadAllBytes(target));
        }
        finally
        {
            File.Delete(target);
        }
    }
}
