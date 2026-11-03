using Ample.Audio.Engine;
using Ample.Language;

namespace Ample.Core.Tests;

public sealed class DelayLlamaTests : IDisposable
{
    private const int Rate = 22050;
    private const int Bar = Rate * 2;   // 120 BPM
    private readonly string folder = Directory.CreateTempSubdirectory("ample-llama-").FullName;

    public void Dispose() => Directory.Delete(folder, true);

    private static short[] Pcm(double[] x) => [.. x.Select(v => (short)Math.Clamp(Math.Round(v * 32767), short.MinValue, short.MaxValue))];

    // the median pitch of a stretch, as a MIDI number (a formant voice has a weak fundamental: the tracker may find it an octave off, so the tests compare the note name)
    private static double Pitch(short[] sound, int from, int to)
    {
        short[] part = sound[from..to];
        double[] voiced = [.. Autotune.Detect(part, Rate).Midi.Skip(10).SkipLast(10).Where(m => !double.IsNaN(m)).Order()];
        Assert.NotEmpty(voiced);
        double found = voiced[voiced.Length / 2];
        return found;
    }

    // the distance to a note in semitones, ignoring the octave
    private static double Off(double pitch, double note) => Math.Abs(((pitch - note) % 12 + 18) % 12 - 6);

    private async Task<IReadOnlyList<RenderOutput>> BlockAsync(string notes, Dictionary<string, string> parameters)
    {
        BlockNode block = new() { Type = "delay-llama", Name = "llama" };
        foreach ((string name, string value) in parameters)
        {
            block.Parameters.Add(new Parameter(name, value, 0));
        }

        Dictionary<string, byte[]> inputs = new() { ["notes"] = System.Text.Encoding.UTF8.GetBytes(notes) };
        RenderRequest request = new(block, EngineRig.Spec.Templates["delay-llama"], Rate, Bar, 1, 2, new HashSet<int> { 1, 2 }, inputs, 7, folder);
        return await BuiltInRenderers.All.Single(r => r.BlockType == "delay-llama").RenderAsync(request, CancellationToken.None);
    }

    [Fact]
    public void A_note_is_sung_at_its_pitch_at_a_healthy_level()
    {
        (double[] left, _) = DelayLlama.Render([new DelayLlama.Note(0, Rate * 2, 57, 100)], new DelayLlama.Settings(Vibrato: 0), Rate, Rate * 2, 1);
        short[] pcm = Pcm(left);
        Assert.InRange(Off(Pitch(pcm, Rate / 2, Rate * 3 / 2), 57), 0, 0.15);
        double peak = left.Max(Math.Abs);
        Assert.InRange(peak, 0.05, 0.95);
    }

    [Fact]
    public void Notes_that_touch_glide_and_the_vowel_moves_the_formants()
    {
        // A3 then E4 right after it: with glide the second note is reached without a new attack, and it ends on E4
        (double[] left, _) = DelayLlama.Render([new DelayLlama.Note(0, Rate, 57, 100), new DelayLlama.Note(Rate, Rate, 64, 100)], new DelayLlama.Settings(Vibrato: 0, GlideMs: 150), Rate, Rate * 2, 1);
        Assert.InRange(Off(Pitch(Pcm(left), Rate * 3 / 2, Rate * 2), 64), 0, 0.2);
        // a is open (high F1), i is closed with a high F2, u is low in both
        Assert.True(DelayLlama.Formant(0, 0) > DelayLlama.Formant(0, 0.5), "F1 of a is above F1 of i");
        Assert.True(DelayLlama.Formant(1, 0.5) > 2000 && DelayLlama.Formant(1, 1) < 700, "F2 of i is high, F2 of u is low");
        Assert.Equal(2140, DelayLlama.Formant(1, 0.5), 3);
    }

    [Fact]
    public async Task The_block_sings_its_notes_in_stereo_with_unison_and_the_same_bytes_every_time()
    {
        string notes = """{"notes":[{"bar":1,"beat":0,"beats":4,"pitch":60,"velocity":100},{"bar":2,"beat":0,"beats":4,"pitch":62,"velocity":100}]}""";
        Dictionary<string, string> parameters = new() { ["unison"] = "3", ["vowel"] = "auto(1:0, 2:100)", ["vibrato"] = "0%" };
        IReadOnlyList<RenderOutput> first = await BlockAsync(notes, parameters);
        Assert.True(ChannelAudio.IsStereo(first[0].Data));
        Assert.Equal(first[0].Data, (await BlockAsync(notes, parameters))[0].Data);
        short[] left = ChannelAudio.Decode(first[0].Data).Buffer.Left;
        Assert.Equal(2 * Bar, left.Length);
        Assert.InRange(Off(Pitch(left, Bar + (Bar / 4), Bar * 2 - (Bar / 4)), 62), 0, 0.3);
    }

    [Fact]
    public async Task The_bend_of_a_note_is_sung()
    {
        string notes = """{"notes":[{"bar":1,"beat":0,"beats":8,"pitch":57,"velocity":100,"bend":1200}]}""";
        short[] left = ChannelAudio.Decode((await BlockAsync(notes, new() { ["vibrato"] = "0%" }))[0].Data).Buffer.Left;
        // at the end of the note (8 beats = two bars) it is an octave higher: near the end close to A4
        Assert.InRange(Off(Pitch(left, (2 * Bar) - (Bar / 5), (2 * Bar) - (Bar / 20)), 69), 0, 0.8);
        // and half way it is half way: a tritone up (D#)
        Assert.InRange(Off(Pitch(left, Bar - (Bar / 10), Bar + (Bar / 10)), 63), 0, 0.8);
    }
}
