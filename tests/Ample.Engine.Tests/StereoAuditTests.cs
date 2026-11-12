using Ample.Audio.Engine;
using Ample.Audio.IO;
using Ample.Engine.Engine;
using Ample.Language;
using Ample.Storage.Projects;
using Ample.Storage.Takes;

namespace Ample.Core.Tests;

/// <summary>A01: every audio effect block keeps a stereo input stereo, with the two channels still different.</summary>
[Trait("Category", "Effects")]
public sealed class StereoAuditTests
{
    private const int Rate = 16000;

    private static byte[] StereoInput()
    {
        short[] left = new short[Rate], right = new short[Rate];
        uint state = 99;
        for (int i = 0; i < Rate; i++)
        {
            state = (state * 1664525) + 1013904223;
            left[i] = (short)(((int)(state >> 16) % 8001) - 4000 + (int)(3000 * Math.Sin(i / 9.0)));
            state = (state * 1664525) + 1013904223;
            right[i] = (short)(((int)(state >> 16) % 8001) - 4000 + (int)(3000 * Math.Sin(i / 23.0)));
        }

        return StereoWav.Encode(new StereoBuffer(left, right), Rate);
    }

    private static async Task<string> Check(IBlockRenderer renderer)
    {
        BlockNode block = new() { Type = renderer.BlockType, Name = "fx" };
        if (!EngineRig.Spec.Templates.TryGetValue(renderer.BlockType, out TemplateSpec? template)) return "no template";
        byte[] input = StereoInput();
        Dictionary<string, byte[]> inputs = new() { ["in"] = input };
        string dir = Directory.CreateTempSubdirectory("ample-stereo-").FullName;
        try
        {
            RenderRequest request = new(block, template, Rate, Rate, 1, 1, new HashSet<int> { 1 }, inputs, 1, dir);
            IReadOnlyList<RenderOutput> outputs = await renderer.RenderAsync(request, CancellationToken.None);
            RenderOutput? audio = outputs.FirstOrDefault(o => o.Role is "out" or "audio" or "mix");
            if (audio is null) return "no audio output";
            if (!ChannelAudio.IsStereo(audio.Data)) return "MONO";
            (StereoBuffer both, _) = ChannelAudio.Decode(audio.Data);
            return both.Left.SequenceEqual(both.Right) ? "SAME-CHANNELS" : "ok";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return "error: " + exception.Message.Split('\n')[0];
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Every_audio_effect_keeps_a_stereo_input_stereo()
    {
        List<string> bad = [];
        // join merges two inputs and the vocoder needs a voice and a carrier, ambience and convolution need a file: they are covered by their own tests
        foreach (IBlockRenderer renderer in AudioEffects.All.Concat(ExtraEffects.All).Where(r => r.BlockType is not ("join" or "ambience" or "convolution" or "vocoder")))
        {
            string result = await Check(renderer);
            if (result != "ok") bad.Add($"{renderer.BlockType}: {result}");
        }

        Assert.True(bad.Count == 0, "Not stereo-clean: " + string.Join("; ", bad));
    }

    [Fact]
    public void Channel_mode_sums_to_mono_copies_to_stereo_and_leaves_auto_alone()
    {
        byte[] stereo = StereoInput();
        byte[] mono = Wav.Encode([.. Enumerable.Range(0, 100).Select(i => (short)(i * 10))], Rate);
        RenderOutput[] outputs = [new("audio", stereo), new("left", mono), new("warnings", [1, 2, 3])];

        IReadOnlyList<RenderOutput> auto = ChannelMode.Apply(outputs, "auto");
        Assert.Same(outputs, auto);
        IReadOnlyList<RenderOutput> summed = ChannelMode.Apply(outputs, "mono");
        Assert.False(ChannelAudio.IsStereo(summed[0].Data));
        (StereoBuffer both, _) = StereoWav.Decode(stereo);
        Assert.Equal((short)((both.Left[7] + both.Right[7]) / 2), Wav.Decode(summed[0].Data).Samples[7]);
        Assert.Equal([1, 2, 3], summed[2].Data);
        IReadOnlyList<RenderOutput> doubled = ChannelMode.Apply([new("audio", mono)], "stereo");
        (StereoBuffer copy, _) = StereoWav.Decode(doubled[0].Data);
        Assert.Equal(copy.Left, copy.Right);
        Assert.Equal(mono, ChannelMode.Apply([new("left", mono)], "stereo")[0].Data);
    }

    [Fact]
    public async Task The_master_default_makes_blocks_stereo_and_a_block_can_say_mono_itself()
    {
        string root = Path.Combine(Directory.CreateTempSubdirectory("ample-channels-").FullName, "p");
        PhysicalFileSystem fs = new();
        using OpenProject p = await ProjectStore.CreateAsync(fs, new SystemClock(), new GuidV7Generator(), root, "channels");
        Takebank bank = Takebank.Open(fs, new SystemClock(), root);
        BuildEngine engine = new(bank, EngineRig.Spec, p.Manifest.Id, root);
        string song = """
composition "s" { ample 1  tempo 120  master-seed 1 }
midi-clip c {
  chords Am
  bars 1
}
piano wide pinned@2.0 at 1 for 1 {
  set felt-piano
  from c.notes -> notes
}
piano narrow pinned@2.0 at 1 for 1 {
  set felt-piano
  channels mono
  from c.notes -> notes
}
master master {
  channels stereo
}

""";
        BuildReport report = await engine.BuildAsync(Parser.Parse(song, EngineRig.Spec));
        async Task<bool> IsStereo(string block)
        {
            BlockResult r = report.Blocks.Single(b => b.Block == block);
            Assert.True(r.Status is "rendered" or "cached", r.Message);
            await using Stream data = bank.OpenData(bank.GetTake(r.TakeId!).Data[0].DataId);
            using MemoryStream ms = new();
            await data.CopyToAsync(ms);
            return ChannelAudio.IsStereo(ms.ToArray());
        }

        Assert.True(await IsStereo("wide"));
        Assert.False(await IsStereo("narrow"));
    }

    [Fact]
    public async Task A_kit_with_a_stereo_sample_renders_stereo_and_a_mono_kit_stays_mono()
    {
        string root = Directory.CreateTempSubdirectory("ample-stereo-kit-").FullName;
        try
        {
            string stereoKit = Path.Combine(root, "samples", "wide"), monoKit = Path.Combine(root, "samples", "narrow");
            Directory.CreateDirectory(stereoKit);
            Directory.CreateDirectory(monoKit);
            short[] left = [.. Enumerable.Range(0, 4000).Select(i => (short)(8000 * Math.Sin(i / 6.0)))];
            short[] right = [.. Enumerable.Range(0, 4000).Select(i => (short)(8000 * Math.Sin(i / 11.0)))];
            File.WriteAllBytes(Path.Combine(stereoKit, "kick.wav"), StereoWav.Encode(new StereoBuffer(left, right), Rate));
            File.WriteAllBytes(Path.Combine(monoKit, "kick.wav"), Wav.Encode(left, Rate));
            foreach (string voice in new[] { "snare", "hat", "open", "clap", "tom", "ride", "crash" })
            {
                File.Copy(Path.Combine(stereoKit, "kick.wav"), Path.Combine(stereoKit, voice + ".wav"));
                File.Copy(Path.Combine(monoKit, "kick.wav"), Path.Combine(monoKit, voice + ".wav"));
            }

            async Task<byte[]> Drums(string kit)
            {
                BlockNode block = new() { Type = "drums", Name = "beat" };
                block.Parameters.Add(new Parameter("kit", kit, 0));
                block.Parameters.Add(new Parameter("pattern", "beat-A", 0));
                RenderRequest request = new(block, EngineRig.Spec.Templates["drums"], Rate, Rate, 1, 1, new HashSet<int> { 1 }, new Dictionary<string, byte[]>(), 1, root);
                IReadOnlyList<RenderOutput> outputs = await BuiltInRenderers.All.Single(r => r.BlockType == "drums").RenderAsync(request, CancellationToken.None);
                return outputs.Single(o => o.Role == "audio").Data;
            }

            byte[] wide = await Drums("samples/wide");
            Assert.True(ChannelAudio.IsStereo(wide));
            (StereoBuffer both, _) = StereoWav.Decode(wide);
            Assert.False(both.Left.SequenceEqual(both.Right));
            Assert.False(ChannelAudio.IsStereo(await Drums("samples/narrow")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task The_sampler_plays_a_stereo_file_in_stereo()
    {
        string root = Directory.CreateTempSubdirectory("ample-stereo-sampler-").FullName;
        try
        {
            short[] left = [.. Enumerable.Range(0, 4000).Select(i => (short)(8000 * Math.Sin(i / 6.0)))];
            short[] right = [.. Enumerable.Range(0, 4000).Select(i => (short)(8000 * Math.Sin(i / 11.0)))];
            File.WriteAllBytes(Path.Combine(root, "pad.wav"), StereoWav.Encode(new StereoBuffer(left, right), Rate));
            BlockNode block = new() { Type = "sampler", Name = "pad" };
            block.Parameters.Add(new Parameter("file", "pad.wav", 0));
            Dictionary<string, byte[]> inputs = new() { ["notes"] = System.Text.Encoding.UTF8.GetBytes("{\"notes\":[{\"bar\":1,\"pitch\":60,\"beat\":0,\"beats\":1,\"velocity\":100}]}") };
            RenderRequest request = new(block, EngineRig.Spec.Templates["sampler"], Rate, Rate, 1, 1, new HashSet<int> { 1 }, inputs, 1, root);
            IReadOnlyList<RenderOutput> outputs = await BuiltInRenderers.All.Single(r => r.BlockType == "sampler").RenderAsync(request, CancellationToken.None);
            byte[] audio = outputs.Single(o => o.Role == "audio").Data;
            Assert.True(ChannelAudio.IsStereo(audio));
            (StereoBuffer both, _) = StereoWav.Decode(audio);
            Assert.False(both.Left.SequenceEqual(both.Right));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Humanize_moves_pitched_sample_notes_deterministically_and_zero_changes_nothing()
    {
        string root = Directory.CreateTempSubdirectory("ample-human-").FullName;
        try
        {
            string set = Path.Combine(root, "samples", "keys");
            Directory.CreateDirectory(set);
            short[] tone = [.. Enumerable.Range(0, 4000).Select(i => (short)(8000 * Math.Sin(i / 6.0)))];
            File.WriteAllBytes(Path.Combine(set, "c4.wav"), Wav.Encode(tone, Rate));
            async Task<short[]> Piano(string? humanize)
            {
                BlockNode block = new() { Type = "piano", Name = "keys" };
                block.Parameters.Add(new Parameter("set", "samples/keys", 0));
                if (humanize is not null) block.Parameters.Add(new Parameter("humanize", humanize, 0));
                string notes = "{\"notes\":[{\"bar\":1,\"pitch\":60,\"beat\":0,\"beats\":1,\"velocity\":100},{\"bar\":1,\"pitch\":60,\"beat\":2,\"beats\":1,\"velocity\":100}]}";
                Dictionary<string, byte[]> inputs = new() { ["notes"] = System.Text.Encoding.UTF8.GetBytes(notes) };
                RenderRequest request = new(block, EngineRig.Spec.Templates["piano"], Rate, Rate, 1, 1, new HashSet<int> { 1 }, inputs, 7, root);
                IReadOnlyList<RenderOutput> outputs = await BuiltInRenderers.All.Single(r => r.BlockType == "piano").RenderAsync(request, CancellationToken.None);
                return Wav.Decode(outputs.Single(o => o.Role == "audio").Data).Samples;
            }

            short[] plain = await Piano(null);
            Assert.Equal(plain, await Piano("0"));
            short[] loose = await Piano("100");
            Assert.False(plain.SequenceEqual(loose));
            Assert.Equal(loose, await Piano("100"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
