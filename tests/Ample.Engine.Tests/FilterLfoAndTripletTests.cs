using Ample.Audio.Engine;
using Ample.Engine.Engine;
using Ample.Storage.Takes;

namespace Ample.Core.Tests;

/// <summary>The oscillator on the filter cutoff (the wobble) and drum rows with triplet steps.</summary>
public sealed class FilterLfoAndTripletTests
{
    private const int Bar = 88200;   // a bar at 120 BPM and 44100 Hz

    private static async Task<StereoBuffer> RenderAsync(string seq, string tail, string block)
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        Directory.CreateDirectory(Path.Combine(rig.ProjectRoot, "samples"));
        File.WriteAllBytes(Path.Combine(rig.ProjectRoot, "samples", "kick.wav"), Wav.Encode(new short[] { 30000, 20000, 10000 }, 44100));
        Directory.CreateDirectory(Path.Combine(rig.ProjectRoot, "sequences"));
        File.WriteAllText(Path.Combine(rig.ProjectRoot, "sequences", "a.seq"), seq);
        string song = "composition \"s\" { ample 1  tempo 120  master-seed 1 }\nsequencer sq at 1 for 1 {\n  file sequences/a.seq\n}\n" + tail;
        BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse(song));
        TakeRecord take = rig.Bank.GetTake(report.Blocks.Single(b => b.Block == block).TakeId!);
        using Stream data = rig.Bank.OpenData(take.Data.First(d => d.Role is "mix" or "audio" or "out").DataId);
        using MemoryStream ms = new();
        data.CopyTo(ms);
        return StereoWav.Decode(ms.ToArray()).Buffer;
    }

    [Fact]
    public async Task A_drum_row_of_twelve_steps_is_a_bar_of_eighth_triplets()
    {
        const string seq = "sequencer 1\nchannel kick drum sample=samples/kick.wav vol=0\npattern p bars=1\n  kick x...x...x...\nplaylist\n  clip p track=1 at=1 for=1\n";
        StereoBuffer mix = await RenderAsync(seq, string.Empty, "sq");
        foreach (int step in new[] { 0, 4, 8 })
        {
            Assert.NotEqual(0, mix.Left[(int)((long)step * Bar / 12) + 1]);
        }

        Assert.Equal(0, mix.Left[Bar / 4 + 10]);   // a sixteenth grid would hit here
    }

    [Fact]
    public async Task The_oscillator_moves_the_cutoff_in_time_with_the_bar()
    {
        const string seq = "sequencer 1\nchannel b bass wave=saw vol=0\npattern p bars=1\n  b 1:0:4:60:110\nplaylist\n  clip p track=1 at=1 for=1\n";
        const string tail = "filter f {\n  type lowpass\n  cutoff 120 Hz\n  lfo_rate 1/1\n  lfo_depth 6 oct\n  lfo_shape square\n  from sq.audio -> audio\n}\n";
        StereoBuffer filtered = await RenderAsync(seq, tail, "f");
        static double Rms(short[] x, int from, int to) => Math.Sqrt(x.Skip(from).Take(to - from).Select(v => (double)v * v).Average());
        double low = Rms(filtered.Left, 4000, Bar / 2 - 4000), high = Rms(filtered.Left, Bar / 2 + 4000, Bar - 4000);
        Assert.True(high > low * 1.5, $"low {low} high {high} length {filtered.Left.Length}");
    }
}
