namespace Ample.Speech.Tests;

public sealed class SpeechEngineTests
{
    [Fact]
    public void Calibrated_pcm_uses_fixed_scale_and_reports_clipping()
    {
        HumanicoPcmResult result = HumanicoCalibratedVoice.ToPcm([0.5, -0.5, 8, -8], CancellationToken.None);

        Assert.Equal([8192, -8192, short.MaxValue, short.MinValue], result.Samples);
        Assert.Equal(2, result.ClippedSamples);
        Assert.Throws<InvalidOperationException>(() => HumanicoCalibratedVoice.ToPcm([double.NaN], CancellationToken.None));
    }

    [Fact]
    public void Calibrated_phone_and_word_paths_share_planning_and_audio()
    {
        IReadOnlyList<SpeechPhone> phones = HumanicoFrontEnd.WordToPhones("hello");
        int frames = HumanicoCalibratedVoice.PlanPhones(phones, 1, 1000, CancellationToken.None).Count;
        double[] fromPhones = HumanicoCalibratedVoice.RenderPhones(phones, 105, 1, 17, 1000, CancellationToken.None);
        double[] fromWord = HumanicoCalibratedVoice.RenderWord("hello", 105, 1, 17, 1000, CancellationToken.None);

        Assert.Equal(frames * 50, fromPhones.Length);
        Assert.Equal(fromWord, fromPhones);
    }

    [Fact]
    public void Stable_humanico_transitions_keep_omitted_filter_bandwidths_positive()
    {
        HumanicoTargetSegment[] segments =
        [
            new(new("HH", "fric", 70, 20), new Dictionary<string, double> { ["ah"] = 0.5 }),
            new(new("AH", "vowel", 85, 50), new Dictionary<string, double> { ["ah"] = 0, ["b1"] = 80 })
        ];
        IReadOnlyList<IReadOnlyDictionary<string, double>> frames =
            HumanicoTargetFrames.PlanStable(segments, 1, 100, CancellationToken.None);

        Assert.Equal(60, frames[13]["b1"]);
        Assert.InRange(frames[14]["b1"], 60, 80);
        Assert.InRange(frames[14]["ah"], 0, 0.5);
        Assert.All(frames, frame => Assert.True(frame["b1"] > 0));
    }

    [Fact]
    public void Calibrated_word_pipeline_matches_pinned_python_with_identical_excitation()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "humanico-word.json");
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        System.Text.Json.JsonElement reference = document.RootElement;
        IReadOnlyList<SpeechPhone> phones = HumanicoFrontEnd.WordToPhones(reference.GetProperty("word").GetString()!);
        string calibrationPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "humanico-calibration.json");
        using System.Text.Json.JsonDocument calibration = System.Text.Json.JsonDocument.Parse(File.ReadAllText(calibrationPath));
        System.Text.Json.JsonElement[] cases = [.. calibration.RootElement.GetProperty("cases").EnumerateArray()];
        List<HumanicoTargetSegment> segments = [];
        for (int i = 0; i < phones.Count; i++)
        {
            SpeechPhone phone = phones[i];
            string following = i + 1 < phones.Count && HumanicoFrontEnd.IsVowel(phones[i + 1].Symbol)
                ? phones[i + 1].Symbol : string.Empty;
            System.Text.Json.JsonElement[] candidates = [.. cases.Where(item => item.GetProperty("phone").GetString() == phone.Symbol)];
            System.Text.Json.JsonElement selected = candidates.FirstOrDefault(item =>
                item.TryGetProperty("following", out var next) && next.GetString() == following);
            if (selected.ValueKind == System.Text.Json.JsonValueKind.Undefined)
                selected = candidates.Single(item => !item.TryGetProperty("following", out _));
            System.Text.Json.JsonElement[] targets = [.. selected.GetProperty("segments").EnumerateArray()];
            IReadOnlyList<SpeechSegment> timings = HumanicoReferenceSegments.Plan([phone], CancellationToken.None);
            for (int j = 0; j < timings.Count; j++)
                segments.Add(new(timings[j], targets[j].GetProperty("target").EnumerateObject()
                    .ToDictionary(property => property.Name, property => property.Value.GetDouble())));
        }
        IReadOnlyList<IReadOnlyDictionary<string, double>> frames = HumanicoTargetFrames.Plan(segments, 1, 1000, CancellationToken.None);
        Assert.Equal(reference.GetProperty("frames").GetInt32(), frames.Count);
        double[] noise = Enumerable.Repeat(reference.GetProperty("noise").GetDouble(), frames.Count * 50).ToArray();
        double[] pitches = Enumerable.Repeat(reference.GetProperty("pitch").GetDouble(), frames.Count).ToArray();
        double[] samples = HumanicoTargetRenderer.Render(frames, pitches, noise, 100_000, CancellationToken.None);
        foreach (System.Text.Json.JsonElement sample in reference.GetProperty("samples").EnumerateArray())
            Assert.InRange(Math.Abs(sample.GetProperty("value").GetDouble() - samples[sample.GetProperty("index").GetInt32()]), 0, 1e-10);
    }

    [Fact]
    public void Calibrated_voice_renders_real_words_with_deterministic_seeded_audio()
    {
        double[] hello = HumanicoCalibratedVoice.RenderWord("hello", 105, 1, 17, 1000, CancellationToken.None);
        double[] repeated = HumanicoCalibratedVoice.RenderWord("hello", 105, 1, 17, 1000, CancellationToken.None);
        double[] robot = HumanicoCalibratedVoice.RenderWord("robot", 105, 1, 17, 1000, CancellationToken.None);

        Assert.Equal(hello, repeated);
        Assert.NotEqual(hello, robot);
        Assert.Contains(hello, sample => sample != 0);
        Assert.All(hello, sample => Assert.True(double.IsFinite(sample)));
        Assert.Throws<SpeechResourceLimitException>(() =>
            HumanicoCalibratedVoice.RenderWord("hello", 105, 1, 17, 1, CancellationToken.None));
    }

    [Fact]
    public void Embedded_humanico_calibration_uses_following_vowel_and_preserves_stress_timing()
    {
        SpeechPhone[] aa = [new("P", -1), new("AA", 1)];
        SpeechPhone[] iy = [new("P", -1), new("IY", 0)];
        IReadOnlyList<HumanicoTargetSegment> first = HumanicoCalibratedSegments.Plan(aa, 10, CancellationToken.None);
        IReadOnlyList<HumanicoTargetSegment> second = HumanicoCalibratedSegments.Plan(iy, 10, CancellationToken.None);

        Assert.Equal("aspir", first[2].Segment.Kind);
        Assert.Equal(730, first[2].Target["f1"]);
        Assert.Equal(270, second[2].Target["f1"]);
        Assert.Equal(150, first[3].Segment.DurationMilliseconds);
        Assert.Equal(85, second[3].Segment.DurationMilliseconds);
        Assert.Throws<SpeechResourceLimitException>(() => HumanicoCalibratedSegments.Plan(aa, 3, CancellationToken.None));
    }

    [Fact]
    public void Embedded_hh_calibration_uses_aspiration_level_not_unbounded_frication()
    {
        SpeechPhone[] phones = [new("HH", -1), new("AH", 0)];
        HumanicoTargetSegment hh = HumanicoCalibratedSegments.Plan(phones, 10, CancellationToken.None)[0];

        Assert.Equal(0, hh.Target["af"]);
        Assert.InRange(hh.Target["ah"], 0.0097, 0.0099);
    }

    [Fact]
    public void Embedded_humanico_calibration_matches_its_recorded_hash()
    {
        using Stream stream = typeof(HumanicoCalibratedSegments).Assembly.GetManifestResourceStream(
            "Ample.Speech.Data.humanico-calibration.json")!;
        Assert.Equal(HumanicoCalibratedSegments.CalibrationSha256,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)));
    }

    [Fact]
    public void Upstream_calibration_captures_following_vowel_in_aspiration_targets()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "humanico-calibration.json");
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        System.Text.Json.JsonElement[] cases = [.. document.RootElement.GetProperty("cases").EnumerateArray()];
        System.Text.Json.JsonElement Aspir(string following) => cases.Single(item =>
            item.GetProperty("phone").GetString() == "P" && item.TryGetProperty("following", out var value)
            && value.GetString() == following).GetProperty("segments")[2].GetProperty("target");

        Assert.Equal(730, Aspir("AA").GetProperty("f1").GetDouble());
        Assert.Equal(270, Aspir("IY").GetProperty("f1").GetDouble());
    }

    [Fact]
    public void Humanico_target_renderer_matches_python_across_frames_and_keeps_absent_parameters()
    {
        IReadOnlyDictionary<string, double>[] frames =
        [
            new Dictionary<string, double> { ["av"] = 0.8 },
            new Dictionary<string, double>
            {
                ["fnz"] = 1900, ["bnz"] = 250, ["f1"] = 270, ["b1"] = 250,
                ["f2"] = 1700, ["af"] = 0.6, ["a5"] = 0.45, ["a6"] = 0.75
            }
        ];
        double[] expected = [0.601073936072864, -0.983756067403740, 1.187756358653780,
            -1.253054355580383, 1.219117010178688, -1.113222293083276, 0.977751182399026,
            -0.813665757400263];
        double[] noise = Enumerable.Repeat(0.25, 100).ToArray();
        double[] actual = HumanicoTargetRenderer.Render(frames, [120, 180], noise, 100, CancellationToken.None);

        for (int i = 0; i < expected.Length; i++)
            Assert.InRange(Math.Abs(expected[i] - actual[50 + i]), 0, 1e-12);
        Assert.Throws<SpeechResourceLimitException>(() =>
            HumanicoTargetRenderer.Render(frames, [120, 180], noise, 99, CancellationToken.None));
    }

    [Fact]
    public void Humanico_target_updates_keep_omitted_voice_parameters()
    {
        HumanicoKlattParameters first = HumanicoTargetAdapter.Create(
            new Dictionary<string, double> { ["f1"] = 730, ["b1"] = 85, ["av"] = 0.9 }, 110);
        HumanicoKlattParameters second = HumanicoTargetAdapter.Apply(first,
            new Dictionary<string, double> { ["af"] = 0.5, ["av"] = 0 }, 120);

        Assert.Equal(730, second.Frequencies[0]);
        Assert.Equal(85, second.CascadeBandwidths[0]);
        Assert.Equal(0, second.Voicing);
        Assert.Equal(0.5, second.Frication);
        Assert.Equal(120, second.F0);
    }

    [Fact]
    public void Humanico_target_frames_interpolate_new_and_disappearing_parameters()
    {
        HumanicoTargetSegment[] segments =
        [
            new(new("A", "vowel", 10, 0), new Dictionary<string, double> { ["av"] = 1, ["f1"] = 500 }),
            new(new("S", "fric", 20, 10), new Dictionary<string, double> { ["af"] = 0.9, ["f1"] = 800 })
        ];
        IReadOnlyList<IReadOnlyDictionary<string, double>> frames =
            HumanicoTargetFrames.Plan(segments, 1, 10, CancellationToken.None);

        Assert.Equal(6, frames.Count);
        Assert.Equal(2d / 3, frames[2]["av"], 12);
        Assert.Equal(0.3, frames[2]["af"], 12);
        Assert.Equal(600, frames[2]["f1"], 12);
        Assert.Equal(0.6, frames[3]["af"], 12);
        Assert.False(frames[4].ContainsKey("av"));
        Assert.Throws<SpeechResourceLimitException>(() => HumanicoTargetFrames.Plan(segments, 1, 5, CancellationToken.None));
    }

    [Fact]
    public void Reference_calibrated_targets_drive_the_full_voice_without_nonfinite_output()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "humanico-calibration.json");
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        foreach (System.Text.Json.JsonElement item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            HumanicoKlattVoice voice = new();
            foreach (System.Text.Json.JsonElement segment in item.GetProperty("segments").EnumerateArray())
            {
                Dictionary<string, double> target = segment.GetProperty("target").EnumerateObject()
                    .ToDictionary(property => property.Name, property => property.Value.GetDouble());
                HumanicoKlattParameters parameters = HumanicoTargetAdapter.Create(target, 110);
                double[] output = voice.RenderFrame(parameters, Enumerable.Repeat(0.25, 50).ToArray(), CancellationToken.None);
                Assert.All(output, value => Assert.True(double.IsFinite(value)));
            }
        }
    }

    [Fact]
    public void Reference_segment_timings_match_pinned_python_for_all_supported_phones()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "humanico-segments.json");
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("ce793bc9304cf34637199b0efde72c8afe130b1d",
            document.RootElement.GetProperty("upstreamRevision").GetString());
        foreach (System.Text.Json.JsonElement item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            SpeechPhone phone = new(item.GetProperty("phone").GetString()!, item.GetProperty("stress").GetInt32());
            IReadOnlyList<SpeechSegment> actual = HumanicoReferenceSegments.Plan([phone], CancellationToken.None);
            System.Text.Json.JsonElement[] expected = [.. item.GetProperty("segments").EnumerateArray()];
            Assert.Equal(expected.Length, actual.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i].GetProperty("kind").GetString(), actual[i].Kind);
                Assert.Equal(expected[i].GetProperty("duration").GetInt32(), actual[i].DurationMilliseconds);
                Assert.Equal(expected[i].GetProperty("transition").GetInt32(), actual[i].TransitionMilliseconds);
            }
        }
    }

    [Fact]
    public void Pinned_humanico_segment_rules_expand_stops_and_affricates()
    {
        SpeechPhone[] phones = [new("P", -1), new("CH", -1), new("AY", 1), new("N", -1)];
        IReadOnlyList<SpeechSegment> segments = HumanicoReferenceSegments.Plan(phones, CancellationToken.None);

        Assert.Equal(["sil", "burst", "aspir", "sil", "burst", "fric", "vowel", "vowel", "nasal"],
            segments.Select(segment => segment.Kind));
        Assert.Equal([70, 14, 35, 45, 14, 110, 74, 90, 90],
            segments.Select(segment => segment.DurationMilliseconds));
        Assert.Equal([3, 3, 12, 3, 3, 20, 55, 82, 35],
            segments.Select(segment => segment.TransitionMilliseconds));
    }

    [Fact]
    public void Humanico_voice_matches_pinned_python_after_a_nasal_parameter_change()
    {
        HumanicoKlattVoice voice = new();
        double[] noise = Enumerable.Repeat(0.25, 50).ToArray();
        voice.RenderFrame(new() { F0 = 120, Voicing = 0.8 }, noise, CancellationToken.None);
        HumanicoKlattParameters nasal = new()
        {
            F0 = 180,
            Voicing = 0.8,
            NasalZero = 1900,
            NasalZeroBandwidth = 250,
            Frequencies = [270, 1700, 2500, 3900, 4500, 4800],
            CascadeBandwidths = [250, 90, 150, 250, 300],
            Frication = 0.6,
            ParallelAmplitudes = [0, 0, 0, 0.45, 0.75]
        };
        double[] expected = [0.601073936072864, -0.983756067403740, 1.187756358653780,
            -1.253054355580383, 1.219117010178688, -1.113222293083276, 0.977751182399026,
            -0.813665757400263];
        double[] result = voice.RenderFrame(nasal, noise, CancellationToken.None);

        for (int i = 0; i < expected.Length; i++)
            Assert.InRange(Math.Abs(expected[i] - result[i]), 0, 1e-12);
    }

    [Fact]
    public void Humanico_voice_matches_pinned_python_with_identical_noise()
    {
        HumanicoKlattParameters parameters = new()
        {
            F0 = 120,
            Voicing = 1,
            Aspiration = 0.2,
            Frication = 0.3,
            Bypass = 0.4,
            ParallelAmplitudes = [0.1, 0.2, 0.3, 0.4, 0.5]
        };
        double[] expected = [0.445498272807271, -0.471105817248866, 0.534707910180645,
            -0.524361087981094, 0.449699001942810, -0.347524932593753, 0.260439365334827,
            -0.229571043143837];
        double[] actual = new HumanicoKlattVoice().RenderFrame(parameters, Enumerable.Repeat(0.25, 8).ToArray(),
            CancellationToken.None);

        for (int i = 0; i < expected.Length; i++)
            Assert.InRange(Math.Abs(expected[i] - actual[i]), 0, 1e-12);
    }

    [Fact]
    public void Humanico_voice_preserves_state_across_frame_boundaries()
    {
        HumanicoKlattParameters parameters = new()
        {
            F0 = 120,
            Voicing = 1,
            Aspiration = 0.2,
            Frication = 0.3,
            Bypass = 0.4,
            ParallelAmplitudes = [0.1, 0.2, 0.3, 0.4, 0.5]
        };
        double[] noise = Enumerable.Range(0, 100).Select(i => (i % 7 - 3) / 3d).ToArray();
        double[] whole = new HumanicoKlattVoice().RenderFrame(parameters, noise, CancellationToken.None);
        HumanicoKlattVoice splitVoice = new();
        double[] split = [.. splitVoice.RenderFrame(parameters, noise.AsSpan(0, 50), CancellationToken.None),
            .. splitVoice.RenderFrame(parameters, noise.AsSpan(50), CancellationToken.None)];

        Assert.Equal(whole, split);
        Assert.Contains(whole, sample => sample != 0);
    }

    [Fact]
    public void Humanico_voice_silence_and_precancelled_frames_do_not_generate_audio()
    {
        HumanicoKlattVoice voice = new();
        Assert.All(voice.RenderFrame(new(), new double[50], CancellationToken.None), sample => Assert.Equal(0, sample));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => voice.RenderFrame(new(), new double[50], cancellation.Token));
    }

    [Fact]
    public void Nasal_antiresonator_cancels_matching_resonator_with_continuous_state()
    {
        HumanicoResonatorCoefficients coefficients = HumanicoResonatorCoefficients.Create(270, 270, 10_000);
        HumanicoKlattResonator pole = new();
        HumanicoKlattAntiresonator zero = new();
        double[] input = [1, 0, 0, 0.25, -0.5, 0, 0, 0];

        foreach (double sample in input)
            Assert.InRange(Math.Abs(zero.Process(pole.Process(sample, coefficients), coefficients) - sample), 0, 1e-12);
    }

    [Fact]
    public void Null_text_is_rejected_as_an_argument_error()
    {
        SpeechRequest request = new(null!);

        Assert.Throws<ArgumentNullException>(() => HumanicoSpeechEngine.Render(request));
    }

    [Fact]
    public void Frame_planning_honors_cancellation_before_work()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => HumanicoFramePlanner.Plan(
            HumanicoSegmentPlanner.PlanWord("electricity"), 105, 0.1, cancellation.Token));
    }

    [Fact]
    public void Segment_planning_honors_cancellation_before_work()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => HumanicoSegmentPlanner.PlanPhones(
            HumanicoFrontEnd.WordToPhones("electricity"), cancellation.Token));
    }

    [Fact]
    public void Render_is_deterministic_and_returns_word_timing()
    {
        SpeechRequest request = new("hello robot", Seed: 17) { Engine = "retro" };
        SpeechResult first = HumanicoSpeechEngine.Render(request);
        SpeechResult second = HumanicoSpeechEngine.Render(request);

        Assert.Equal(first.Samples, second.Samples);
        Assert.Equal(first.Words, second.Words);
        Assert.Equal(2, first.Words.Count);
        Assert.Equal("retro/formant-2", first.EngineIdentity);
    }

    [Fact]
    public void Natural_duration_plan_uses_phone_frames_instead_of_fixed_word_slots()
    {
        SpeechResult result = HumanicoSpeechEngine.Render(new("hello robot") { Engine = "retro" });

        Assert.Equal(2, result.Words.Count);
        Assert.Equal(4_050, result.Words[0].EndSample);
        Assert.Equal(result.Words[0].EndSample, result.Words[1].StartSample);
        Assert.True(result.Samples.Length < 16_000);
    }

    [Fact]
    public void Shared_word_planner_is_the_single_frame_duration_seam()
    {
        Assert.Equal(
            SpeechPlanner.PlanWord("hello", 105, 1).Select(frame => frame.Symbol),
            SpeechPlanner.PlanPhones(HumanicoFrontEnd.WordToPhones("hello"), 105, 1).Select(frame => frame.Symbol));
    }

    [Fact]
    public void Humanico_candidate_is_explicit_and_distinct_from_retro()
    {
        SpeechResult retro = HumanicoSpeechEngine.Render(new("hello robot", Seed: 17) { Engine = "retro" });
        SpeechResult humanico = HumanicoSpeechEngine.Render(new SpeechRequest("hello robot", Seed: 17) { Engine = "humanico" });

        Assert.Equal("retro/formant-2", retro.EngineIdentity);
        Assert.Equal("humanico/klatt-candidate-3", humanico.EngineIdentity);
        Assert.NotEqual(retro.Samples, humanico.Samples);
        Assert.Equal(humanico.Samples, HumanicoSpeechEngine.Render(new SpeechRequest("hello robot", Seed: 17) { Engine = "humanico" }).Samples);
    }

    [Fact]
    public void Empty_humanico_render_keeps_candidate_identity()
    {
        SpeechResult result = HumanicoSpeechEngine.Render(new SpeechRequest("...?!") { Engine = "humanico" });

        Assert.Empty(result.Samples);
        Assert.Equal("humanico/klatt-candidate-3", result.EngineIdentity);
    }

    [Fact]
    public void Explicit_phonemes_render_without_text_fallback()
    {
        SpeechResult result = HumanicoSpeechEngine.Render(new SpeechRequest(string.Empty) { Engine = "retro", Phonemes = "HH AH0 L OW1" });

        Assert.NotEmpty(result.Samples);
        Assert.Equal("[phonemes]", result.Words.Single().SourceWord);
    }

    [Fact]
    public void Invalid_explicit_phonemes_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => HumanicoSpeechEngine.Render(new SpeechRequest(string.Empty) { Engine = "retro", Phonemes = "HH BAD1" }));
    }

    [Fact]
    public void Unknown_engine_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HumanicoSpeechEngine.Render(new SpeechRequest("hello") { Engine = "future" }));
    }

    [Fact]
    public void Already_cancelled_request_stops_before_rendering()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            HumanicoSpeechEngine.Render(new SpeechRequest("one two three") { Engine = "retro" }, cancellation.Token));
    }

    [Fact]
    public void Cancellation_stops_during_synthesis_without_publishing_output()
    {
        using CancellationTokenSource cancellation = new();
        SpeechResult? result = null;
        string text = string.Join(' ', Enumerable.Repeat("word", 200));
        Assert.Throws<OperationCanceledException>(() => result = HumanicoSpeechEngine.Render(new SpeechRequest(text, Progress: progress =>
        {
            if (progress.Stage == "synthesis" && progress.Completed > 0) cancellation.Cancel();
        })
        { Engine = "retro" }, cancellation.Token));
        Assert.Null(result);
    }

    [Theory]
    [InlineData(10_000, 4_050)]
    [InlineData(44_100, 17_861)]
    [InlineData(48_000, 19_440)]
    public void Internal_frame_clock_resamples_without_drift(int sampleRate, int expectedSamples)
    {
        SpeechResult result = HumanicoSpeechEngine.Render(new("hello", sampleRate) { Engine = "retro" });

        Assert.Equal(expectedSamples, result.Samples.Length);
        Assert.Equal(expectedSamples, result.Words[0].EndSample);
    }

    [Fact]
    public void Full_seed_influences_scaffold_noise()
    {
        SpeechResult low = HumanicoSpeechEngine.Render(new("hello", Seed: 17) { Engine = "retro" });
        SpeechResult high = HumanicoSpeechEngine.Render(new("hello", Seed: 4_294_967_313) { Engine = "retro" });

        Assert.NotEqual(low.Samples, high.Samples);
    }

    [Fact]
    public void Word_seed_derivation_uses_full_seed_and_is_index_stable()
    {
        Assert.NotEqual(SpeechSeed.ForWord(17, 0), SpeechSeed.ForWord(4_294_967_313, 0));
        Assert.Equal(SpeechSeed.ForWord(17, 1), SpeechSeed.ForWord(17, 1));
        Assert.NotEqual(SpeechSeed.ForWord(17, 0), SpeechSeed.ForWord(17, 1));
        Assert.Equal("SplitMix64/ample-2", SpeechSeed.AlgorithmIdentity);
    }

    [Fact]
    public void Configured_output_budget_rejects_before_buffer_allocation()
    {
        Assert.Throws<SpeechResourceLimitException>(() =>
            HumanicoSpeechEngine.Render(new("one two", MaxOutputSamples: 100) { Engine = "retro" }));
    }

    [Fact]
    public void Tokenization_normalizes_punctuation_and_expands_digits()
    {
        SpeechResult result = HumanicoSpeechEngine.Render(new("42, robot…") { Engine = "retro" });

        Assert.Equal(["four", "two", "robot"], result.Words.Select(word => word.SourceWord));
    }

    [Fact]
    public void Rate_below_template_minimum_is_rejected_before_planning()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HumanicoSpeechEngine.Render(new("hello", Rate: 0.05) { Engine = "retro" }));
    }

    [Fact]
    public void Configured_work_budget_rejects_internal_buffer_before_synthesis()
    {
        Assert.Throws<SpeechResourceLimitException>(() =>
            HumanicoSpeechEngine.Render(new("one two", MaxWorkingSamples: 100) { Engine = "retro" }));
    }

    [Theory]
    [InlineData("123", "one", "two", "three")]
    [InlineData("١٢٣", "١٢٣")]
    [InlineData("１２３", "１２３")]
    public void Unicode_digit_policy_never_indexes_ascii_expansion_table(string text, params string[] expected)
    {
        SpeechResult result = HumanicoSpeechEngine.Render(new(text));

        Assert.Equal(expected, result.Words.Select(word => word.SourceWord));
    }

    [Fact]
    public void Humanico_front_end_has_deterministic_oov_phone_fallback()
    {
        Assert.Equal(["K", "K", "Z"], HumanicoFrontEnd.WordToPhones("qxz").Select(phone => phone.Symbol));
        Assert.All(HumanicoFrontEnd.WordToPhones("qxz"), phone => Assert.True(phone.Stress is -1 or 0));
    }

    [Fact]
    public void Humanico_segment_planner_preserves_phone_order_and_duration_rules()
    {
        IReadOnlyList<SpeechSegment> segments = HumanicoSegmentPlanner.PlanWord("hello");

        Assert.Equal(["HH", "AH", "L", "AX", "UH"], segments.Select(segment => segment.Symbol));
        Assert.Contains(segments, segment => segment.Kind == "vowel");
        Assert.All(segments, segment => Assert.InRange(segment.DurationMilliseconds, 1, 150));
    }

    [Fact]
    public void Humanico_segment_planner_expands_upstream_diphthongs_and_affricates()
    {
        IReadOnlyList<SpeechSegment> word = HumanicoSegmentPlanner.PlanWord("hello");
        IReadOnlyList<SpeechSegment> phrase = HumanicoSegmentPlanner.PlanWord("jazz");

        Assert.Contains(word, segment => segment.Symbol == "AX");
        Assert.Contains(word, segment => segment.Symbol == "UH");
        Assert.Contains(phrase, segment => segment.Symbol == "D");
        Assert.Contains(phrase, segment => segment.Symbol == "ZH");
    }

    [Fact]
    public void Humanico_frame_planner_uses_fixed_five_millisecond_frames()
    {
        IReadOnlyList<SpeechFrame> frames = HumanicoFramePlanner.Plan(HumanicoSegmentPlanner.PlanWord("hello"), 105, 1);

        Assert.NotEmpty(frames);
        Assert.Equal(Enumerable.Range(0, frames.Count), frames.Select(frame => frame.FrameIndex));
        Assert.Contains(frames, frame => frame.FundamentalHz > 105);
        Assert.Contains(frames, frame => frame.FundamentalHz < 105);
    }

    [Fact]
    public void Humanico_frame_planner_applies_stress_bump_to_the_pitch_contour()
    {
        IReadOnlyList<SpeechFrame> frames = HumanicoFramePlanner.Plan(HumanicoSegmentPlanner.PlanWord("hello"), 105, 1);
        Assert.Contains(frames.Where(frame => frame.Symbol == "AX"), frame => frame.FundamentalHz > 105);
    }

    [Fact]
    public void Humanico_klatt_resonator_matches_the_pinned_coefficient_shape()
    {
        HumanicoResonatorCoefficients coefficients = HumanicoResonatorCoefficients.Create(500, 60, 10_000);
        HumanicoKlattResonator first = new();
        HumanicoKlattResonator second = new();
        double[] impulse = [1, 0, 0, 0, 0];

        double[] firstResponse = impulse.Select(sample => first.Process(sample, coefficients)).ToArray();
        double[] secondResponse = impulse.Select(sample => second.Process(sample, coefficients)).ToArray();

        Assert.Equal(firstResponse, secondResponse);
        Assert.Equal(coefficients.A, firstResponse[0], 12);
        Assert.NotEqual(0, firstResponse[1]);
    }

    [Fact]
    public void Humanico_klatt_cascade_is_finite_and_stateful()
    {
        SpeechFrame frame = HumanicoFramePlanner.Plan(HumanicoSegmentPlanner.PlanWord("hello"), 105, 1)
            .First(item => item.Parameters.Voiced);
        HumanicoKlattCascade cascade = new();
        double[] response = Enumerable.Range(0, 100).Select(_ => cascade.Process(0.25, frame.Parameters)).ToArray();

        Assert.All(response, value => Assert.True(double.IsFinite(value)));
        Assert.Contains(response, value => Math.Abs(value) > 1e-9);
        Assert.NotEqual(response[0], response[^1]);
    }

    [Fact]
    public void Humanico_klatt_source_is_repeatable_and_resets_with_new_state()
    {
        HumanicoKlattSource first = new();
        HumanicoKlattSource second = new();
        double[] firstRun = Enumerable.Range(0, 300).Select(_ => first.Process(105, 1)).ToArray();
        double[] secondRun = Enumerable.Range(0, 300).Select(_ => second.Process(105, 1)).ToArray();

        Assert.Equal(firstRun, secondRun);
        Assert.Contains(firstRun, value => value > 0);
        Assert.True(firstRun.Max() <= 1);
    }

    [Fact]
    public void Humanico_klatt_frame_renderer_is_deterministic_and_bounded()
    {
        IReadOnlyList<SpeechFrame> frames = HumanicoFramePlanner.Plan(HumanicoSegmentPlanner.PlanWord("hello"), 105, 1);
        short[] first = HumanicoKlattFrameRenderer.Render(frames);
        short[] second = HumanicoKlattFrameRenderer.Render(frames);

        Assert.Equal(first, second);
        Assert.Equal(frames.Count * 50, first.Length);
        Assert.All(first, sample => Assert.InRange(sample, short.MinValue, short.MaxValue));
        Assert.Contains(first, sample => sample != 0);
        Assert.InRange(first.Max(sample => Math.Abs((int)sample)), 1, 20_000);
    }

    [Fact]
    public void Humanico_klatt_frame_renderer_can_fit_the_shared_word_slot()
    {
        IReadOnlyList<SpeechFrame> frames = HumanicoFramePlanner.Plan(HumanicoSegmentPlanner.PlanWord("hello"), 105, 1);

        short[] fitted = HumanicoKlattFrameRenderer.Render(frames, 160 * 50);

        Assert.Equal(8_000, fitted.Length);
        Assert.Contains(fitted, sample => sample != 0);
    }

    [Fact]
    public void Humanico_klatt_frame_renderer_keeps_unvoiced_segments_audible_and_seeded()
    {
        IReadOnlyList<SpeechFrame> frames = HumanicoFramePlanner.Plan(HumanicoSegmentPlanner.PlanWord("speech"), 105, 1);

        short[] first = HumanicoKlattFrameRenderer.Render(frames, 8_000, 17, CancellationToken.None);
        short[] second = HumanicoKlattFrameRenderer.Render(frames, 8_000, 17, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Contains(first, sample => sample != 0);
    }

    [Fact]
    public void Humanico_klatt_frame_renderer_keeps_voiced_source_present()
    {
        IReadOnlyList<SpeechFrame> frames = HumanicoFramePlanner.Plan(HumanicoSegmentPlanner.PlanWord("tonight"), 145, 1);
        short[] samples = HumanicoKlattFrameRenderer.Render(frames, 8_000, 31, CancellationToken.None);

        Assert.True(samples.Max(sample => Math.Abs((int)sample)) > 100);
    }

    [Fact]
    public void Humanico_frame_planner_assigns_voiced_formant_parameters()
    {
        IReadOnlyList<SpeechFrame> frames = HumanicoFramePlanner.Plan(HumanicoSegmentPlanner.PlanWord("hello"), 105, 1);

        SpeechFrame vowel = Assert.Single(frames.Where(frame => frame.Symbol == "AH").Take(1));
        Assert.True(vowel.Parameters.Voiced);
        Assert.InRange(vowel.Parameters.F1Hz, 100, 1_000);
        Assert.InRange(vowel.Parameters.F2Hz, 700, 2_500);
        Assert.Equal(0, vowel.Parameters.NoiseGain);
        Assert.Equal(0.92, vowel.Parameters.SourceGain);
        SpeechFrame consonant = Assert.Single(frames.Where(frame => frame.Symbol == "HH").Take(1));
        Assert.False(consonant.Parameters.Voiced);
        Assert.Equal(1.1, consonant.Parameters.NoiseGain);
    }

    [Fact]
    public void Humanico_frame_synthesizer_is_deterministic_and_cancellable()
    {
        IReadOnlyList<SpeechFrame> frames = HumanicoFramePlanner.Plan(HumanicoSegmentPlanner.PlanWord("hello"), 105, 1);

        short[] first = HumanicoFrameSynthesizer.Render(frames, 17);
        short[] second = HumanicoFrameSynthesizer.Render(frames, 17);

        Assert.Equal(first, second);
        Assert.Contains(first, sample => sample != 0);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => HumanicoFrameSynthesizer.Render(frames, 17, cancellation.Token));
    }

    [Fact]
    public void Humanico_front_end_uses_fixture_dictionary_before_letter_fallback()
    {
        Assert.Equal(["AE", "M", "P", "AH", "L"], HumanicoFrontEnd.WordToPhones("ample").Select(phone => phone.Symbol));
        Assert.Equal(1, HumanicoFrontEnd.WordToPhones("ample")[0].Stress);
        Assert.Equal(["K", "K", "Z"], HumanicoFrontEnd.WordToPhones("qxz").Select(phone => phone.Symbol));
        Assert.StartsWith("cmudict/", HumanicoPronunciationLexicon.SourceIdentity);
    }

    [Fact]
    public void Humanico_front_end_loads_pinned_cmudict_for_words_outside_fixture()
    {
        IReadOnlyList<SpeechPhone> phones = HumanicoFrontEnd.WordToPhones("electricity");

        Assert.NotEmpty(phones);
        Assert.Contains(phones, phone => phone.Symbol == "EH" && phone.Stress >= 0);
    }

    [Fact]
    public void Humanico_reference_manifest_pins_all_sound_determining_fixture_inputs()
    {
        HumanicoReferenceManifest manifest = HumanicoReferenceManifest.PinnedFixture;

        Assert.Equal("ce793bc9304cf34637199b0efde72c8afe130b1d", manifest.UpstreamRevision);
        Assert.Equal(HumanicoPronunciationLexicon.SourceIdentity, manifest.DictionaryIdentity);
        Assert.Equal("81917843C7F44CE2B094AC63873C2C7A4CF802040792C455BA3CA406891C3D22", manifest.DictionarySha256);
        Assert.EndsWith(manifest.DictionarySha256, manifest.DictionaryIdentity, StringComparison.Ordinal);
        Assert.Equal(10_000, manifest.InternalSampleRate);
        Assert.Equal(5, manifest.FrameMilliseconds);
        Assert.Equal(SpeechSeed.AlgorithmIdentity, manifest.NoiseAlgorithm);
    }

    [Fact]
    public void Humanico_reference_fixture_keeps_phone_and_frame_boundaries_stable()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "humanico-reference.json");
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, document.RootElement.GetProperty("fixtureVersion").GetInt32());
        Assert.Equal("internal-port-contract", document.RootElement.GetProperty("kind").GetString());
        foreach (System.Text.Json.JsonElement item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            string word = item.GetProperty("word").GetString()!;
            string[] expectedPhones = [.. item.GetProperty("phones").EnumerateArray().Select(phone => phone.GetString()!)];
            IReadOnlyList<SpeechPhone> phones = HumanicoFrontEnd.WordToPhones(word);
            Assert.Equal(expectedPhones, phones.Select(phone => phone.Stress >= 0 ? $"{phone.Symbol}{phone.Stress}" : phone.Symbol));
            IReadOnlyList<SpeechFrame> frames = HumanicoFramePlanner.Plan(HumanicoSegmentPlanner.PlanPhones(phones), 105, 1);
            Assert.Equal(item.GetProperty("frameCountAtRate1").GetInt32(), frames.Count);
        }
    }

    [Fact]
    public void Engine_uses_the_explicit_frame_scaffold_identity_and_audible_frame_path()
    {
        SpeechResult result = HumanicoSpeechEngine.Render(new("ample robot", Seed: 17) { Engine = "retro" });

        Assert.Equal("retro/formant-2", result.EngineIdentity);
        Assert.Contains(result.Samples, sample => sample != 0);
    }

    [Fact]
    public void Frame_synthesizer_uses_formant_parameters_for_vowels()
    {
        SpeechFrame eh = HumanicoFramePlanner.Plan(HumanicoSegmentPlanner.PlanWord("hello"), 105, 1)
            .First(frame => frame.Symbol == "AH");
        SpeechFrame aa = HumanicoFramePlanner.Plan(HumanicoSegmentPlanner.PlanWord("robot"), 105, 1)
            .First(frame => frame.Symbol == "AA");

        Assert.NotEqual(HumanicoFrameSynthesizer.Render([eh], 17), HumanicoFrameSynthesizer.Render([aa], 17));
    }

    [Fact]
    public void Word_note_planner_orders_chords_and_assigns_each_note_once()
    {
        SpeechNoteEvent[] notes =
        [
            new(0, 67, 100, 1, 2), new(0, 60, 100, 1, 1), new(12, 64, 0, 1, 3), new(24, 72, 100, 1, 4)
        ];

        IReadOnlyList<SpeechWordNote> result = HumanicoWordNotePlanner.Assign(["we", "are", "made"], notes);

        Assert.Equal(["we", "are", "made"], result.Select(item => item.Word));
        Assert.Equal([60, 67, 72], result.Select(item => item.Note.MidiPitch));
        Assert.Equal([0, 1, 2], result.Select(item => item.WordIndex));
    }

    [Fact]
    public void Note_pitch_mapper_uses_reference_note_velocity_and_explicit_saturation()
    {
        SpeechNotePitch middle = HumanicoNotePitchMapper.Map(new(0, 60, 127, 1, 0));
        SpeechNotePitch upper = HumanicoNotePitchMapper.Map(new(0, 72, 64, 1, 1));
        SpeechNotePitch extreme = HumanicoNotePitchMapper.Map(new(0, 127, 100, 1, 2));

        Assert.Equal(105, middle.FundamentalHz);
        Assert.Equal(1, middle.Amplitude);
        Assert.False(middle.Saturated);
        Assert.InRange(upper.FundamentalHz, 200, 220);
        Assert.InRange(upper.Amplitude, 0.49, 0.51);
        Assert.Equal(700, extreme.FundamentalHz);
        Assert.True(extreme.Saturated);
    }

    [Fact]
    public void Frame_parameters_distinguish_fricatives_and_stops()
    {
        SpeechFrame fricative = HumanicoFramePlanner.Plan(HumanicoSegmentPlanner.PlanWord("speech"), 105, 1)
            .First(frame => frame.Symbol == "S");
        SpeechFrame stop = HumanicoFramePlanner.Plan(HumanicoSegmentPlanner.PlanWord("top"), 105, 1)
            .First(frame => frame.Symbol == "T");

        Assert.True(fricative.Parameters.NoiseGain > stop.Parameters.NoiseGain);
        Assert.False(fricative.Parameters.Voiced);
        Assert.False(stop.Parameters.Voiced);
    }

    [Fact]
    public void Frame_parameters_use_upstream_voiced_targets_for_sonorants()
    {
        SpeechFrame nasal = HumanicoFramePlanner.Plan(HumanicoSegmentPlanner.PlanWord("ample"), 105, 1)
            .First(frame => frame.Symbol == "M");
        SpeechFrame liquid = HumanicoFramePlanner.Plan(HumanicoSegmentPlanner.PlanWord("world"), 105, 1)
            .First(frame => frame.Symbol == "L");

        Assert.True(nasal.Parameters.Voiced);
        Assert.True(liquid.Parameters.Voiced);
        Assert.InRange(nasal.Parameters.F2Hz, 900, 1_100);
        Assert.InRange(liquid.Parameters.F2Hz, 1_200, 1_500);
    }

    [Fact]
    public void Musical_phrase_renderer_uses_note_pitch_and_velocity()
    {
        short[] low = HumanicoMusicalPhraseRenderer.Render(["ample"], [new SpeechNoteEvent(0, 48, 127, 1, 0)], 17);
        short[] high = HumanicoMusicalPhraseRenderer.Render(["ample"], [new SpeechNoteEvent(0, 72, 64, 1, 0)], 17);

        Assert.Equal(low.Length, high.Length);
        Assert.NotEqual(low, high);
        Assert.Contains(low.Zip(high), pair => Math.Abs(pair.First) > Math.Abs(pair.Second));
    }

    [Fact]
    public void Frame_synthesizer_keeps_vowel_formants_audible_at_low_fundamental()
    {
        SpeechFrame eh = HumanicoFramePlanner.Plan(HumanicoSegmentPlanner.PlanWord("hello"), 105, 1)
            .First(frame => frame.Symbol == "AH");
        short[] samples = HumanicoFrameSynthesizer.Render([eh], 17);

        Assert.True(samples.Max(sample => Math.Abs((int)sample)) > 500);
        Assert.NotEqual(samples, HumanicoFrameSynthesizer.Render(
            [eh with { Parameters = eh.Parameters with { F2Hz = 900 } }], 17));
    }

    [Fact]
    public void Frame_planner_carries_segment_positions_for_transition_windows()
    {
        IReadOnlyList<SpeechFrame> frames = HumanicoFramePlanner.Plan(HumanicoSegmentPlanner.PlanWord("hello"), 105, 1);

        Assert.Equal(0, frames[0].SegmentIndex);
        Assert.Equal(0, frames[0].FrameInSegment);
        Assert.Contains(frames, frame => frame.SegmentIndex > 0 && frame.FrameInSegment == 0);
    }

    [Fact]
    public void The_default_engine_is_humanico()
    {
        SpeechResult byDefault = HumanicoSpeechEngine.Render(new("hello robot", Seed: 17));
        SpeechResult explicitHumanico = HumanicoSpeechEngine.Render(new SpeechRequest("hello robot", Seed: 17) { Engine = "humanico" });
        SpeechResult retro = HumanicoSpeechEngine.Render(new SpeechRequest("hello robot", Seed: 17) { Engine = "retro" });

        Assert.Equal("humanico/klatt-candidate-3", byDefault.EngineIdentity);
        Assert.Equal(explicitHumanico.Samples, byDefault.Samples);
        Assert.NotEqual(retro.Samples, byDefault.Samples);
    }

    [Fact]
    public void Humanico_words_are_in_order_and_inside_the_output()
    {
        SpeechResult result = HumanicoSpeechEngine.Render(new("one two three", Seed: 3));

        Assert.Equal(3, result.Words.Count);
        Assert.All(result.Words, word => Assert.True(word.StartSample < word.EndSample && word.EndSample <= result.Samples.Length));
        Assert.True(result.Words[0].EndSample <= result.Words[1].StartSample && result.Words[1].EndSample <= result.Words[2].StartSample);
        Assert.Contains(result.Samples, sample => sample != 0);
    }

    [Fact]
    public void Humanico_speaks_explicit_phonemes()
    {
        SpeechResult result = HumanicoSpeechEngine.Render(new SpeechRequest(string.Empty) { Phonemes = "HH AH0 L OW1" });

        Assert.Equal("humanico/klatt-candidate-3", result.EngineIdentity);
        Assert.Contains(result.Samples, sample => sample != 0);
    }

    [Fact]
    public void Humanico_stops_when_it_is_cancelled_before_it_starts()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => HumanicoSpeechEngine.Render(new("one two three"), cancellation.Token));
    }
}

