using System.Text;
using Ample.Audio.Engine;
using Ample.Core.Abstractions;
using Ample.Engine.Engine;
using Ample.Language;
using Ample.Speech;
using Ample.Storage.Takes;

namespace Ample.Core.Tests;

[Trait("Category", "Engine")]
public sealed class SpeechRendererTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-speech-").FullName;

    [Fact]
    public async Task Speech_rejects_input_and_notes_over_host_limits()
    {
        string notes = "{\"notes\":[{\"bar\":1,\"beat\":0,\"beats\":1,\"pitch\":60,\"velocity\":127},"
            + "{\"bar\":1,\"beat\":1,\"beats\":1,\"pitch\":62,\"velocity\":127}]}";
        Dictionary<string, string> textLimit = new() { ["max_input_characters"] = "4" };
        Dictionary<string, string> noteLimit = new() { ["max_note_events"] = "1" };

        await Assert.ThrowsAsync<SpeechResourceLimitException>(() =>
            RenderWordsAsync("hello", notes, "natural", 10_000, settings: textLimit));
        await Assert.ThrowsAsync<SpeechResourceLimitException>(() =>
            RenderWordsAsync("hello", notes, "natural", 10_000, settings: noteLimit));
    }

    [Fact]
    public async Task Host_renderer_settings_limit_speech_during_a_normal_build()
    {
        const string song = "composition \"speech\" { ample 1 tempo 120 master-seed 7 }\n"
            + "speech voice at 1 for 1 {\n  text \"hello\"\n}\n";
        using EngineRig rig = await EngineRig.CreateAsync();
        BuildOptions options = new()
        {
            RendererSettings = new Dictionary<string, string> { ["speech.max_output_samples"] = "100" }
        };

        BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse(song), options);
        BlockResult block = report.Blocks.Single(item => item.Block == "voice");
        Assert.Equal("failed", block.Status);
        Assert.Null(block.TakeId);
        Assert.Contains("configured limit", block.Message);
    }

    [Fact]
    public async Task A_song_setting_cannot_loosen_a_host_speech_limit()
    {
        // the song asks for a huge limit on one line (the key of a setting has no underscore, so it is also reported); the host's 100 samples stay
        const string song = "composition \"speech\" { ample 1 tempo 120 master-seed 7 }\n"
            + "settings { speech.maxOutputSamples 99999999 }\n"
            + "speech voice at 1 for 1 {\n  text \"hello\"\n}\n";
        using EngineRig rig = await EngineRig.CreateAsync();
        BuildOptions options = new() { RendererSettings = new Dictionary<string, string> { ["speech.max_output_samples"] = "100" } };

        BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse(song), options);
        Assert.Equal("failed", report.Blocks.Single(item => item.Block == "voice").Status);
    }

    [Fact]
    public async Task Humanico_words_mode_renders_calibrated_voice_with_new_identity()
    {
        string notes = "{\"notes\":[{\"bar\":1,\"beat\":0,\"beats\":1,\"pitch\":60,\"velocity\":127}]}";
        IReadOnlyList<RenderOutput> outputs = await RenderWordsAsync("hello", notes, "natural", 10_000, "humanico");

        Assert.Contains(Wav.Decode(outputs[0].Data).Samples, sample => sample != 0);
        Assert.Contains("humanico/klatt-candidate-3", outputs[0].MetadataJson);
        Assert.Contains(HumanicoCalibratedSegments.CalibrationSha256, outputs[0].MetadataJson);
    }

    public void Dispose() => Directory.Delete(folder, true);

    private static RenderRequest Request(int firstBar, int barCount, IReadOnlySet<int> activeBars, IReadOnlyDictionary<string, string>? settings = null)
    {
        BlockNode block = new() { Type = "speech", Name = "voice" };
        block.Parameters.Add(new Parameter("text", "\"one two\"", 0));
        return new(block, EngineRig.Spec.Templates["speech"], 1_000, 1_000, firstBar, barCount, activeBars, new Dictionary<string, byte[]>(), 17, Path.GetTempPath(), Settings: settings);
    }

    private async Task<IReadOnlyList<RenderOutput>> RenderWordsAsync(string text, string notes, string policy, int rate, string? engine = null,
        IReadOnlyDictionary<string, string>? settings = null, int? samplesPerBar = null, params (string Name, string Value)[] extra)
    {
        BlockNode block = new() { Type = "speech", Name = "voice" };
        foreach ((string name, string value) in extra) block.Parameters.Add(new Parameter(name, value, 0));
        block.Parameters.Add(new Parameter("mode", "words", 0));
        block.Parameters.Add(new Parameter("text", text, 0));
        block.Parameters.Add(new Parameter("duration_policy", policy, 0));
        if (engine is not null) block.Parameters.Add(new Parameter("engine", engine, 0));
        Dictionary<string, byte[]> inputs = new() { ["notes"] = Encoding.UTF8.GetBytes(notes) };
        RenderRequest request = new(block, EngineRig.Spec.Templates["speech"], rate, samplesPerBar ?? rate * 4, 1, 2,
            new HashSet<int> { 1, 2 }, inputs, 17, folder, Settings: settings);
        return await BuiltInRenderers.All.Single(renderer => renderer.BlockType == "speech").RenderAsync(request, CancellationToken.None);
    }

    [Theory]
    [InlineData("humanico")]
    [InlineData("retro")]
    public async Task The_memory_estimate_is_above_what_a_words_render_allocates(string engine)
    {
        // 24 words on 24 notes over 2 bars of 2 seconds at 44.1 kHz: the mix buffer, the words and the output are what is allocated
        string notes = NoteJson(Enumerable.Range(0, 24).Select(i => (i * 0.3, 0.25, 48 + i % 12)));
        string text = string.Join(' ', Enumerable.Repeat("robot", 24));
        _ = await RenderWordsAsync("hello", NoteJson([(0, 1, 60)]), "natural", 44_100, engine, samplesPerBar: 88_200);   // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        short[] samples = Wav.Decode((await RenderWordsAsync(text, notes, "natural", 44_100, engine, samplesPerBar: 88_200))[0].Data).Samples;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long estimate = Ample.Speech.SpeechMemory.EstimateBlockBytes(samples.Length, text, null, 1, engine, wordsMode: true, 44_100);

        Assert.True(allocated <= estimate, $"{engine} words allocated {allocated} bytes, the estimate says {estimate}");
    }

    [Fact]
    public async Task The_memory_estimate_is_above_what_a_flite_words_render_allocates()
    {
        // flite speaks "robot" longer than the other engines: twelve words on notes 0.6 beat apart keep the voices below the polyphony limit
        string notes = NoteJson(Enumerable.Range(0, 12).Select(i => (i * 0.6, 0.5, 48 + i % 12)));
        string text = string.Join(' ', Enumerable.Repeat("robot", 12));
        _ = await RenderWordsAsync("hello", NoteJson([(0, 1, 60)]), "natural", 44_100, "flite", samplesPerBar: 88_200);   // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        short[] samples = Wav.Decode((await RenderWordsAsync(text, notes, "natural", 44_100, "flite", samplesPerBar: 88_200))[0].Data).Samples;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long estimate = Ample.Speech.SpeechMemory.EstimateBlockBytes(samples.Length, text, null, 1, "flite", wordsMode: true, 44_100);

        Assert.True(allocated <= estimate, $"flite words allocated {allocated} bytes, the estimate says {estimate}");
    }

    [Fact]
    public async Task The_take_metadata_holds_the_word_intervals_that_the_editor_reads()
    {
        // web/lib/speech-timing.mjs reads speech.words as Index, StartSample, EndSample, Text: this pins the shape it depends on
        string notes = NoteJson([(0, 1, 60), (1, 1, 62)]);
        string metadata = (await RenderWordsAsync("one two", notes, "gate", 44_100, samplesPerBar: 88_200)).Single().MetadataJson!;

        using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(metadata);
        System.Text.Json.JsonElement[] words = [.. json.RootElement.GetProperty("speech").GetProperty("words").EnumerateArray()];
        Assert.Equal(2, words.Length);
        Assert.Equal(["one", "two"], words.Select(word => word.GetProperty("Text").GetString()));
        Assert.All(words, word => Assert.True(word.GetProperty("EndSample").GetInt32() > word.GetProperty("StartSample").GetInt32()));
        Assert.True(words[0].GetProperty("EndSample").GetInt32() <= words[1].GetProperty("StartSample").GetInt32() + 1);
    }

    [Fact]
    public async Task Every_word_interval_names_its_place_in_the_text_in_both_modes()
    {
        const string text = "Go 42, now! It's fine";
        string[] expected = ["Go", "42", "42", "now", "It's", "fine"];   // a number is spoken as digits that share its place
        string words = (await RenderWordsAsync(text, NoteJson(Enumerable.Range(0, 6).Select(i => (i * 0.5, 0.4, 60 + i))), "gate", 44_100, samplesPerBar: 88_200)).Single().MetadataJson!;
        string spoken = await TextMetadataAsync(text);

        foreach (string metadata in new[] { words, spoken })
        {
            using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(metadata);
            System.Text.Json.JsonElement[] items = [.. json.RootElement.GetProperty("speech").GetProperty("words").EnumerateArray()];
            Assert.Equal(expected, items.Select(item => text[item.GetProperty("From").GetInt32()..item.GetProperty("To").GetInt32()]));
        }
    }

    private async Task<string> TextMetadataAsync(string text)
    {
        BlockNode block = new() { Type = "speech", Name = "voice" };
        block.Parameters.Add(new Parameter("text", text, 0));
        RenderRequest request = new(block, EngineRig.Spec.Templates["speech"], 22_050, 22_050 * 16, 1, 1, new HashSet<int> { 1 }, new Dictionary<string, byte[]>(), 17, folder);
        return (await BuiltInRenderers.All.Single(renderer => renderer.BlockType == "speech").RenderAsync(request, CancellationToken.None)).Single().MetadataJson!;
    }

    [Fact]
    public async Task A_speech_block_without_an_engine_uses_humanico()
    {
        const string notes = "{\"notes\":[{\"bar\":1,\"beat\":0,\"beats\":2,\"pitch\":60,\"velocity\":127}]}";
        byte[] byDefault = (await RenderWordsAsync("hello", notes, "natural", 10_000)).Single().Data;
        byte[] humanico = (await RenderWordsAsync("hello", notes, "natural", 10_000, "humanico")).Single().Data;
        byte[] retro = (await RenderWordsAsync("hello", notes, "natural", 10_000, "retro")).Single().Data;

        Assert.Equal(humanico, byDefault);
        Assert.NotEqual(retro, byDefault);
    }

    [Fact]
    public async Task A_word_whose_dictionary_line_has_an_inline_comment_is_spoken_by_the_default_engine()
    {
        // "hiv EY1 CH AY1 V IY1 # abbrev" in cmudict.dict: the comment must not become phonemes
        const string notes = "{\"notes\":[{\"bar\":1,\"beat\":0,\"beats\":2,\"pitch\":60,\"velocity\":127}]}";
        (short[] samples, _) = Wav.Decode((await RenderWordsAsync("hiv", notes, "natural", 10_000)).Single().Data);

        Assert.Contains(samples, sample => sample != 0);
    }

    [Fact]
    public async Task A_later_range_matches_the_same_slice_of_a_full_render()
    {
        IBlockRenderer renderer = BuiltInRenderers.All.Single(item => item.BlockType == "speech");
        byte[] fullData = (await renderer.RenderAsync(Request(1, 7, new HashSet<int> { 5, 6 }), CancellationToken.None)).Single().Data;
        byte[] rangeData = (await renderer.RenderAsync(Request(6, 1, new HashSet<int> { 5, 6 }), CancellationToken.None)).Single().Data;
        (short[] full, _) = Wav.Decode(fullData);
        (short[] range, _) = Wav.Decode(rangeData);

        Assert.Equal(full.AsSpan(5_000, 1_000).ToArray(), range);
    }

    [Fact]
    public async Task Words_mode_range_preserves_the_tail_of_a_word_started_before_the_range()
    {
        IBlockRenderer renderer = BuiltInRenderers.All.Single(item => item.BlockType == "speech");
        BlockNode fullBlock = new() { Type = "speech", Name = "voice" };
        fullBlock.Parameters.Add(new Parameter("mode", "words", 0));
        fullBlock.Parameters.Add(new Parameter("text", "hello robot", 0));
        string notes = "{\"notes\":[{\"bar\":5,\"beat\":0,\"beats\":2,\"pitch\":60,\"velocity\":127}]}";
        Dictionary<string, byte[]> inputs = new() { ["notes"] = Encoding.UTF8.GetBytes(notes) };
        RenderRequest fullRequest = new(fullBlock, EngineRig.Spec.Templates["speech"], 1_000, 1_000, 1, 7,
            new HashSet<int> { 5, 6 }, inputs, 17, folder);
        RenderRequest rangeRequest = fullRequest with { FirstBar = 6, BarCount = 1 };

        short[] full = Wav.Decode((await renderer.RenderAsync(fullRequest, CancellationToken.None))[0].Data).Samples;
        short[] range = Wav.Decode((await renderer.RenderAsync(rangeRequest, CancellationToken.None))[0].Data).Samples;

        Assert.Equal(full.AsSpan(5_000, 1_000).ToArray(), range);
    }

    [Fact]
    public async Task Build_plan_changes_when_the_speech_engine_changes()
    {
        const string header = "composition \"speech\" { ample 1 tempo 120 master-seed 7 }\n";
        string Song(string engine) => header + $"speech voice at 1 for 1 {{\n  engine \"{engine}\"\n  text \"hello\"\n}}\n";
        using EngineRig retroRig = await EngineRig.CreateAsync();
        using EngineRig humanicoRig = await EngineRig.CreateAsync();
        BuildReport retro = await retroRig.Engine.BuildAsync(EngineRig.Parse(Song("retro")));
        BuildReport humanico = await humanicoRig.Engine.BuildAsync(EngineRig.Parse(Song("humanico")));
        string retroTake = retro.Blocks.Single(b => b.Block == "voice").TakeId!;
        string humanicoTake = humanico.Blocks.Single(b => b.Block == "voice").TakeId!;
        StoredPlan retroPlan = retroRig.Bank.GetPlan(retroRig.Bank.GetTake(retroTake).PlanId);
        StoredPlan humanicoPlan = humanicoRig.Bank.GetPlan(humanicoRig.Bank.GetTake(humanicoTake).PlanId);

        Assert.NotEqual(retroPlan.PlanId, humanicoPlan.PlanId);
        Assert.Contains("humanico/klatt-candidate-3", humanicoPlan.Plan.ToolprintJson);
        Assert.Contains("humanico", humanicoPlan.Plan.SettingsJson);
    }

    [Fact]
    public async Task Build_publishes_a_words_speech_take_with_timing_metadata()
    {
        const string song = "composition \"speech\" { ample 1 tempo 120 master-seed 7 }\n"
            + "midi-clip melody at 1 for 1 {\n  notes 1:0:1:60:100\n}\n"
            + "speech voice at 1 for 1 {\n  mode \"words\"\n  text \"hello\"\n  from melody.notes -> notes\n}\n";
        using EngineRig rig = await EngineRig.CreateAsync();
        BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse(song));

        TakeRecord take = rig.Bank.GetTake(report.Blocks.Single(b => b.Block == "voice").TakeId!);
        StoredPlan plan = rig.Bank.GetPlan(take.PlanId);
        Assert.Contains("retro/formant-2", plan.Plan.ToolprintJson);
        Assert.Contains("mode", plan.Plan.SettingsJson);
        Assert.Contains(take.Data, data => data.Role == "out");
    }

    [Fact]
    public void Speech_template_exposes_optional_notes_input_for_words_mode()
    {
        TemplateSpec template = EngineRig.Spec.Templates["speech"];

        Assert.Contains(template.Inputs, input => input.Name == "notes" && input.Type == "midi");
    }

    [Fact]
    public async Task Words_mode_uses_library_tokenization_for_punctuation()
    {
        IReadOnlyList<RenderOutput> outputs = await RenderWordsAsync(
            "hello, world", "{\"notes\":[{\"bar\":1,\"beat\":0,\"beats\":1,\"pitch\":60,\"velocity\":127},{\"bar\":1,\"beat\":1,\"beats\":1,\"pitch\":62,\"velocity\":127}]}", "natural", 10_000);

        string metadata = outputs.Single().MetadataJson!;
        Assert.Contains("\"Text\":\"hello\"", metadata);
        Assert.Contains("\"Text\":\"world\"", metadata);
        Assert.DoesNotContain("hello,", metadata);
    }

    [Fact]
    public async Task Build_cancellation_stops_speech_before_publishing_a_take()
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        using CancellationTokenSource cancellation = new();
        string text = string.Join(' ', Enumerable.Repeat("hello", 2_000));
        string song = $"composition \"speech\" {{ ample 1 tempo 120 master-seed 7 }}\nspeech voice at 1 for 40 {{\n  text \"{text}\"\n}}\n";
        BuildReport report = await rig.Engine.BuildAsync(EngineRig.Parse(song), new BuildOptions(Detail: progress =>
        {
            if (progress.Block == "voice" && progress.Steps.Any(step => step.Name == "synthesis")) cancellation.Cancel();
        }), cancellation.Token);

        Assert.Equal("cancelled", report.Blocks.Single(block => block.Block == "voice").Status);
        Assert.Null(report.Blocks.Single(block => block.Block == "voice").TakeId);
        Assert.DoesNotContain(rig.Bank.ListTakes(), take => rig.Bank.GetPlan(take.PlanId).Plan.BlockPath == "voice");
    }

    [Fact]
    public async Task Speech_renderer_receives_configurable_budget_settings()
    {
        IBlockRenderer renderer = BuiltInRenderers.All.Single(item => item.BlockType == "speech");

        Assert.Equal("speech", Assert.IsAssignableFrom<IConfigurableRenderer>(renderer).SettingsKey);
        Dictionary<string, string> settings = new() { ["max_output_samples"] = "100" };
        await Assert.ThrowsAsync<SpeechResourceLimitException>(async () => await renderer.RenderAsync(
            Request(1, 1, new HashSet<int> { 1 }, settings), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Speech_renderer_rejects_output_working_buffer_before_allocation()
    {
        IBlockRenderer renderer = BuiltInRenderers.All.Single(item => item.BlockType == "speech");
        Dictionary<string, string> settings = new() { ["max_working_samples"] = "100" };

        await Assert.ThrowsAsync<SpeechResourceLimitException>(async () => await renderer.RenderAsync(
            Request(1, 1, new HashSet<int> { 1 }, settings), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Speech_renderer_applies_gain_in_decibels_after_rendering()
    {
        async Task<short[]> Render(string? gain)
        {
            BlockNode block = new() { Type = "speech", Name = "voice" };
            block.Parameters.Add(new Parameter("text", "hello", 0));
            if (gain is not null) block.Parameters.Add(new Parameter("gain", gain, 0));
            RenderRequest request = new(block, EngineRig.Spec.Templates["speech"], 10_000, 40_000, 1, 1,
                new HashSet<int> { 1 }, new Dictionary<string, byte[]>(), 17, folder);
            byte[] data = (await BuiltInRenderers.All.Single(renderer => renderer.BlockType == "speech").RenderAsync(request, CancellationToken.None))[0].Data;
            return Wav.Decode(data).Samples;
        }

        Assert.NotEqual(await Render(null), await Render("6"));
    }

    [Fact]
    public async Task Words_mode_counts_the_integer_mix_buffer_in_working_budget()
    {
        Dictionary<string, string> settings = new() { ["max_working_samples"] = "15000" };

        await Assert.ThrowsAsync<SpeechResourceLimitException>(() => RenderWordsAsync(
            "hello", "{\"notes\":[{\"bar\":1,\"beat\":0,\"beats\":1,\"pitch\":60,\"velocity\":127}]}", "natural", 10_000,
            settings: settings));
    }

    [Fact]
    public async Task Speech_renderer_rejects_output_budget_before_words_rendering()
    {
        await Assert.ThrowsAsync<SpeechResourceLimitException>(() => RenderWordsAsync(
            "hello", "{\"notes\":[{\"bar\":1,\"beat\":0,\"beats\":1,\"pitch\":60,\"velocity\":127}]}", "natural", 10_000,
            settings: new Dictionary<string, string> { ["max_output_samples"] = "100" }));
    }

    [Fact]
    public async Task Speech_renderer_rejects_words_working_budget_before_synthesis()
    {
        await Assert.ThrowsAsync<SpeechResourceLimitException>(() => RenderWordsAsync(
            "hello", "{\"notes\":[{\"bar\":1,\"beat\":0,\"beats\":1,\"pitch\":60,\"velocity\":127}]}", "natural", 10_000,
            settings: new Dictionary<string, string> { ["max_working_samples"] = "10" }));
    }

    [Fact]
    public async Task Speech_renderer_rejects_cumulative_words_working_budget_before_synthesis()
    {
        await Assert.ThrowsAsync<SpeechResourceLimitException>(() => RenderWordsAsync(
            "hello robot", "{\"notes\":[{\"bar\":1,\"beat\":0,\"beats\":1,\"pitch\":60,\"velocity\":127},{\"bar\":1,\"beat\":1,\"beats\":1,\"pitch\":62,\"velocity\":127}]}", "natural", 10_000,
            settings: new Dictionary<string, string> { ["max_working_samples"] = "8000" }));
    }

    [Fact]
    public async Task Speech_renderer_honors_cancellation_before_output_allocation()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => BuiltInRenderers.All.Single(renderer => renderer.BlockType == "speech")
            .RenderAsync(Request(1, 1, new HashSet<int> { 1 }), cancellation.Token).AsTask());
    }

    [Fact]
    public void Wav_encoder_honors_cancellation_before_allocating_output()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => Wav.Encode(new short[1], 10_000, cancellation.Token));
    }

    [Fact]
    public async Task Words_mode_reports_progress_and_stops_before_publishing_after_cancellation()
    {
        using CancellationTokenSource cancellation = new();
        BlockNode block = new() { Type = "speech", Name = "voice" };
        block.Parameters.Add(new Parameter("mode", "words", 0));
        block.Parameters.Add(new Parameter("text", "one two", 0));
        Dictionary<string, byte[]> inputs = new() { ["notes"] = Encoding.UTF8.GetBytes("{\"notes\":[{\"bar\":1,\"beat\":0,\"beats\":1,\"pitch\":60,\"velocity\":127},{\"bar\":1,\"beat\":1,\"beats\":1,\"pitch\":62,\"velocity\":127}]}") };
        List<ProgressReport> progress = [];
        RenderRequest request = new(block, EngineRig.Spec.Templates["speech"], 10_000, 40_000, 1, 1,
            new HashSet<int> { 1 }, inputs, 17, folder, Report: report =>
            {
                progress.Add(report);
                if (report.Step == "words-synthesis" && report.StepFraction > 0) cancellation.Cancel();
            });

        await Assert.ThrowsAsync<OperationCanceledException>(() => BuiltInRenderers.All.Single(renderer => renderer.BlockType == "speech")
            .RenderAsync(request, cancellation.Token).AsTask());
        Assert.Contains(progress, report => report.Step == "words-synthesis");
    }

    [Fact]
    public async Task Words_mode_cancels_during_resampling()
    {
        using CancellationTokenSource cancellation = new();
        BlockNode block = new() { Type = "speech", Name = "voice" };
        block.Parameters.Add(new Parameter("mode", "words", 0));
        block.Parameters.Add(new Parameter("text", "hello", 0));
        Dictionary<string, byte[]> inputs = new() { ["notes"] = Encoding.UTF8.GetBytes("{\"notes\":[{\"bar\":1,\"beat\":0,\"beats\":8,\"pitch\":60,\"velocity\":127}]}") };
        List<ProgressReport> progress = [];
        RenderRequest request = new(block, EngineRig.Spec.Templates["speech"], 48_000, 192_000, 1, 1,
            new HashSet<int> { 1 }, inputs, 17, folder, Report: report =>
            {
                progress.Add(report);
                if (report.Step == "speech-resampling" && report.StepFraction > 0) cancellation.Cancel();
            });

        await Assert.ThrowsAsync<OperationCanceledException>(() => BuiltInRenderers.All.Single(renderer => renderer.BlockType == "speech")
            .RenderAsync(request, cancellation.Token).AsTask());
        Assert.Contains(progress, report => report.Step == "speech-resampling");
    }

    [Fact]
    public async Task Words_mode_cancels_during_notes_planning()
    {
        using CancellationTokenSource cancellation = new();
        BlockNode block = new() { Type = "speech", Name = "voice" };
        block.Parameters.Add(new Parameter("mode", "words", 0));
        block.Parameters.Add(new Parameter("text", "hello", 0));
        Dictionary<string, byte[]> inputs = new() { ["notes"] = Encoding.UTF8.GetBytes("{\"notes\":[]}") };
        List<ProgressReport> progress = [];
        RenderRequest request = new(block, EngineRig.Spec.Templates["speech"], 1_000, 1_000, 1, 100,
            new HashSet<int>(Enumerable.Range(1, 100)), inputs, 17, folder, Report: report =>
            {
                progress.Add(report);
                if (report.Step == "words-planning" && report.StepFraction > 0) cancellation.Cancel();
            });

        await Assert.ThrowsAsync<OperationCanceledException>(() => BuiltInRenderers.All.Single(renderer => renderer.BlockType == "speech")
            .RenderAsync(request, cancellation.Token).AsTask());
        Assert.Contains(progress, report => report.Step == "words-planning");
    }

    [Fact]
    public void Toolprint_identifies_the_audible_scaffold_algorithm()
    {
        IBlockRenderer renderer = BuiltInRenderers.All.Single(item => item.BlockType == "speech");

        Assert.Contains("retro/formant-2", renderer.ToolprintJson);
        Assert.Contains("splitmix64-formant-glottal-linear-resample", renderer.ToolprintJson);
        Assert.Contains("\"algorithmVersion\":8", renderer.ToolprintJson);
        Assert.Contains("SplitMix64/ample-2", renderer.ToolprintJson);
        Assert.Contains("\"wordsMixVoiceHeadroom\":4", renderer.ToolprintJson);
        Assert.Contains("\"speechLevels\":{\"retro\":5,\"humanico\":0,\"flite\":-3}", renderer.ToolprintJson);   // a change of an offset changes the toolprint, so matched takes are made again
        Assert.Equal("1.8", renderer.Version);
        Assert.Contains(HumanicoReferenceManifest.PinnedFixture.DictionarySha256, renderer.ToolprintJson);
        Assert.Contains(HumanicoReferenceManifest.PinnedFixture.UpstreamRevision, renderer.ToolprintJson);
    }

    [Fact]
    public async Task Humanico_engine_is_explicit_in_renderer_metadata()
    {
        IReadOnlyList<RenderOutput> result = await RenderWordsAsync("hello", "{\"notes\":[{\"bar\":1,\"beat\":0,\"beats\":1,\"pitch\":60,\"velocity\":127}]}", "natural", 10_000, "humanico");

        Assert.Contains("humanico/klatt-candidate-3", result[0].MetadataJson);
        Assert.Contains("\"provisional\":true", result[0].MetadataJson);
        Assert.Contains("candidate-calibrated-klatt-linear-resample", result[0].MetadataJson);
        Assert.Contains("internalSampleRate", result[0].MetadataJson);
    }

    [Fact]
    public async Task Unknown_engine_is_rejected_by_renderer()
    {
        await Assert.ThrowsAsync<AmpleException>(() => RenderWordsAsync("hello", "{\"notes\":[{\"bar\":1,\"beat\":0,\"beats\":1,\"pitch\":60,\"velocity\":127}]}", "natural", 10_000, "future"));
    }

    [Fact]
    public async Task The_flite_engine_speaks_words_mode()
    {
        // flite sings a word per note like the other engines: one word, one note, sound that starts with the note
        short[] samples = Wav.Decode((await RenderWordsAsync("hello", NoteJson([(0, 1, 60)]), "natural", 22_050, "flite", samplesPerBar: 44_100)).Single().Data).Samples;

        Assert.Contains(samples[..1_000], sample => Math.Abs((int)sample) > 40);   // the mix keeps a quarter of the level of a voice
        Assert.Contains(samples, sample => Math.Abs((int)sample) > 1_000);
    }

    [Theory]
    [InlineData("gate")]
    [InlineData("fit")]
    public async Task Flite_and_humanico_place_the_same_words_on_the_same_notes(string policy)
    {
        // the planner (which word on which note) and the cropping to the gate do not depend on the engine: the notes are shorter than any of the words
        string notes = NoteJson([(0, 0.25, 60), (1, 0.25, 64), (2, 0.25, 67)]);
        (int Start, int End, string Text)[] flite = WordIntervals((await RenderWordsAsync("one two three", notes, policy, 44_100, "flite", samplesPerBar: 88_200)).Single().MetadataJson!);
        (int Start, int End, string Text)[] humanico = WordIntervals((await RenderWordsAsync("one two three", notes, policy, 44_100, "humanico", samplesPerBar: 88_200)).Single().MetadataJson!);

        Assert.Equal(humanico, flite);
        Assert.Equal(3, flite.Length);
    }

    [Fact]
    public async Task A_flite_word_starts_with_its_note_whatever_the_policy()
    {
        string notes = NoteJson([(0, 1, 60), (2, 1, 62)]);
        foreach (string policy in new[] { "natural", "gate", "fit" })
        {
            (int Start, int End, string Text)[] words = WordIntervals((await RenderWordsAsync("one two", notes, policy, 44_100, "flite", samplesPerBar: 88_200)).Single().MetadataJson!);
            Assert.Equal([0, 44_100], words.Select(word => word.Start));
            Assert.All(words, word => Assert.True(word.End > word.Start));
        }
    }

    [Fact]
    public async Task A_flite_word_names_its_place_in_the_text()
    {
        const string text = "Go 42, now! It's fine";
        string metadata = (await RenderWordsAsync(text, NoteJson(Enumerable.Range(0, 6).Select(i => (i * 0.5, 0.4, 60 + i))), "gate", 44_100, "flite", samplesPerBar: 88_200)).Single().MetadataJson!;

        using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(metadata);
        System.Text.Json.JsonElement[] items = [.. json.RootElement.GetProperty("speech").GetProperty("words").EnumerateArray()];
        Assert.Equal(["Go", "42", "42", "now", "It's", "fine"], items.Select(item => text[item.GetProperty("From").GetInt32()..item.GetProperty("To").GetInt32()]));
        Assert.StartsWith("flite/", json.RootElement.GetProperty("speech").GetProperty("engine").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_note_that_the_voice_cannot_follow_is_sung_an_octave_away_and_the_take_says_so()
    {
        // 95 Hz at note 60: note 100 asks for about 958 Hz, note 12 for about 3 Hz; the voice sings 50 to 480 Hz, the other notes are folded by octaves
        string metadata = (await RenderWordsAsync("high low fine", NoteJson([(0, 1, 100), (1, 1, 12), (2, 1, 62)]), "natural", 22_050, "flite", samplesPerBar: 44_100)).Single().MetadataJson!;

        using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(metadata);
        System.Text.Json.JsonElement[] warnings = [.. json.RootElement.GetProperty("speech").GetProperty("warnings").EnumerateArray()];
        Assert.Equal([0, 1], warnings.Where(w => w.GetProperty("Kind").GetString() == "pitch-folded").Select(w => w.GetProperty("WordIndex").GetInt32()));
    }

    [Fact]
    public async Task Flite_words_in_a_fit_policy_are_sped_up_and_the_take_says_so()
    {
        // a word that is longer than its note is spoken faster (up to fit_max_rate) before it is cropped
        string metadata = (await RenderWordsAsync("remember", NoteJson([(0, 0.5, 60)]), "fit", 22_050, "flite", samplesPerBar: 44_100)).Single().MetadataJson!;

        Assert.Contains("fit-accelerated", metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Flite_words_keep_to_the_working_budget()
    {
        Dictionary<string, string> tiny = new() { ["max_working_samples"] = "2000" };

        await Assert.ThrowsAsync<SpeechResourceLimitException>(() =>
            RenderWordsAsync("hello world again", NoteJson([(0, 1, 60), (1, 1, 62), (2, 1, 64)]), "natural", 22_050, "flite", settings: tiny, samplesPerBar: 44_100));
    }

    [Fact]
    public async Task The_brightness_of_the_flite_voice_also_shapes_its_words()
    {
        string notes = NoteJson([(0, 1, 60)]);
        short[] plain = Wav.Decode((await RenderWordsAsync("sing", notes, "natural", 22_050, "flite", samplesPerBar: 44_100)).Single().Data).Samples;
        short[] bright = Wav.Decode((await RenderWordsAsync("sing", notes, "natural", 22_050, "flite", samplesPerBar: 44_100, extra: ("brightness", "9"))).Single().Data).Samples;

        Assert.NotEqual(plain, bright);
        // a high shelf adds to the high end: the sum of the absolute steps between samples (the high end) grows more than the level itself
        double Steps(short[] s) { double sum = 0; for (int i = 1; i < s.Length; i++) sum += Math.Abs(s[i] - s[i - 1]); return sum; }
        double Level(short[] s) => s.Sum(x => Math.Abs((double)x));
        Assert.True(Steps(bright) / Level(bright) > Steps(plain) / Level(plain));
    }

    [Theory]
    [InlineData("lead_trim", "\"on\"")]
    [InlineData("pause_scale", "2")]
    public async Task Lead_trim_and_pause_scale_belong_to_the_text_of_the_flite_voice(string name, string value)
    {
        AmpleException error = await Assert.ThrowsAsync<AmpleException>(() =>
            RenderWordsAsync("hello", NoteJson([(0, 1, 60)]), "natural", 22_050, "flite", samplesPerBar: 44_100, extra: (name, value)));

        Assert.Equal("AMP5351", error.DiagnosticCode);
        Assert.Contains("words mode", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Flite_words_render_identically_every_time()
    {
        string notes = NoteJson([(0, 1, 60), (1, 1, 64), (2, 1, 67)]);
        byte[] first = (await RenderWordsAsync("one two three", notes, "gate", 22_050, "flite", samplesPerBar: 44_100)).Single().Data;
        byte[] second = (await RenderWordsAsync("one two three", notes, "gate", 22_050, "flite", samplesPerBar: 44_100)).Single().Data;

        Assert.Equal(first, second);
    }

    private static (int Start, int End, string Text)[] WordIntervals(string metadata)
    {
        using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(metadata);
        return [.. json.RootElement.GetProperty("speech").GetProperty("words").EnumerateArray()
            .Select(word => (word.GetProperty("StartSample").GetInt32(), word.GetProperty("EndSample").GetInt32(), word.GetProperty("Text").GetString()!))];
    }

    [Theory]
    [InlineData("voice", "\"other\"", "humanico")]
    [InlineData("pitch_range", "5", "humanico")]
    [InlineData("voice", "\"other\"", "flite")]
    [InlineData("pitch_range", "500", "flite")]
    public async Task Voice_and_pitch_range_belong_to_the_flite_engine_and_are_checked(string name, string value, string engine)
    {
        BlockNode block = new() { Type = "speech", Name = "voice" };
        block.Parameters.Add(new Parameter("engine", $"\"{engine}\"", 0));
        block.Parameters.Add(new Parameter("text", "\"hello\"", 0));
        block.Parameters.Add(new Parameter(name, value, 0));
        RenderRequest request = new(block, EngineRig.Spec.Templates["speech"], 16_000, 16_000, 1, 1, new HashSet<int> { 1 }, new Dictionary<string, byte[]>(), 17, folder);

        AmpleException error = await Assert.ThrowsAsync<AmpleException>(async () =>
            await BuiltInRenderers.All.Single(renderer => renderer.BlockType == "speech").RenderAsync(request, CancellationToken.None));

        Assert.Equal("AMP5351", error.DiagnosticCode);
    }

    [Fact]
    public async Task A_flite_block_speaks_phonemes_as_one_word_and_refuses_a_phoneme_it_does_not_have()
    {
        async Task<IReadOnlyList<RenderOutput>> RenderAsync(string phonemes)
        {
            BlockNode block = new() { Type = "speech", Name = "voice" };
            block.Parameters.Add(new Parameter("engine", "\"flite\"", 0));
            block.Parameters.Add(new Parameter("phonemes", $"\"{phonemes}\"", 0));
            RenderRequest request = new(block, EngineRig.Spec.Templates["speech"], 22_050, 88_200, 1, 1, new HashSet<int> { 1 }, new Dictionary<string, byte[]>(), 17, folder);
            return await BuiltInRenderers.All.Single(renderer => renderer.BlockType == "speech").RenderAsync(request, CancellationToken.None);
        }

        IReadOnlyList<RenderOutput> spoken = await RenderAsync("HH AH0 L OW1 W ER1 L D");
        Assert.Contains(Wav.Decode(spoken[0].Data).Samples, sample => sample != 0);
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(spoken[0].MetadataJson!);
        System.Text.Json.JsonElement words = document.RootElement.GetProperty("speech").GetProperty("words");
        Assert.Equal(["[phonemes]"], words.EnumerateArray().Select(word => word.GetProperty("Text").GetString()));

        AmpleException error = await Assert.ThrowsAsync<AmpleException>(async () => await RenderAsync("HH QQ L OW1"));
        Assert.Equal("AMP5351", error.DiagnosticCode);
        Assert.Contains("QQ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_flite_text_block_speaks_with_its_own_pitch_and_reports_the_words_in_the_text()
    {
        async Task<IReadOnlyList<RenderOutput>> RenderAsync(params (string Name, string Value)[] extra)
        {
            BlockNode block = new() { Type = "speech", Name = "voice" };
            block.Parameters.Add(new Parameter("engine", "\"flite\"", 0));
            block.Parameters.Add(new Parameter("text", "\"Hello world, 12 monkeys.\"", 0));
            foreach ((string name, string value) in extra) block.Parameters.Add(new Parameter(name, value, 0));
            RenderRequest request = new(block, EngineRig.Spec.Templates["speech"], 22_050, 88_200, 1, 1, new HashSet<int> { 1 }, new Dictionary<string, byte[]>(), 17, folder);
            return await BuiltInRenderers.All.Single(renderer => renderer.BlockType == "speech").RenderAsync(request, CancellationToken.None);
        }

        IReadOnlyList<RenderOutput> bare = await RenderAsync();
        IReadOnlyList<RenderOutput> same = await RenderAsync(("pitch_hz", "95"));
        IReadOnlyList<RenderOutput> other = await RenderAsync(("pitch_hz", "150"));

        Assert.Contains(Wav.Decode(bare[0].Data).Samples, sample => sample != 0);
        Assert.Equal(bare[0].Data, same[0].Data);   // a block that does not write pitch_hz speaks at the 95 Hz of the voice
        Assert.NotEqual(bare[0].Data, other[0].Data);
        string metadata = bare[0].MetadataJson!;
        Assert.Contains("\"provisional\":false", metadata);   // the flite voice is not provisional (Humanico still is)
        Assert.Contains("flite-kal16-diphone-lpc-linear-resample", metadata);
        Assert.Contains("\"internalSampleRate\":16000", metadata);
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(metadata);
        System.Text.Json.JsonElement words = document.RootElement.GetProperty("speech").GetProperty("words");
        Assert.Equal(["hello", "world", "twelve", "monkeys"], words.EnumerateArray().Select(word => word.GetProperty("Text").GetString()));
        Assert.Equal([0, 6, 13, 16], words.EnumerateArray().Select(word => word.GetProperty("From").GetInt32()));
    }

    private async Task<IReadOnlyList<RenderOutput>> RenderTextBlockAsync(string engine, params (string Name, string Value)[] extra)
    {
        BlockNode block = new() { Type = "speech", Name = "voice" };
        block.Parameters.Add(new Parameter("engine", $"\"{engine}\"", 0));
        if (!extra.Any(e => e.Name == "text")) block.Parameters.Add(new Parameter("text", "\"Hello world, 12 monkeys.\"", 0));
        foreach ((string name, string value) in extra) block.Parameters.Add(new Parameter(name, value, 0));
        RenderRequest request = new(block, EngineRig.Spec.Templates["speech"], 22_050, 88_200, 1, 1, new HashSet<int> { 1 }, new Dictionary<string, byte[]>(), 17, folder);
        return await BuiltInRenderers.All.Single(renderer => renderer.BlockType == "speech").RenderAsync(request, CancellationToken.None);
    }

    private static int[][] WordPlaces(RenderOutput output)
    {
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(output.MetadataJson!);
        System.Text.Json.JsonElement words = document.RootElement.GetProperty("speech").GetProperty("words");
        return [.. words.EnumerateArray().Select(word => new[] { word.GetProperty("StartSample").GetInt32(), word.GetProperty("EndSample").GetInt32() })];
    }

    private static double RmsDb(RenderOutput output) => 20 * Math.Log10(Math.Sqrt(Wav.Decode(output.Data).Samples.Select(sample => (double)sample * sample).Average()));

    [Fact]
    public async Task The_flite_options_are_off_by_default_and_a_block_that_writes_the_defaults_is_the_same_take()
    {
        IReadOnlyList<RenderOutput> bare = await RenderTextBlockAsync("flite");
        IReadOnlyList<RenderOutput> defaults = await RenderTextBlockAsync("flite", ("lead_trim", "\"off\""), ("pause_scale", "1"), ("brightness", "0"), ("level", "\"matched\""));

        Assert.Equal(bare[0].Data, defaults[0].Data);
    }

    [Fact]
    public async Task Lead_trim_starts_the_first_word_at_the_beginning_of_the_block_and_the_timings_follow()
    {
        IReadOnlyList<RenderOutput> bare = await RenderTextBlockAsync("flite");
        IReadOnlyList<RenderOutput> trimmed = await RenderTextBlockAsync("flite", ("lead_trim", "\"on\""));

        int[][] before = WordPlaces(bare[0]), after = WordPlaces(trimmed[0]);
        Assert.InRange(before[0][0], 2_000, 20_000);   // the voice opens with a pause of a few hundred milliseconds
        Assert.InRange(after[0][0], 1, 40);   // one millisecond is kept
        Assert.Equal(before[1][0] - before[0][0], after[1][0] - after[0][0]);   // the words keep their distance
        Assert.Contains("\"leadTrim\":true", trimmed[0].MetadataJson);
        Assert.NotEqual(bare[0].Data, trimmed[0].Data);
    }

    [Fact]
    public async Task Pause_scale_moves_the_words_after_a_pause_and_leaves_the_words_themselves()
    {
        int[][] bare = WordPlaces((await RenderTextBlockAsync("flite"))[0]);
        int[][] none = WordPlaces((await RenderTextBlockAsync("flite", ("pause_scale", "0")))[0]);
        int[][] doubled = WordPlaces((await RenderTextBlockAsync("flite", ("pause_scale", "2")))[0]);

        int comma = bare[2][0] - bare[1][1];   // the pause after "world,"
        Assert.True(comma > 1_000, "the text has a comma pause of its own");
        Assert.Equal(0, none[2][0] - none[1][1]);
        Assert.InRange(doubled[2][0] - doubled[1][1], comma * 2 - 2, comma * 2 + 2);
        Assert.InRange(Math.Abs(bare[1][1] - bare[1][0] - (none[1][1] - none[1][0])), 0, 2);   // the length of a word does not change (up to rounding at the sample rate)
        Assert.Equal(bare[0][0], none[0][0]);
    }

    [Fact]
    public async Task Brightness_changes_the_sound_only_when_asked_and_keeps_the_length()
    {
        IReadOnlyList<RenderOutput> bare = await RenderTextBlockAsync("flite");
        IReadOnlyList<RenderOutput> bright = await RenderTextBlockAsync("flite", ("brightness", "6"));

        Assert.NotEqual(bare[0].Data, bright[0].Data);
        Assert.Equal(bare[0].Data.Length, bright[0].Data.Length);
        Assert.Contains("\"brightnessDb\":6", bright[0].MetadataJson);
    }

    [Theory]
    [InlineData("retro", 5.0)]
    [InlineData("humanico", 0.0)]
    [InlineData("flite", -3.0)]
    public async Task Level_matched_adds_the_offset_of_the_engine_to_the_gain(string engine, double offset)
    {
        IReadOnlyList<RenderOutput> plain = await RenderTextBlockAsync(engine);
        IReadOnlyList<RenderOutput> matched = await RenderTextBlockAsync(engine, ("level", "\"matched\""));
        IReadOnlyList<RenderOutput> engineLevel = await RenderTextBlockAsync(engine, ("level", "\"engine\""));

        Assert.Equal(plain[0].Data, matched[0].Data);   // level matched is the default
        if (offset == 0) Assert.Equal(engineLevel[0].Data, matched[0].Data);   // level engine is what every engine did before renderer 1.8
        else Assert.Equal(offset, RmsDb(matched[0]) - RmsDb(engineLevel[0]), 0.1);
        Assert.Contains("\"level\":\"matched\"", matched[0].MetadataJson);
    }

    [Theory]
    [InlineData("humanico", "lead_trim", "\"on\"")]
    [InlineData("retro", "pause_scale", "2")]
    [InlineData("humanico", "brightness", "3")]
    [InlineData("flite", "lead_trim", "\"maybe\"")]
    [InlineData("flite", "pause_scale", "5")]
    [InlineData("flite", "pause_scale", "-1")]
    [InlineData("flite", "brightness", "13")]
    [InlineData("flite", "brightness", "-1")]
    [InlineData("flite", "level", "\"loud\"")]
    [InlineData("humanico", "level", "\"loud\"")]
    public async Task The_flite_options_and_the_level_are_checked(string engine, string name, string value)
    {
        AmpleException error = await Assert.ThrowsAsync<AmpleException>(async () => await RenderTextBlockAsync(engine, (name, value)));

        Assert.Equal("AMP5351", error.DiagnosticCode);
    }

    [Fact]
    public async Task A_flite_block_in_a_song_builds_and_is_a_different_take_than_the_other_engines()
    {
        const string header = "composition \"speech\" { ample 1 tempo 120 master-seed 7 }\n";
        string Song(string engine) => header + $"speech voice at 1 for 2 {{\n  engine \"{engine}\"\n  text \"hello\"\n}}\n";
        using EngineRig rig = await EngineRig.CreateAsync();
        BuildReport flite = await rig.Engine.BuildAsync(EngineRig.Parse(Song("flite")));
        BuildReport humanico = await rig.Engine.BuildAsync(EngineRig.Parse(Song("humanico")));
        string fliteTake = flite.Blocks.Single(block => block.Block == "voice").TakeId!;
        string humanicoTake = humanico.Blocks.Single(block => block.Block == "voice").TakeId!;

        Assert.NotEqual(fliteTake, humanicoTake);
        Assert.Contains("flite/cmu_us_kal16", rig.Bank.GetPlan(rig.Bank.GetTake(fliteTake).PlanId).Plan.ToolprintJson);
    }

    [Fact]
    public void The_toolprint_names_the_flite_data_so_that_a_changed_voice_makes_new_takes()
    {
        IBlockRenderer renderer = BuiltInRenderers.All.Single(item => item.BlockType == "speech");

        Assert.Contains("flite/cmu_us_kal16/6c9f20dc", renderer.ToolprintJson, StringComparison.Ordinal);
        Assert.Contains(Ample.Speech.Flite.Data.FliteDataIdentity.DataSha256, renderer.ToolprintJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Words_mode_assigns_positive_notes_in_order_and_changes_pitch()
    {
        const string notes = "{\"notes\":[{\"bar\":1,\"beat\":0,\"beats\":1,\"pitch\":48,\"velocity\":127}," +
            "{\"bar\":1,\"beat\":1,\"beats\":1,\"pitch\":72,\"velocity\":64}]}";
        IReadOnlyList<RenderOutput> low = await RenderWordsAsync("hello robot", notes, "natural", 10_000);
        IReadOnlyList<RenderOutput> high = await RenderWordsAsync("hello robot", notes.Replace("48", "72", StringComparison.Ordinal), "natural", 10_000);

        Assert.Equal("out", low[0].Role);
        Assert.NotEqual(low[0].Data, high[0].Data);
        Assert.Contains("\"mode\":\"words\"", low[0].MetadataJson);
        Assert.Contains("\"Text\":\"hello\"", low[0].MetadataJson);
    }

    [Fact]
    public async Task Words_mode_mixes_overlapping_voices_with_fixed_headroom()
    {
        const string overlapping = "{\"notes\":[{\"bar\":1,\"beat\":0,\"beats\":1,\"pitch\":48,\"velocity\":127},{\"bar\":1,\"beat\":0,\"beats\":1,\"pitch\":72,\"velocity\":127}]}";
        const string single = "{\"notes\":[{\"bar\":1,\"beat\":0,\"beats\":1,\"pitch\":72,\"velocity\":127}]}";
        short[] mixed = Wav.Decode((await RenderWordsAsync("hello robot", overlapping, "natural", 10_000))[0].Data).Samples;
        short[] lastVoiceOnly = Wav.Decode((await RenderWordsAsync("robot", single, "natural", 10_000))[0].Data).Samples;

        Assert.False(mixed.AsSpan().SequenceEqual(lastVoiceOnly));
        Assert.DoesNotContain(mixed, sample => sample is short.MinValue or short.MaxValue);
    }

    [Fact]
    public async Task Words_mode_rejects_more_than_four_simultaneous_voices()
    {
        const string notes = "{\"notes\":[{\"bar\":1,\"beat\":0,\"beats\":2,\"pitch\":48,\"velocity\":127},{\"bar\":1,\"beat\":0,\"beats\":2,\"pitch\":52,\"velocity\":127},{\"bar\":1,\"beat\":0,\"beats\":2,\"pitch\":55,\"velocity\":127},{\"bar\":1,\"beat\":0,\"beats\":2,\"pitch\":59,\"velocity\":127},{\"bar\":1,\"beat\":0,\"beats\":2,\"pitch\":64,\"velocity\":127}]}";

        await Assert.ThrowsAsync<AmpleException>(() => RenderWordsAsync("one two three four five", notes, "natural", 10_000));
    }

    private static string NoteJson(IEnumerable<(double Beat, double Beats, int Pitch)> notes)
        => "{\"notes\":[" + string.Join(",", notes.Select(n => FormattableString.Invariant(
            $"{{\"bar\":1,\"beat\":{n.Beat},\"beats\":{n.Beats},\"pitch\":{n.Pitch},\"velocity\":127}}"))) + "]}";

    [Fact]
    public async Task A_chord_with_more_notes_than_words_only_counts_the_notes_that_get_a_word()
    {
        // five notes at once but one word: only one voice sounds, so this is not five overlapping voices
        string chord = NoteJson([(0, 2, 48), (0, 2, 52), (0, 2, 55), (0, 2, 60), (0, 2, 64)]);
        short[] samples = Wav.Decode((await RenderWordsAsync("hello", chord, "natural", 10_000))[0].Data).Samples;

        Assert.Contains(samples, sample => sample != 0);
    }

    [Fact]
    public async Task Natural_words_count_for_as_long_as_they_sound_not_for_the_note_gate()
    {
        // five short notes 0.02 beat (20 ms here) apart whose gates (0.01 beat) never overlap, but a natural word goes on after its note-off
        string retrigger = NoteJson([(0, 0.01, 60), (0.02, 0.01, 62), (0.04, 0.01, 64), (0.06, 0.01, 65), (0.08, 0.01, 67)]);
        const string words = "one two three four five";

        AmpleException error = await Assert.ThrowsAsync<AmpleException>(() => RenderWordsAsync(words, retrigger, "natural", 10_000));
        Assert.Equal("AMP5351", error.DiagnosticCode);
        // the same notes are fine when the word is cut at the gate (gate) or sped up and cut (fit): the audible intervals do not overlap
        Assert.Contains(Wav.Decode((await RenderWordsAsync(words, retrigger, "gate", 10_000))[0].Data).Samples, sample => sample != 0);
        Assert.Contains(Wav.Decode((await RenderWordsAsync(words, retrigger, "fit", 10_000))[0].Data).Samples, sample => sample != 0);
    }

    [Fact]
    public async Task Four_overlapping_voices_at_full_velocity_do_not_clip()
    {
        string four = NoteJson([(0, 2, 48), (0, 2, 55), (0, 2, 60), (0, 2, 72)]);
        short[] samples = Wav.Decode((await RenderWordsAsync("one two three four", four, "natural", 10_000))[0].Data).Samples;

        Assert.DoesNotContain(samples, sample => sample is short.MinValue or short.MaxValue);
        Assert.Contains(samples, sample => Math.Abs((int)sample) > 1000);
    }

    [Fact]
    public async Task Gate_and_fit_policies_bound_a_word_to_the_note_length()
    {
        const string notes = "{\"notes\":[{\"bar\":1,\"beat\":0,\"beats\":1,\"pitch\":60,\"velocity\":127}]}";
        short[] gate = Wav.Decode((await RenderWordsAsync("hello", notes, "gate", 44_100, samplesPerBar: 44_100))[0].Data).Samples;
        short[] fit = Wav.Decode((await RenderWordsAsync("hello", notes, "fit", 44_100, samplesPerBar: 44_100))[0].Data).Samples;

        Assert.Equal(gate.Length, fit.Length);
        Assert.All(gate[11_025..20_000], sample => Assert.Equal(0, sample));
        Assert.All(fit[11_025..20_000], sample => Assert.Equal(0, sample));
    }

    [Fact]
    public async Task Gate_policy_fades_inside_the_note_boundary()
    {
        const string notes = "{\"notes\":[{\"bar\":1,\"beat\":0,\"beats\":1,\"pitch\":60,\"velocity\":127}]}";
        short[] gate = Wav.Decode((await RenderWordsAsync("hello", notes, "gate", 44_100, samplesPerBar: 44_100))[0].Data).Samples;

        Assert.Equal(0, gate[11_024]);
        Assert.Contains(gate[10_900..11_000], sample => sample != 0);
    }

    [Fact]
    public async Task Fit_metadata_reports_acceleration_and_crop_without_text()
    {
        const string notes = "{\"notes\":[{\"bar\":1,\"beat\":0,\"beats\":0.25,\"pitch\":60,\"velocity\":127},{\"bar\":1,\"beat\":0.25,\"beats\":0.25,\"pitch\":64,\"velocity\":127},{\"bar\":1,\"beat\":0.5,\"beats\":0.25,\"pitch\":67,\"velocity\":127}]}";
        IReadOnlyList<RenderOutput> result = await RenderWordsAsync("hello robot tonight", notes, "fit", 44_100, samplesPerBar: 44_100);

        Assert.Contains("fit-accelerated", result[0].MetadataJson);
        Assert.Contains("duration-cropped", result[0].MetadataJson);
    }

    [Fact]
    public async Task Words_mode_needs_notes_for_a_text_that_has_words()
    {
        await Assert.ThrowsAsync<AmpleException>(() => RenderWordsAsync("hello", "{\"notes\":[]}", "natural", 10_000));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task An_empty_text_line_is_refused_with_a_message_that_says_it_must_not_be_empty(string text)
    {
        foreach (string mode in new[] { "words", "text" })
        {
            AmpleException error = await Assert.ThrowsAsync<AmpleException>(() => RenderBlockAsync(mode, ("text", text)));
            Assert.Equal("AMP5351", error.DiagnosticCode);
            Assert.Contains("must not be empty", error.Message, StringComparison.Ordinal);
        }

        AmpleException phonemes = await Assert.ThrowsAsync<AmpleException>(() => RenderBlockAsync("text", ("phonemes", text)));
        Assert.Contains("must not be empty", phonemes.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("... , ! ?")]
    [InlineData("-")]
    public async Task A_text_that_has_no_words_in_it_is_silence_in_words_mode_with_or_without_notes(string text)
    {
        string notes = NoteJson([(0, 1, 60)]);
        foreach (string input in new[] { notes, "{\"notes\":[]}" })
        {
            short[] samples = Wav.Decode((await RenderWordsAsync(text, input, "natural", 10_000))[0].Data).Samples;
            Assert.All(samples, sample => Assert.Equal(0, sample));
            Assert.NotEmpty(samples);
        }
    }

    private async Task<short[]> RenderBlockAsync(string mode, params (string Name, string Value)[] parameters)
    {
        BlockNode block = new() { Type = "speech", Name = "voice" };
        block.Parameters.Add(new Parameter("mode", mode, 0));
        foreach ((string name, string value) in parameters) block.Parameters.Add(new Parameter(name, value, 0));
        RenderRequest request = new(block, EngineRig.Spec.Templates["speech"], 10_000, 40_000, 1, 1, new HashSet<int> { 1 },
            mode == "words" ? new Dictionary<string, byte[]> { ["notes"] = Encoding.UTF8.GetBytes(NoteJson([(0, 1, 60)])) } : new Dictionary<string, byte[]>(), 17, folder);
        IReadOnlyList<RenderOutput> outputs = await BuiltInRenderers.All.Single(renderer => renderer.BlockType == "speech").RenderAsync(request, CancellationToken.None);
        return Wav.Decode(outputs[0].Data).Samples;
    }

    private async Task<(short[] Samples, string Metadata)> RenderTextWithGainAsync(string gain)
    {
        BlockNode block = new() { Type = "speech", Name = "voice" };
        block.Parameters.Add(new Parameter("text", "Get down get funky get up get loud", 0));
        if (gain != "0") block.Parameters.Add(new Parameter("gain", gain, 0));
        RenderRequest request = new(block, EngineRig.Spec.Templates["speech"], 10_000, 40_000, 1, 4, new HashSet<int> { 1, 2, 3, 4 },
            new Dictionary<string, byte[]>(), 17, folder);
        RenderOutput output = (await BuiltInRenderers.All.Single(renderer => renderer.BlockType == "speech").RenderAsync(request, CancellationToken.None)).Single();
        return (Wav.Decode(output.Data).Samples, output.MetadataJson!);
    }

    [Fact]
    public async Task Gain_that_saturates_is_reported_with_the_number_of_cut_samples_and_is_never_silent()
    {
        (short[] plain, string plainMeta) = await RenderTextWithGainAsync("0");
        (short[] loud, string loudMeta) = await RenderTextWithGainAsync("12");

        int peak = plain.Max(sample => Math.Abs((int)sample));
        Assert.DoesNotContain("clipped", plainMeta, StringComparison.Ordinal);
        Assert.InRange(peak, 1_000, 32_000);   // a normal voice leaves headroom at 0 dB
        int cut = loud.Count(sample => sample is short.MinValue or short.MaxValue);
        bool shouldClip = peak * Math.Pow(10, 12 / 20d) > short.MaxValue;
        Assert.Equal(shouldClip, loudMeta.Contains("gain-clipped", StringComparison.Ordinal));
        Assert.Equal(shouldClip, cut > 0);
    }

    [Fact]
    public async Task Text_mode_needs_text_or_phonemes_never_both_and_a_text_without_words_is_silence()
    {
        Assert.All(await RenderBlockAsync("text", ("text", "...")), sample => Assert.Equal(0, sample));
        await Assert.ThrowsAsync<AmpleException>(() => RenderBlockAsync("text"));
        // both is an error, also when one of them is empty (the empty one is refused first, with its own message)
        await Assert.ThrowsAsync<AmpleException>(() => RenderBlockAsync("text", ("text", "hello"), ("phonemes", "HH AH0 L OW1")));
        await Assert.ThrowsAsync<AmpleException>(() => RenderBlockAsync("text", ("text", "hello"), ("phonemes", "")));
        Assert.Contains(await RenderBlockAsync("text", ("phonemes", "HH AH0 L OW1")), sample => sample != 0);
    }

    [Fact]
    public async Task Words_mode_without_a_text_parameter_is_an_error_and_phonemes_are_not_allowed()
    {
        await Assert.ThrowsAsync<AmpleException>(() => RenderBlockAsync("words"));
        await Assert.ThrowsAsync<AmpleException>(() => RenderBlockAsync("words", ("text", "hello"), ("phonemes", "HH AH0 L OW1")));
        Assert.Contains(await RenderBlockAsync("words", ("text", "hello")), sample => sample != 0);
    }

    [Fact]
    public async Task Text_mode_rejects_a_words_only_duration_policy()
    {
        BlockNode block = new() { Type = "speech", Name = "voice" };
        block.Parameters.Add(new Parameter("mode", "text", 0));
        block.Parameters.Add(new Parameter("text", "hello", 0));
        block.Parameters.Add(new Parameter("duration_policy", "gate", 0));
        RenderRequest request = new(block, EngineRig.Spec.Templates["speech"], 10_000, 40_000, 1, 1,
            new HashSet<int> { 1 }, new Dictionary<string, byte[]>(), 17, folder);

        await Assert.ThrowsAsync<AmpleException>(async () => await BuiltInRenderers.All.Single(renderer => renderer.BlockType == "speech")
            .RenderAsync(request, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Fit_max_rate_is_validated_and_changes_fit_output()
    {
        const string notes = "{\"notes\":[{\"bar\":1,\"beat\":0,\"beats\":0.25,\"pitch\":60,\"velocity\":127}]}";
        BlockNode block = new() { Type = "speech", Name = "voice" };
        block.Parameters.Add(new Parameter("mode", "words", 0));
        block.Parameters.Add(new Parameter("text", "hello", 0));
        block.Parameters.Add(new Parameter("duration_policy", "fit", 0));
        block.Parameters.Add(new Parameter("fit_max_rate", "0.5", 0));
        RenderRequest request = new(block, EngineRig.Spec.Templates["speech"], 10_000, 40_000, 1, 1,
            new HashSet<int> { 1 }, new Dictionary<string, byte[]> { ["notes"] = Encoding.UTF8.GetBytes(notes) }, 17, folder);

        await Assert.ThrowsAsync<AmpleException>(() => BuiltInRenderers.All.Single(renderer => renderer.BlockType == "speech")
            .RenderAsync(request, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Text_mode_remains_available_without_notes()
    {
        BlockNode block = new() { Type = "speech", Name = "voice" };
        block.Parameters.Add(new Parameter("mode", "text", 0));
        block.Parameters.Add(new Parameter("text", "hello robot", 0));
        RenderRequest request = new(block, EngineRig.Spec.Templates["speech"], 10_000, 40_000, 1, 2,
            new HashSet<int> { 1, 2 }, new Dictionary<string, byte[]>(), 17, folder);
        IReadOnlyList<RenderOutput> result = await BuiltInRenderers.All.Single(renderer => renderer.BlockType == "speech").RenderAsync(request, CancellationToken.None);

        Assert.Contains(result[0].Data, value => value != 0);
        Assert.Contains("\"mode\":\"text\"", result[0].MetadataJson);
    }

    [Fact]
    public async Task Text_mode_accepts_explicit_phonemes_and_rejects_both_inputs()
    {
        BlockNode phonemeBlock = new() { Type = "speech", Name = "voice" };
        phonemeBlock.Parameters.Add(new Parameter("phonemes", "HH AH0 L OW1", 0));
        RenderRequest phonemeRequest = new(phonemeBlock, EngineRig.Spec.Templates["speech"], 10_000, 40_000, 1, 2,
            new HashSet<int> { 1, 2 }, new Dictionary<string, byte[]>(), 17, folder);
        IReadOnlyList<RenderOutput> result = await BuiltInRenderers.All.Single(renderer => renderer.BlockType == "speech")
            .RenderAsync(phonemeRequest, CancellationToken.None);
        Assert.Contains(result[0].Data, value => value != 0);

        phonemeBlock.Parameters.Add(new Parameter("text", "hello", 0));
        await Assert.ThrowsAsync<AmpleException>(() => BuiltInRenderers.All.Single(renderer => renderer.BlockType == "speech")
            .RenderAsync(phonemeRequest, CancellationToken.None).AsTask());
    }
    [Fact]
    public async Task The_lexicon_of_a_flite_block_gives_a_word_its_own_phones_and_is_part_of_the_take()
    {
        IReadOnlyList<RenderOutput> bare = await RenderTextBlockAsync("flite");
        IReadOnlyList<RenderOutput> own = await RenderTextBlockAsync("flite", ("lexicon", "\"hello = B AY1; monkeys = M AA1 N K IY0 Z\""));
        IReadOnlyList<RenderOutput> again = await RenderTextBlockAsync("flite", ("lexicon", "\"monkeys = M AA1 N K IY0 Z;hello = B AY1\""));

        Assert.NotEqual(bare[0].Data, own[0].Data);
        Assert.Equal(own[0].Data, again[0].Data);   // the order of the entries does not matter
        Assert.Contains("\"userLexicon\":\"lex[", own[0].MetadataJson, StringComparison.Ordinal);
        Assert.Contains("\"userLexicon\":null", bare[0].MetadataJson, StringComparison.Ordinal);
        Assert.Equal(WordPlaces(bare[0]).Length, WordPlaces(own[0]).Length);   // the words keep their places in the text: only the sound of a word changes
    }

    [Fact]
    public async Task Spell_reads_the_words_of_a_flite_block_letter_by_letter()
    {
        IReadOnlyList<RenderOutput> bare = await RenderTextBlockAsync("flite");
        IReadOnlyList<RenderOutput> spelled = await RenderTextBlockAsync("flite", ("spell", "\"world\""));
        IReadOnlyList<RenderOutput> all = await RenderTextBlockAsync("flite", ("spell", "\"all\""));

        Assert.True(WordPlaces(spelled[0]).Length > WordPlaces(bare[0]).Length);   // world is five words (letters)
        Assert.True(WordPlaces(all[0]).Length > WordPlaces(spelled[0]).Length);
        Assert.Contains("spell[all]", all[0].MetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_lexicon_works_in_words_mode_and_the_note_still_sets_the_pitch()
    {
        string notes = NoteJson([(0, 1, 60), (1, 1, 64)]);
        short[] plain = Wav.Decode((await RenderWordsAsync("tomato sing", notes, "natural", 22_050, "flite", samplesPerBar: 44_100)).Single().Data).Samples;
        IReadOnlyList<RenderOutput> withLexicon = await RenderWordsAsync("tomato sing", notes, "natural", 22_050, "flite", samplesPerBar: 44_100, extra: ("lexicon", "\"tomato = T AH0 M AA1 T OW0\""));
        short[] own = Wav.Decode(withLexicon.Single().Data).Samples;

        Assert.NotEqual(plain, own);
    }

    [Theory]
    [InlineData("humanico", "lexicon", "\"a = EY1\"", "belong to the flite engine")]
    [InlineData("retro", "spell", "\"all\"", "belong to the flite engine")]
    [InlineData("flite", "lexicon", "\"tomato T AH0\"", "word = PHONEMES")]
    [InlineData("flite", "lexicon", "\"tomato = T AH0 XX1\"", "Unknown phoneme")]
    [InlineData("flite", "lexicon", "\"tomato = T AH M EY1\"", "stress digit")]
    [InlineData("flite", "spell", "\"two words, 12\"", "not a word")]
    public async Task The_lexicon_and_spell_are_checked(string engine, string name, string value, string message)
    {
        AmpleException error = await Assert.ThrowsAsync<AmpleException>(async () => await RenderTextBlockAsync(engine, (name, value)));

        Assert.Equal("AMP5351", error.DiagnosticCode);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_tags_of_a_flite_text_shape_the_words_and_are_not_spoken()
    {
        IReadOnlyList<RenderOutput> bare = await RenderTextBlockAsync("flite", ("text", "\"Hello world, 12 monkeys.\""));
        IReadOnlyList<RenderOutput> tagged = await RenderTextBlockAsync("flite", ("text", "\"Hello <pitch +4st>world</pitch>, <break 300ms/>12 <rate 2>monkeys</rate>.\""));

        Assert.NotEqual(bare[0].Data, tagged[0].Data);
        int[][] before = WordPlaces(bare[0]), after = WordPlaces(tagged[0]);
        Assert.Equal(before.Length, after.Length);   // the tags add no word
        Assert.True(after[0][0] <= after[1][0]);
    }

    [Theory]
    [InlineData("humanico", "\"Hello <emph>world</emph>.\"", "belong to the flite engine")]
    [InlineData("retro", "\"Hello <break 300ms/> world.\"", "belong to the flite engine")]
    [InlineData("flite", "\"Hello <emph>world.\"", "not closed")]
    [InlineData("flite", "\"Hello <pitch 900>world</pitch>.\"", "50 to 480")]
    [InlineData("flite", "\"Hello <break 300/> world.\"", "needs a unit")]
    public async Task The_tags_are_checked_and_belong_to_the_flite_engine(string engine, string text, string message)
    {
        AmpleException error = await Assert.ThrowsAsync<AmpleException>(async () => await RenderTextBlockAsync(engine, ("text", text)));

        Assert.Equal("AMP5351", error.DiagnosticCode);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tags_are_refused_in_words_mode_where_every_word_has_its_note()
    {
        AmpleException error = await Assert.ThrowsAsync<AmpleException>(async () =>
            await RenderWordsAsync("\"one <emph>two</emph>\"", NoteJson([(0, 1, 60), (1, 1, 64)]), "natural", 22_050, "flite", samplesPerBar: 44_100));

        Assert.Equal("AMP5351", error.DiagnosticCode);
        Assert.Contains("text mode", error.Message, StringComparison.Ordinal);
    }

}
