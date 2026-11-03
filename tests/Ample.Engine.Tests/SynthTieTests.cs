using System.Text;
using Ample.Audio.Engine;
using Ample.Language;

namespace Ample.Core.Tests;

/// <summary>The built-in synth does not strike a tied piece of a note again: a note that was cut at a bar line (a long note, or one made longer by stretch) sounds as one.</summary>
public sealed class SynthTieTests
{
    private const int Rate = 44100;
    private const int SamplesPerBar = Rate * 2;   // 120 bpm, four beats

    private static async Task<short[]> Render(string notes)
    {
        BlockNode block = new() { Type = "synth", Name = "s", Class = "stable", Version = "1.0" };
        block.Parameters.Add(new Parameter("wave", "sine", 1));
        block.Parameters.Add(new Parameter("attack", "400 ms", 1));
        block.Parameters.Add(new Parameter("release", "1 ms", 1));   // the first note stops at once, so that only a new attack can be heard at the bar line
        Dictionary<string, byte[]> inputs = new() { ["notes"] = Encoding.UTF8.GetBytes(notes) };
        RenderRequest request = new(block, EngineRig.Spec.Templates["synth"], Rate, SamplesPerBar, 1, 2, new HashSet<int> { 1, 2 }, inputs, 0, Path.GetTempPath(), null, null);
        IReadOnlyList<RenderOutput> outputs = await BuiltInRenderers.All.First(r => r.BlockType == "synth").RenderAsync(request, CancellationToken.None);
        return Wav.Decode(outputs.First(o => o.Role == "audio").Data).Samples;
    }

    private static int Loudest(short[] samples, int from, int count) => samples.Skip(from).Take(count).Max(s => Math.Abs((int)s));

    [Fact]
    public async Task A_tied_piece_goes_on_where_a_new_note_starts_again()
    {
        const string first = "{\"bar\":1,\"pitch\":60,\"beat\":0,\"beats\":4,\"velocity\":100}";
        short[] struck = await Render("{\"notes\":[" + first + ",{\"bar\":2,\"pitch\":60,\"beat\":0,\"beats\":4,\"velocity\":100}]}");
        short[] tied = await Render("{\"notes\":[" + first + ",{\"bar\":2,\"pitch\":60,\"beat\":0,\"beats\":4,\"velocity\":100,\"tie\":true}]}");
        // 5 to 15 ms into the second bar (the 1 ms release of the first note is over): a new note is still at the start of its 400 ms attack, a tied piece is at its full level
        int struckLevel = Loudest(struck, SamplesPerBar + 200, Rate / 100), tiedLevel = Loudest(tied, SamplesPerBar + 200, Rate / 100);
        Assert.True(struckLevel < tiedLevel / 4, $"struck {struckLevel}, tied {tiedLevel}, loudest of all {Loudest(tied, 0, tied.Length)}");
    }
}
