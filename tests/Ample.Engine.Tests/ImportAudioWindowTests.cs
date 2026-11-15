using Ample.Audio.Engine;
using Ample.Language;

namespace Ample.Core.Tests;

/// <summary>A short excerpt of a long WAV file reads only its own frames and gives exactly what reading the whole file gives (issue 293).</summary>
public sealed class ImportAudioWindowTests : IDisposable
{
    private const int Rate = 16000;
    private readonly string folder = Directory.CreateTempSubdirectory("ample-import-window-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
            // a locked file must not fail the run
        }
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static short[] Ramp(int frames) => [.. Enumerable.Range(0, frames).Select(i => (short)((i * 7) % 20000 - 10000))];

    private static byte[] Stereo(int frames) => StereoWav.Encode(new StereoBuffer(Ramp(frames), [.. Ramp(frames).Select(v => (short)-v)]), Rate);

    private async Task<byte[]> RenderAsync(string file, Dictionary<string, string> parameters, bool windowedReads, int bars = 2)
    {
        BlockNode block = new() { Type = "import-audio", Name = "clip" };
        block.Parameters.Add(new Parameter("file", "\"" + file + "\"", 0));
        foreach ((string name, string value) in parameters)
        {
            block.Parameters.Add(new Parameter(name, value, 0));
        }

        RenderRequest request = new(block, EngineRig.Spec.Templates["import-audio"], Rate, Rate, 1, bars, new HashSet<int> { 1, 2 }, new Dictionary<string, byte[]>(), 1, folder);
        IReadOnlyList<RenderOutput> outputs = await new ImportAudioRenderer { WindowedReads = windowedReads }.RenderAsync(request, CancellationToken.None);
        return outputs.Single(o => o.Role == "audio").Data;
    }

    [Fact]
    public void Only_the_frames_of_the_excerpt_are_read_and_they_start_where_the_file_says()
    {
        string path = Write("long.wav", Wav.Encode(Ramp(160000), Rate));   // ten seconds

        (byte[] wav, int start, bool windowed) = AudioFile.ReadWavWindow(path, Rate, "2", "3");

        Assert.True(windowed);
        Assert.Equal(2 * Rate, start);
        short[] samples = Wav.Decode(wav).Samples;
        Assert.Equal(Rate, samples.Length);
        Assert.Equal(Ramp(160000)[(2 * Rate)..(3 * Rate)], samples);
    }

    [Fact]
    public void Another_rate_no_excerpt_or_the_whole_file_is_read_as_before()
    {
        string path = Write("short.wav", Wav.Encode(Ramp(32000), Rate));

        Assert.False(AudioFile.ReadWavWindow(path, 44100, "0.5", "1").Windowed);   // another rate: resampling needs the whole file and its context
        Assert.False(AudioFile.ReadWavWindow(path, Rate, string.Empty, string.Empty).Windowed);
        Assert.False(AudioFile.ReadWavWindow(path, Rate, "0", "2").Windowed);   // that is the whole file
    }

    [Theory]
    [InlineData("1", "2", "", "")]
    [InlineData("0.5", "", "", "")]
    [InlineData("", "1.5", "", "")]
    [InlineData("1", "2.2", "on", "")]
    [InlineData("0.25", "1.75", "on", "0.1")]
    public async Task The_result_is_the_same_sample_for_sample_as_with_the_whole_file_read_mono(string start, string end, string loop, string crossfade)
    {
        Write("mono.wav", Wav.Encode(Ramp(80000), Rate));
        Dictionary<string, string> parameters = new() { ["start"] = start, ["end"] = end, ["loop"] = loop, ["crossfade"] = crossfade };

        byte[] windowed = await RenderAsync("mono.wav", parameters, windowedReads: true);
        byte[] whole = await RenderAsync("mono.wav", parameters, windowedReads: false);

        Assert.Equal(whole, windowed);
    }

    [Fact]
    public async Task The_result_is_the_same_for_a_stereo_file_and_for_loop_points_measured_in_the_file()
    {
        Write("stereo.wav", Stereo(80000));
        Dictionary<string, string> parameters = new() { ["start"] = "1", ["end"] = "4", ["loop"] = "on", ["loop_start"] = "1.5", ["loop_end"] = "3.5", ["crossfade"] = "0.05" };

        byte[] windowed = await RenderAsync("stereo.wav", parameters, windowedReads: true);
        byte[] whole = await RenderAsync("stereo.wav", parameters, windowedReads: false);

        Assert.Equal(whole, windowed);
    }
}
