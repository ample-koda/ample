using Ample.Speech.Flite;

namespace Ample.Speech.Tests.Flite;

/// <summary>Flite M8: the speech engine around the Flite pipeline (settings, resampling, timings, limits, phones), with a pipeline that is made up for the test.</summary>
public sealed class FliteSpeechEngineTests
{
    private static SpeechRequest Request(string text = "one two", int sampleRate = 32_000, double rate = 1, double pitch = 95) =>
        new(text, sampleRate, rate, pitch) { Engine = "flite" };

    [Fact]
    public void The_request_becomes_the_settings_of_flite()
    {
        FakePipeline pipeline = new(1_600);
        _ = FliteSpeechEngine.Render(Request(rate: 0.5, pitch: 120) with { MaxWorkingSamples = 10_000 }, pipeline, useCache: false, CancellationToken.None);

        Assert.Equal(1.1 / 0.5, pipeline.Seen!.DurationStretch, 12);
        Assert.Equal(120, pipeline.Seen.F0Mean);
        Assert.Equal(FliteSpeechEngine.DefaultPitchRangeHz, pipeline.Seen.F0Stddev);
        Assert.Equal(16_000, pipeline.Seen.MaxSamples);   // 10,000 working samples at 10 kHz are one second, which is 16,000 at 16 kHz
    }

    [Fact]
    public void The_pitch_range_is_passed_on_and_a_flat_voice_is_allowed()
    {
        FakePipeline pipeline = new(1_600);
        _ = FliteSpeechEngine.Render(Request() with { PitchRangeHz = 0 }, pipeline, useCache: false, CancellationToken.None);

        Assert.Equal(0, pipeline.Seen!.F0Stddev);
    }

    [Fact]
    public void The_samples_are_resampled_to_the_rate_of_the_request_and_the_timings_follow()
    {
        FakePipeline pipeline = new(16_000, [new("one", 0, 3, 0, 8_000), new("two", 4, 7, 8_000, 16_000)]);
        SpeechResult result = FliteSpeechEngine.Render(Request(sampleRate: 48_000), pipeline, useCache: false, CancellationToken.None);

        Assert.Equal(48_000, result.Samples.Length);
        Assert.Equal(48_000, result.SampleRate);
        Assert.Equal(FliteSpeechEngine.Identity, result.EngineIdentity);
        Assert.Equal([new SpeechWordTiming(0, 0, 24_000, "one"), new SpeechWordTiming(1, 24_000, 48_000, "two")], result.Words);
        Assert.Equal([new SpeechWordSpan("one", 0, 3), new SpeechWordSpan("two", 4, 7)], result.Spans);
    }

    [Fact]
    public void A_ramp_is_resampled_by_linear_interpolation()
    {
        short[] ramp = [0, 1_000, 2_000, 3_000];
        FakePipeline pipeline = new(ramp, []);
        SpeechResult result = FliteSpeechEngine.Render(Request(sampleRate: 32_000), pipeline, useCache: false, CancellationToken.None);

        Assert.Equal([0, 500, 1_000, 1_500, 2_000, 2_500, 3_000, 3_000], result.Samples);
    }

    [Fact]
    public void An_empty_render_is_silence_with_the_identity()
    {
        SpeechResult result = FliteSpeechEngine.Render(Request(), new FakePipeline(0), useCache: false, CancellationToken.None);

        Assert.Empty(result.Samples);
        Assert.Empty(result.Words);
        Assert.Equal(FliteSpeechEngine.Identity, result.EngineIdentity);
    }

    [Fact]
    public void The_output_limit_is_checked_before_the_output_is_made_and_the_pipeline_gets_the_same_limit()
    {
        FakePipeline pipeline = new(16_000);
        SpeechRequest request = Request(sampleRate: 16_000) with { MaxOutputSamples = 8_000 };

        Assert.Throws<SpeechResourceLimitException>(() => FliteSpeechEngine.Render(request, pipeline, useCache: false, CancellationToken.None));
        Assert.True(pipeline.Seen!.MaxSamples is > 0 and <= 8_001);
    }

    [Fact]
    public void A_pipeline_that_overruns_its_budget_is_refused()
    {
        FakePipeline pipeline = new(20_000);

        Assert.Throws<SpeechResourceLimitException>(() => FliteSpeechEngine.Render(Request() with { MaxWorkingSamples = 10_000 }, pipeline, useCache: false, CancellationToken.None));
    }

    [Fact]
    public void Cancellation_stops_before_the_pipeline_runs()
    {
        using CancellationTokenSource cancel = new();
        cancel.Cancel();
        FakePipeline pipeline = new(1_600);

        Assert.Throws<OperationCanceledException>(() => FliteSpeechEngine.Render(Request(), pipeline, useCache: false, cancel.Token));
        Assert.Equal(0, pipeline.Runs);
    }

    [Theory]
    [InlineData("flite", "kal16", 1, 95, true)]
    [InlineData("flite", "other", 1, 95, false)]
    [InlineData("flite", "kal16", 3, 95, false)]
    [InlineData("flite", "kal16", 1, 20, false)]
    [InlineData("humanico", "kal16", 1, 95, false)]
    public void The_request_is_validated(string engine, string voice, double rate, double pitch, bool valid)
    {
        SpeechRequest request = Request(rate: rate, pitch: pitch) with { Engine = engine, Voice = voice };
        Action render = () => FliteSpeechEngine.Render(request, new FakePipeline(1_600), useCache: false, CancellationToken.None);

        if (valid) render();
        else Assert.ThrowsAny<ArgumentException>(render);
    }

    [Fact]
    public void Phonemes_are_spoken_as_one_word_over_the_whole_waveform()
    {
        const string phonemes = "HH AH0 L OW1 W ER1 L D";
        SpeechRequest request = new(string.Empty, 16_000, 1, 95) { Engine = "flite", Phonemes = phonemes };

        SpeechResult first = FliteSpeechEngine.Render(request);
        SpeechResult second = FliteSpeechEngine.Render(request);

        Assert.True(first.Samples.Length > 2_000, "hello world takes more than an eighth of a second");
        Assert.Equal(first.Samples, second.Samples);
        SpeechWordTiming word = Assert.Single(first.Words);
        Assert.Equal("[phonemes]", word.SourceWord);
        Assert.Equal(0, word.StartSample);
        Assert.Equal(first.Samples.Length, word.EndSample);
        SpeechWordSpan span = Assert.Single(first.Spans!);
        Assert.Equal((0, phonemes.Length), (span.From, span.To));
    }

    [Fact]
    public void Phonemes_and_text_together_are_refused()
    {
        SpeechRequest request = new("hello", 16_000) { Engine = "flite", Phonemes = "HH AH0 L OW1" };

        Assert.ThrowsAny<ArgumentException>(() => FliteSpeechEngine.Render(request, new FakePipeline(1_600), useCache: false, CancellationToken.None));
    }

    [Fact]
    public void A_phoneme_that_is_not_in_the_voice_is_refused_with_the_list()
    {
        SpeechRequest request = new(string.Empty, 16_000) { Engine = "flite", Phonemes = "HH QQ L OW1" };

        ArgumentException error = Assert.ThrowsAny<ArgumentException>(() => FliteSpeechEngine.Render(request));
        Assert.Contains("'QQ'", error.Message, StringComparison.Ordinal);
        Assert.Contains("HH", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Phones_run_in_the_cache_under_their_own_key()
    {
        SpeechRequest phones = new(string.Empty, 16_000) { Engine = "flite", Phonemes = "HH AH0 L OW1" };
        SpeechRequest text = new("hello", 16_000) { Engine = "flite" };

        SpeechResult a = FliteSpeechEngine.Render(phones);
        SpeechResult b = FliteSpeechEngine.Render(text);

        Assert.NotEqual(a.Samples.Length, b.Samples.Length);
    }

    [Fact]
    public void The_words_stand_where_their_segments_sound_and_where_their_token_stands_in_the_text()
    {
        // two words of a text with a multi-byte character in front of the second: the place in the text counts characters, the places in the waveform come from the segment ends
        FliteRenderResult made = new()
        {
            Samples = new short[3_000],
            SampleRate = 16_000,
            Segments = [new("pau", 500, -1), new("hh", 1_000, 0), new("ow", 1_500, 0), new("pau", 1_700, -1), new("w", 2_200, 1), new("er", 3_000, 1)],
            Words = [new("hello", 0, "hello", 0), new("world", 1, "world", 10)],
            MissingDiphones = []
        };
        List<FliteSpokenWord> words = FliteDefaultPipeline.WordsOf("hello, \u00E9 world", made);

        Assert.Equal([new FliteSpokenWord("hello", 0, 5, 500, 1_500), new FliteSpokenWord("world", 9, 14, 1_700, 3_000)], words);
    }

    [Fact]
    public void A_number_is_several_words_that_share_the_place_of_the_number()
    {
        FliteRenderResult made = new()
        {
            Samples = new short[2_000],
            SampleRate = 16_000,
            Segments = [new("t", 500, 0), new("uw", 1_000, 0), new("t", 1_500, 1), new("uw", 2_000, 1)],
            Words = [new("two", 0, "22", 5), new("two", 0, "22", 5)],
            MissingDiphones = []
        };
        List<FliteSpokenWord> words = FliteDefaultPipeline.WordsOf("said 22", made);

        Assert.Equal([new FliteSpokenWord("two", 5, 7, 0, 1_000), new FliteSpokenWord("two", 5, 7, 1_000, 2_000)], words);
    }

    [Fact]
    public void The_real_voice_speaks_a_sentence_in_a_repeatable_way_with_places_in_the_text()
    {
        const string text = "Hello world, 12 monkeys.";
        SpeechRequest request = Request(text, sampleRate: 16_000);
        SpeechResult first = FliteSpeechEngine.Render(request, new FliteDefaultPipeline(), useCache: false, CancellationToken.None);
        SpeechResult second = FliteSpeechEngine.Render(request, new FliteDefaultPipeline(), useCache: false, CancellationToken.None);

        Assert.NotEmpty(first.Samples);
        Assert.Equal(first.Samples, second.Samples);
        Assert.Equal(["hello", "world", "twelve", "monkeys"], first.Words.Select(word => word.SourceWord));
        Assert.Equal([0, 6, 13, 16], first.Spans!.Select(span => span.From));
        Assert.Equal([5, 11, 15, 23], first.Spans!.Select(span => span.To));
        Assert.True(first.Words.Zip(first.Words.Skip(1)).All(pair => pair.First.StartSample < pair.First.EndSample && pair.First.EndSample <= pair.Second.StartSample));
        Assert.True(first.Words[^1].EndSample <= first.Samples.Length);
    }

    [Fact]
    public void A_text_of_several_sentences_is_spoken_a_sentence_at_a_time_and_the_last_sentence_is_not_lost()
    {
        // 'Done.' starts a new sentence at the very end of the text: Flite itself would drop it without a new line after it
        const string text = "Alice walked the dog. Then Bob left! Done.";
        SpeechResult result = FliteSpeechEngine.Render(Request(text, sampleRate: 16_000), new FliteDefaultPipeline(), useCache: false, CancellationToken.None);

        Assert.Equal(["alice", "walked", "the", "dog", "then", "bob", "left", "done"], result.Words.Select(word => word.SourceWord));
        Assert.Equal(text.IndexOf("Done", StringComparison.Ordinal), result.Spans![^1].From);
        Assert.True(result.Words.Zip(result.Words.Skip(1)).All(pair => pair.First.EndSample <= pair.Second.StartSample));
        // each sentence has its own melody, so the sound is not the one of the same text spoken as one utterance
        SpeechResult one = FliteSpeechEngine.Render(Request("Alice walked the dog, then Bob left, done", sampleRate: 16_000), new FliteDefaultPipeline(), useCache: false, CancellationToken.None);
        Assert.NotEqual(one.Samples.Length, result.Samples.Length);
    }

    [Fact]
    public void The_slower_rate_makes_a_longer_speech_and_the_pitch_changes_the_sound()
    {
        SpeechResult normal = FliteSpeechEngine.Render(Request("hello world", sampleRate: 16_000), new FliteDefaultPipeline(), useCache: false, CancellationToken.None);
        SpeechResult slow = FliteSpeechEngine.Render(Request("hello world", sampleRate: 16_000, rate: 0.5), new FliteDefaultPipeline(), useCache: false, CancellationToken.None);
        SpeechResult high = FliteSpeechEngine.Render(Request("hello world", sampleRate: 16_000, pitch: 180), new FliteDefaultPipeline(), useCache: false, CancellationToken.None);

        Assert.True(slow.Samples.Length > normal.Samples.Length * 3 / 2);
        Assert.NotEqual(normal.Samples, high.Samples);
    }

    [Fact]
    public void The_plan_of_a_request_is_its_render()
    {
        SpeechRequest request = Request("hello world", sampleRate: 22_050);
        SpeechPlan plan = SpeechPlanner.Plan(request);
        SpeechResult render = FliteSpeechEngine.Render(request);

        Assert.Equal(render.Samples.Length, plan.OutputSamples);
        Assert.Equal(render.Words, plan.Timings);
    }

    [Fact]
    public void The_memo_speaks_a_text_once_and_a_different_setting_again()
    {
        FakePipeline pipeline = new(1_600);
        string text = "memo " + Guid.NewGuid();
        _ = FliteSpeechEngine.Render(Request(text), pipeline, useCache: true, CancellationToken.None);
        _ = FliteSpeechEngine.Render(Request(text, sampleRate: 8_000), pipeline, useCache: true, CancellationToken.None);
        Assert.Equal(1, pipeline.Runs);

        _ = FliteSpeechEngine.Render(Request(text, rate: 1.5), pipeline, useCache: true, CancellationToken.None);
        Assert.Equal(2, pipeline.Runs);
    }

    private sealed class FakePipeline : IFlitePipeline
    {
        private readonly short[] samples;
        private readonly IReadOnlyList<FliteSpokenWord> words;

        public FakePipeline(int length, IReadOnlyList<FliteSpokenWord>? words = null)
        {
            samples = [.. Enumerable.Range(0, length).Select(i => (short)(i % 100))];
            this.words = words ?? [];
        }

        public FakePipeline(short[] samples, IReadOnlyList<FliteSpokenWord> words)
        {
            this.samples = samples;
            this.words = words;
        }

        public FliteSettings? Seen { get; private set; }

        public int Runs { get; private set; }

        public FliteRender Run(string text, FliteSettings settings, CancellationToken cancellationToken)
        {
            Seen = settings;
            Runs++;
            return new(samples, words);
        }
    }
}
