namespace Ample.Speech.Tests.Flite;

/// <summary>
/// Flite X2 and X3: the shaping of a finished render (the leading silence, the pauses, the brightness) is measured, never judged by ear,
/// and an unasked shaping leaves the render alone.
/// </summary>
public sealed class FliteShapingTests
{
    private const int Lead = 4_000;
    private const int WordLength = 2_000;
    private const int Gap = 1_000;
    private const int Tail = 800;

    /// <summary>A render of two words with a leading silence, a pause between them and a tail; the words carry a loud ramp, the silences a little noise.</summary>
    private static FliteRender Sample(out short[] firstWord, out short[] secondWord)
    {
        Random noise = new(7);
        List<short> samples = [];
        short Quiet() => (short)noise.Next(-20, 21);
        for (int i = 0; i < Lead; i++) samples.Add(Quiet());
        firstWord = [.. Enumerable.Range(0, WordLength).Select(i => (short)(8_000 * Math.Sin(i * 0.31)))];
        samples.AddRange(firstWord);
        for (int i = 0; i < Gap; i++) samples.Add(Quiet());
        secondWord = [.. Enumerable.Range(0, WordLength).Select(i => (short)(9_000 * Math.Sin(i * 0.17)))];
        samples.AddRange(secondWord);
        for (int i = 0; i < Tail; i++) samples.Add(Quiet());
        return new([.. samples], [new("one", 0, 3, Lead, Lead + WordLength), new("two", 4, 7, Lead + WordLength + Gap, Lead + 2 * WordLength + Gap)]);
    }

    private static short[] Slice(short[] samples, int from, int length) => [.. samples.Skip(from).Take(length)];

    private static int LargestStep(short[] samples, int from, int to) => Enumerable.Range(from + 1, to - from - 1).Max(i => Math.Abs(samples[i] - samples[i - 1]));

    [Fact]
    public void Nothing_asked_returns_the_render_itself()
    {
        FliteRender render = Sample(out _, out _);

        Assert.Same(render, FliteShaping.Shape(render, leadTrim: false, pauseScale: 1));
    }

    [Fact]
    public void The_leading_silence_is_dropped_down_to_one_millisecond_and_the_words_move_with_it()
    {
        FliteRender render = Sample(out short[] first, out short[] second);
        int removed = Lead - FliteShaping.LeadKeepSamples;

        FliteRender shaped = FliteShaping.Shape(render, leadTrim: true, pauseScale: 1);

        Assert.Equal(render.Samples.Length - removed, shaped.Samples.Length);
        Assert.Equal(FliteShaping.LeadKeepSamples, shaped.Words[0].StartSample);
        Assert.Equal(FliteShaping.LeadKeepSamples + WordLength, shaped.Words[0].EndSample);
        Assert.Equal(render.Words[1].StartSample - removed, shaped.Words[1].StartSample);
        Assert.Equal(render.Words[1].EndSample - removed, shaped.Words[1].EndSample);
        Assert.Equal(first, Slice(shaped.Samples, shaped.Words[0].StartSample, WordLength));
        Assert.Equal(second, Slice(shaped.Samples, shaped.Words[1].StartSample, WordLength));
        Assert.Equal(["one", "two"], shaped.Words.Select(word => word.Name));
        Assert.Equal([0, 4], shaped.Words.Select(word => word.TextFrom));
        Assert.InRange(shaped.Samples[0], -1, 1);   // the kept millisecond fades in from silence
    }

    [Fact]
    public void A_render_that_starts_on_its_first_word_is_not_cut_at_all()
    {
        FliteRender render = new([.. Enumerable.Range(0, 400).Select(i => (short)(i * 10))], [new("a", 0, 1, 5, 400)]);

        FliteRender shaped = FliteShaping.Shape(render, leadTrim: true, pauseScale: 1);

        Assert.Equal(400, shaped.Samples.Length);
        Assert.Equal(5, shaped.Words[0].StartSample);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.5, 500)]
    [InlineData(2.0, 2_000)]
    [InlineData(4.0, 4_000)]
    public void The_pause_between_the_words_is_scaled_and_the_words_themselves_are_untouched(double scale, int gap)
    {
        FliteRender render = Sample(out short[] first, out short[] second);

        FliteRender shaped = FliteShaping.Shape(render, leadTrim: false, pauseScale: scale);

        Assert.Equal(render.Samples.Length - Gap + gap, shaped.Samples.Length);
        Assert.Equal(Lead, shaped.Words[0].StartSample);
        Assert.Equal(Lead + WordLength, shaped.Words[0].EndSample);
        Assert.Equal(Lead + WordLength + gap, shaped.Words[1].StartSample);
        Assert.Equal(Lead + 2 * WordLength + gap, shaped.Words[1].EndSample);
        Assert.Equal(first, Slice(shaped.Samples, shaped.Words[0].StartSample, WordLength));
        Assert.Equal(second, Slice(shaped.Samples, shaped.Words[1].StartSample, WordLength));
        Assert.Equal(render.Samples.Skip(render.Samples.Length - Tail), shaped.Samples.Skip(shaped.Samples.Length - Tail));   // the tail stays as the voice made it
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.3)]
    [InlineData(2.5)]
    public void A_scaled_pause_has_no_click_at_the_splice(double scale)
    {
        FliteRender render = Sample(out _, out _);
        int quietStep = LargestStep(render.Samples, Lead + WordLength + 1, Lead + WordLength + Gap - 1);

        FliteRender shaped = FliteShaping.Shape(render, leadTrim: false, pauseScale: scale);

        int from = shaped.Words[0].EndSample, to = shaped.Words[1].StartSample;
        if (to - from > 3) Assert.True(LargestStep(shaped.Samples, from + 1, to - 1) <= quietStep + 2, "a step inside the shaped pause is larger than the noise of the pause");
    }

    [Fact]
    public void Lead_trim_and_pause_scale_work_together_and_the_timings_stay_inside_the_samples()
    {
        FliteRender render = Sample(out _, out _);

        FliteRender shaped = FliteShaping.Shape(render, leadTrim: true, pauseScale: 0.5);

        Assert.Equal(render.Samples.Length - (Lead - FliteShaping.LeadKeepSamples) - Gap / 2, shaped.Samples.Length);
        Assert.All(shaped.Words, word => Assert.InRange(word.EndSample, word.StartSample, shaped.Samples.Length));
        Assert.Equal(shaped.Words[0].EndSample + Gap / 2, shaped.Words[1].StartSample);
    }

    [Fact]
    public void A_render_without_words_is_left_alone()
    {
        FliteRender render = new([1, 2, 3], []);

        Assert.Same(render, FliteShaping.Shape(render, leadTrim: true, pauseScale: 0));
    }

    [Theory]
    [InlineData(200.0, 0.0, 0.3)]
    [InlineData(200.0, 6.0, 0.3)]
    [InlineData(8_000.0, 6.0, 0.6)]
    [InlineData(8_000.0, 12.0, 0.8)]
    public void The_brightness_shelf_adds_its_gain_at_the_high_end_and_leaves_the_low_end(double hz, double gainDb, double tolerance)
    {
        const int rate = 22_050;
        short[] sine = [.. Enumerable.Range(0, rate).Select(i => (short)(3_000 * Math.Sin(2 * Math.PI * hz * i / rate)))];
        short[] shaped = [.. sine];

        FliteShaping.Brighten(shaped, rate, gainDb);

        double Rms(short[] samples) => Math.Sqrt(samples.Skip(rate / 2).Select(x => (double)x * x).Average());
        double expected = hz < 1_000 ? 0 : gainDb;   // 200 Hz is far below the corner and 8 kHz far above it
        Assert.Equal(expected, 20 * Math.Log10(Rms(shaped) / Rms(sine)), tolerance);
    }

    [Fact]
    public void No_brightness_leaves_the_samples_as_they_are()
    {
        short[] samples = [.. Enumerable.Range(0, 1_000).Select(i => (short)(i * 7 % 3_000))];
        short[] copy = [.. samples];

        FliteShaping.Brighten(samples, 22_050, 0);

        Assert.Equal(copy, samples);
    }

    [Fact]
    public void The_engine_without_options_gives_the_same_samples_as_before()
    {
        FliteRender render = Sample(out _, out _);
        SpeechRequest plain = new("one two", 16_000, 1, 95) { Engine = "flite" };

        SpeechResult before = FliteSpeechEngine.Render(plain, new FixedPipeline(render), useCache: false, CancellationToken.None);
        SpeechResult explicitDefaults = FliteSpeechEngine.Render(plain with { }, new FixedPipeline(render), useCache: false, CancellationToken.None);

        Assert.Equal(render.Samples, before.Samples);
        Assert.Equal(before.Samples, explicitDefaults.Samples);
    }

    [Fact]
    public void The_engine_trims_and_scales_and_the_timings_are_in_the_rate_of_the_request()
    {
        FliteRender render = Sample(out _, out _);
        SpeechRequest request = new("one two", 32_000, 1, 95) { Engine = "flite", LeadTrim = true, PauseScale = 0 };

        SpeechResult result = FliteSpeechEngine.Render(request, new FixedPipeline(render), useCache: false, CancellationToken.None);

        int shapedLength = render.Samples.Length - (Lead - FliteShaping.LeadKeepSamples) - Gap;
        Assert.Equal(shapedLength * 2, result.Samples.Length);
        Assert.Equal(FliteShaping.LeadKeepSamples * 2, result.Words[0].StartSample);
        Assert.Equal(result.Words[0].EndSample, result.Words[1].StartSample);
        Assert.Equal(["one", "two"], result.Spans!.Select(span => span.Word));
    }

    [Fact]
    public void The_exact_render_is_cached_without_the_shaping()
    {
        FliteRender render = Sample(out _, out _);
        FixedPipeline pipeline = new(render);
        SpeechRequest request = new("one two cached shaping", 16_000, 1, 95) { Engine = "flite" };

        SpeechResult exact = FliteSpeechEngine.Render(request, pipeline, useCache: true, CancellationToken.None);
        SpeechResult trimmed = FliteSpeechEngine.Render(request with { LeadTrim = true }, pipeline, useCache: true, CancellationToken.None);
        SpeechResult again = FliteSpeechEngine.Render(request, pipeline, useCache: true, CancellationToken.None);

        Assert.Equal(1, pipeline.Runs);
        Assert.True(trimmed.Samples.Length < exact.Samples.Length);
        Assert.Equal(exact.Samples, again.Samples);
    }

    [Theory]
    [InlineData(-0.1, 0.0)]
    [InlineData(4.5, 0.0)]
    [InlineData(double.NaN, 0.0)]
    [InlineData(1.0, -1.0)]
    [InlineData(1.0, 12.5)]
    [InlineData(1.0, double.PositiveInfinity)]
    public void The_options_have_a_range(double pauseScale, double brightness)
    {
        FliteRender render = Sample(out _, out _);
        SpeechRequest request = new("one", 16_000, 1, 95) { Engine = "flite", PauseScale = pauseScale, BrightnessDb = brightness };

        Assert.Throws<ArgumentOutOfRangeException>(() => FliteSpeechEngine.Render(request, new FixedPipeline(render), useCache: false, CancellationToken.None));
    }

    private sealed class FixedPipeline(FliteRender render) : IFlitePipeline
    {
        public int Runs { get; private set; }

        public FliteRender Run(string text, FliteSettings settings, CancellationToken cancellationToken)
        {
            Runs++;
            return render;
        }
    }
}
