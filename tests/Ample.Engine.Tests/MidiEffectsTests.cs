using System.Text;
using System.Text.Json.Nodes;
using Ample.Audio.Engine;
using Ample.Language;

namespace Ample.Core.Tests;

[Trait("Category", "MidiEffects")]
public sealed class MidiEffectsTests : IDisposable
{
    private static readonly int[] TransposedPitches = [127, 14];
    private static readonly int[] InvertedPitches = [72, 64, 67];
    private static readonly int[] ArpeggioPitches = [60, 64, 67, 64];
    private readonly string folder = Directory.CreateTempSubdirectory("ample-midi-fx-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
    }

    private async Task<(JsonObject Result, byte[] Bytes)> RunAsync(string type, JsonObject input, Dictionary<string, string> parameters, ulong seed)
    {
        BlockNode block = new() { Type = type, Name = "fx" };
        foreach ((string name, string value) in parameters) block.Parameters.Add(new Parameter(name, value, 0));
        byte[] bytes = Encoding.UTF8.GetBytes(input.ToJsonString());
        RenderRequest request = new(block, EngineRig.Spec.Templates[type], 44100, 44100, 1, 1, new HashSet<int> { 1 }, new Dictionary<string, byte[]> { ["notes"] = bytes }, seed, folder);
        IReadOnlyList<RenderOutput> outputs = await MidiEffects.All.Single(renderer => renderer.BlockType == type).RenderAsync(request, CancellationToken.None);
        byte[] output = outputs.Single(item => item.Role == "notes").Data;
        return (JsonNode.Parse(output)!.AsObject(), output);
    }

    private static JsonObject Note(int bar, decimal beat, decimal beats, int pitch, int velocity = 96) => new() { ["bar"] = bar, ["beat"] = beat, ["beats"] = beats, ["pitch"] = pitch, ["velocity"] = velocity };

    [Fact]
    public async Task Transpose_clamps_pitch_and_bypass_returns_the_original_bytes()
    {
        JsonObject input = new() { ["notes"] = new JsonArray(Note(1, 0, 1, 126), Note(1, 1, 1, 2)) };
        JsonObject moved = (await RunAsync("transpose", input, new() { ["semitones"] = "12" }, 1)).Result;
        Assert.Equal(TransposedPitches, moved["notes"]!.AsArray().Select(note => (int)note!["pitch"]!));
        (JsonObject _, byte[] original) = await RunAsync("transpose", input, new() { ["enabled"] = "off", ["semitones"] = "12" }, 1);
        Assert.Equal(Encoding.UTF8.GetBytes(input.ToJsonString()), original);
    }

    [Fact]
    public async Task Chord_applies_inversion_and_preserves_note_timing_and_velocity()
    {
        JsonObject input = new() { ["notes"] = new JsonArray(Note(1, 0.5m, 1, 60, 88)) };
        JsonObject output = (await RunAsync("chord", input, new() { ["type"] = "major", ["inversion"] = "1" }, 1)).Result;
        Assert.Equal(InvertedPitches, output["notes"]!.AsArray().Select(note => (int)note!["pitch"]!));
        Assert.All(output["notes"]!.AsArray(), note => { Assert.Equal(0.5m, (decimal)note!["beat"]!); Assert.Equal(88, (int)note["velocity"]!); });
    }

    [Fact]
    public async Task Quantize_uses_decimal_grid_rounding_for_starts_and_lengths()
    {
        JsonObject input = new() { ["notes"] = new JsonArray(Note(1, 0.13m, 0.7m, 60)) };
        JsonObject output = (await RunAsync("quantize", input, new() { ["grid"] = "1/16", ["strength"] = "100", ["lengths"] = "on" }, 1)).Result;
        Assert.Equal(0.25m, (decimal)output["notes"]![0]!["beat"]!);
        Assert.Equal(0.75m, (decimal)output["notes"]![0]!["beats"]!);
    }

    [Fact]
    public async Task Humanize_is_repeatable_for_the_same_effective_seed()
    {
        JsonObject input = new() { ["notes"] = new JsonArray(Note(1, 0.5m, 0.5m, 60), Note(1, 1.5m, 0.5m, 64), Note(2, 0.5m, 0.5m, 67)) };
        Dictionary<string, string> parameters = new() { ["velocity"] = "12", ["timing"] = "35" };
        (_, byte[] first) = await RunAsync("midi-humanize", input, parameters, 42);
        (_, byte[] again) = await RunAsync("midi-humanize", input, parameters, 42);
        (_, byte[] differentSeed) = await RunAsync("midi-humanize", input, parameters, 43);
        Assert.Equal(first, again);
        Assert.NotEqual(first, differentSeed);
    }

    [Fact]
    public async Task Arpeggiator_schedules_an_updown_run_and_random_mode_is_seeded()
    {
        JsonObject input = new() { ["notes"] = new JsonArray(Note(1, 0, 1, 60), Note(1, 0, 1, 64), Note(1, 0, 1, 67)) };
        JsonObject updown = (await RunAsync("arpeggiator", input, new() { ["rate"] = "1/16", ["pattern"] = "updown", ["octaves"] = "1", ["gate"] = "80" }, 1)).Result;
        Assert.Equal(ArpeggioPitches, updown["notes"]!.AsArray().Select(note => (int)note!["pitch"]!));
        Dictionary<string, string> random = new() { ["rate"] = "1/16", ["pattern"] = "random", ["octaves"] = "2", ["gate"] = "80" };
        (_, byte[] first) = await RunAsync("arpeggiator", input, random, 42);
        (_, byte[] again) = await RunAsync("arpeggiator", input, random, 42);
        Assert.Equal(first, again);
    }
}
