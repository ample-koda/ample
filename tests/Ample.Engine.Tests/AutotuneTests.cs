using Ample.Audio.Engine;
using Ample.Core.Abstractions;
using Ample.Language;

namespace Ample.Core.Tests;

public sealed class AutotuneTests : IDisposable
{
    private const int Rate = 16000;
    private const int Bar = Rate * 2;
    private readonly string folder = Directory.CreateTempSubdirectory("ample-autotune-").FullName;

    public void Dispose() => Directory.Delete(folder, true);

    // a voice-like tone (a few harmonics) of three seconds at a frequency
    private static short[] Tone(double hz, double seconds = 3) => [.. Enumerable.Range(0, (int)(seconds * Rate)).Select(i =>
    {
        double t = i / (double)Rate;
        double v = Math.Sin(2 * Math.PI * hz * t) + (0.5 * Math.Sin(4 * Math.PI * hz * t)) + (0.25 * Math.Sin(6 * Math.PI * hz * t));
        return (short)(v * 6000);
    })];

    private async Task<short[]> RenderAsync(short[] source, Dictionary<string, string> parameters, string? notes = null)
    {
        BlockNode block = new() { Type = "autotune", Name = "tune" };
        foreach ((string name, string value) in parameters)
        {
            block.Parameters.Add(new Parameter(name, value, 0));
        }

        Dictionary<string, byte[]> inputs = new() { ["in"] = Wav.Encode(source, Rate) };
        if (notes is not null)
        {
            inputs["target"] = System.Text.Encoding.UTF8.GetBytes(notes);
        }

        RenderRequest request = new(block, EngineRig.Spec.Templates["autotune"], Rate, Bar, 1, 2, new HashSet<int> { 1, 2 }, inputs, 1, folder);
        IReadOnlyList<RenderOutput> outputs = await ExtraEffects.All.Single(r => r.BlockType == "autotune").RenderAsync(request, CancellationToken.None);
        return Wav.Decode(outputs.Single(o => o.Role == "out").Data).Samples;
    }

    // the median pitch of the middle of a sound, as a MIDI number
    private static double Pitch(short[] sound)
    {
        double[] voiced = [.. Autotune.Detect(sound, Rate).Midi.Skip(50).SkipLast(50).Where(m => !double.IsNaN(m)).Order()];
        Assert.NotEmpty(voiced);
        return voiced[voiced.Length / 2];
    }

    [Fact]
    public void The_pitch_of_a_tone_is_followed()
    {
        Assert.Equal(69, Pitch(Tone(440)), 1);
        Assert.Equal(57, Pitch(Tone(220)), 1);
    }

    [Fact]
    public async Task A_voice_a_little_too_high_is_pulled_to_the_note()
    {
        short[] sharp = Tone(452);   // about 46 cents above A4
        Assert.InRange(Pitch(sharp), 69.35, 69.6);
        short[] tuned = await RenderAsync(sharp, new() { ["key"] = "chromatic", ["speed"] = "0 ms", ["humanize"] = "0%" });
        Assert.Equal(sharp.Length, tuned.Length);
        Assert.InRange(Pitch(tuned), 68.9, 69.1);
    }

    [Fact]
    public async Task Half_the_amount_moves_it_half_way_and_the_key_decides_the_note()
    {
        short[] sharp = Tone(452);
        Assert.InRange(Pitch(await RenderAsync(sharp, new() { ["key"] = "chromatic", ["speed"] = "0 ms", ["humanize"] = "0%", ["amount"] = "50%" })), 69.15, 69.35);
        // A# is not in A minor: a tone at A#4 goes to the closest note of the key (A4 or B4)
        double moved = Pitch(await RenderAsync(Tone(466.16), new() { ["key"] = "A minor", ["speed"] = "0 ms", ["humanize"] = "0%" }));
        Assert.True(Math.Abs(moved - 69) < 0.15 || Math.Abs(moved - 71) < 0.15, $"pitch {moved}");
    }

    [Fact]
    public async Task The_melody_on_target_decides_the_note_in_the_closest_octave()
    {
        // a C5 (72) for both bars; the A4 that is sung goes up to C5
        string notes = """{"notes":[{"bar":1,"beat":0,"beats":8,"pitch":72}]}""";
        Assert.InRange(Pitch(await RenderAsync(Tone(440), new() { ["speed"] = "0 ms", ["humanize"] = "0%" }, notes)), 71.85, 72.15);
        // a C3 in the score is sung in the octave of the voice: C5 again (the closest C to A4)
        string low = """{"notes":[{"bar":1,"beat":0,"beats":8,"pitch":48}]}""";
        Assert.InRange(Pitch(await RenderAsync(Tone(440), new() { ["speed"] = "0 ms", ["humanize"] = "0%" }, low)), 71.85, 72.15);
    }

    [Fact]
    public async Task A_fix_wins_and_off_leaves_it_alone()
    {
        Assert.InRange(Pitch(await RenderAsync(Tone(440), new() { ["key"] = "chromatic", ["speed"] = "0 ms", ["humanize"] = "0%", ["fixes"] = "0s-3s B4" })), 70.85, 71.15);
        short[] sharp = Tone(452);
        Assert.InRange(Pitch(await RenderAsync(sharp, new() { ["key"] = "chromatic", ["speed"] = "0 ms", ["fixes"] = "0s-3s off" })), 69.35, 69.6);
    }

    [Fact]
    public void Fixes_and_keys_are_read_and_wrong_ones_are_explained()
    {
        IReadOnlyList<AutotuneFix> fixes = AutotuneFixes.Parse("7.9s-8.4s G4 vibrato 0%; 4.2s-4.7s E4; 1-2 off", Rate, Bar);
        Assert.Equal(3, fixes.Count);
        Assert.Equal(new AutotuneFix(Bar, 2 * Bar, null, null), fixes[0]);
        Assert.Equal(64, fixes[1].Note);
        Assert.Equal(0, fixes[2].Vibrato);
        Assert.Equal(66, AutotuneFixes.Note("F#4"));
        Assert.Equal(46, AutotuneFixes.Note("Bb2"));
        Assert.Equal("C#5", AutotuneFixes.Name(73));
        Assert.Equal("AMP5397", Assert.Throws<AmpleException>(() => AutotuneFixes.Parse("at 4 seconds sing e", Rate, Bar)).DiagnosticCode);
        Assert.Equal("AMP5397", Assert.Throws<AmpleException>(() => AutotuneFixes.Parse("4s-3s E4", Rate, Bar)).DiagnosticCode);
        Autotune.Track none = new(160, []);
        Assert.Equal("AMP5397", Assert.Throws<AmpleException>(() => Autotune.Scale("H lydian", none)).DiagnosticCode);
        (bool[] scale, _) = Autotune.Scale("A minor", none);
        Assert.Equal([true, false, true, false, true, true, false, true, false, true, false, true], scale);
        Assert.Equal("chromatic", Autotune.Scale("auto", none).Name);
    }

    [Fact]
    public void The_key_is_found_from_what_is_sung()
    {
        // a G major scale (G A B C D E F#), each note half a second
        int[] notes = [67, 69, 71, 72, 74, 76, 78, 79, 67, 71, 74, 79];
        short[] song = [.. notes.SelectMany(n => Tone(440 * Math.Pow(2, (n - 69) / 12.0), 0.5))];
        Assert.Equal("G major", Autotune.Scale("auto", Autotune.Detect(song, Rate)).Name);
    }

    [Fact]
    public async Task The_same_input_gives_the_same_bytes()
    {
        short[] sharp = Tone(452);
        Assert.Equal(await RenderAsync(sharp, new() { ["key"] = "chromatic" }), await RenderAsync(sharp, new() { ["key"] = "chromatic" }));
    }
}
