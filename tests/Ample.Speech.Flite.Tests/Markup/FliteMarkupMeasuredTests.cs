namespace Ample.Speech.Flite.Tests.Markup;

/// <summary>
/// What the tags do to the sound, measured and not listened to: the pitch of a tagged word against the pitch that was asked (within 10 cents), the length of a break (within 10 ms),
/// the length of a word under <c>&lt;rate&gt;</c>, and that the word marks follow the text as it is written. A text without tags is the exact render.
/// </summary>
public class FliteMarkupMeasuredTests
{
    private const int Rate = 16_000;

    private static SpeechRequest Request(string text, double pitch = 95, double range = 0) =>
        new(text, Rate, 1, pitch) { Engine = "flite", PitchRangeHz = range };

    /// <summary>The median pitch in Hz of the voiced frames of a waveform: normalised autocorrelation over 40 ms frames, lags for 60 to 480 Hz.</summary>
    private static double MedianPitch(short[] samples)
    {
        List<double> found = [];
        int frame = Rate * 40 / 1000, minLag = Rate / 480, maxLag = Rate / 60;
        for (int start = 0; start + frame + maxLag < samples.Length; start += frame / 2)
        {
            double energy = 0;
            for (int i = 0; i < frame; i++) energy += (double)samples[start + i] * samples[start + i];
            if (energy < 1e6) continue;
            double best = 0;
            int bestLag = 0;
            double[] corr = new double[maxLag + 2];
            for (int lag = minLag - 1; lag <= maxLag + 1; lag++)
            {
                double sum = 0, other = 0;
                for (int i = 0; i < frame && start + i + lag < samples.Length; i++)
                {
                    sum += (double)samples[start + i] * samples[start + i + lag];
                    other += (double)samples[start + i + lag] * samples[start + i + lag];
                }

                corr[lag] = sum / Math.Sqrt(energy * other + 1);
                if (lag >= minLag && lag <= maxLag && corr[lag] > best) { best = corr[lag]; bestLag = lag; }
            }

            if (best > 0.85)
            {
                // a parabola through the peak and its neighbours: the lag in fractions of a sample (at 16 kHz one sample is 17 cents at 330 Hz)
                double left = corr[bestLag - 1], right = corr[bestLag + 1], denominator = left - (2 * best) + right;
                double lagExact = denominator == 0 ? bestLag : bestLag + (0.5 * (left - right) / denominator);
                found.Add(Rate / lagExact);
            }
        }

        Assert.True(found.Count >= 3, "the word has voiced frames");
        found.Sort();
        return found[found.Count / 2];
    }

    private static double Cents(double a, double b) => 1200 * Math.Log2(a / b);

    [Theory]
    [InlineData("<pitch 150>mama mama</pitch>", 150)]
    [InlineData("<pitch 220>mama mama</pitch>", 220)]
    [InlineData("<pitch 330>mama mama</pitch>", 330)]
    public void A_pitch_in_hertz_is_followed_within_ten_cents(string text, double hz)
    {
        SpeechResult made = FliteSpeechEngine.Render(Request(text));

        Assert.InRange(Math.Abs(Cents(MedianPitch(made.Samples), hz)), 0, 10);
    }

    [Theory]
    [InlineData("<pitch +12st>mama mama</pitch>", 190)]
    [InlineData("<pitch -5st>mama mama</pitch>", 71.2)]
    [InlineData("<pitch +5st>mama mama</pitch>", 126.8)]
    public void A_pitch_in_semitones_is_a_shift_of_the_pitch_of_the_block(string text, double expected)
    {
        SpeechResult made = FliteSpeechEngine.Render(Request(text));

        Assert.InRange(Math.Abs(Cents(MedianPitch(made.Samples), expected)), 0, 10);
    }

    [Fact]
    public void Only_the_tagged_words_change_pitch()
    {
        SpeechResult made = FliteSpeechEngine.Render(Request("mama <pitch 220>mama</pitch> mama"));
        IReadOnlyList<SpeechWordTiming> words = made.Words;
        double Pitch(int w) => MedianPitch(made.Samples[words[w].StartSample..words[w].EndSample]);

        Assert.InRange(Math.Abs(Cents(Pitch(1), 220)), 0, 10);
        Assert.InRange(Math.Abs(Cents(Pitch(0), 95)), 0, 15);
        Assert.InRange(Math.Abs(Cents(Pitch(2), 95)), 0, 15);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(750)]
    [InlineData(2000)]
    public void A_break_is_exactly_as_long_as_written(int milliseconds)
    {
        int plain = FliteSpeechEngine.Render(Request("one two")).Samples.Length;
        SpeechResult broken = FliteSpeechEngine.Render(Request($"one<break {milliseconds}ms/> two"));

        Assert.InRange(Math.Abs(broken.Samples.Length - plain - milliseconds * Rate / 1000), 0, Rate / 100);   // within 10 ms
        // the words keep their order and the second one starts later by the length of the pause
        SpeechResult reference = FliteSpeechEngine.Render(Request("one two"));
        Assert.InRange(Math.Abs(broken.Words[1].StartSample - reference.Words[1].StartSample - milliseconds * Rate / 1000), 0, Rate / 100);
        Assert.Equal(reference.Words[0].EndSample, broken.Words[0].EndSample);
    }

    [Fact]
    public void A_break_at_the_start_and_at_the_end_is_silence_and_a_break_does_not_click()
    {
        short[] lead = FliteSpeechEngine.Render(Request("<break 500ms/>one")).Samples;
        short[] tail = FliteSpeechEngine.Render(Request("one<break 500ms/>")).Samples;
        short[] middle = FliteSpeechEngine.Render(Request("one<break 500ms/>two")).Samples;

        Assert.True(lead.Length > 8000);
        Assert.True(tail.Length > 8000);
        Assert.All(new[] { lead, tail, middle }, s => Assert.True(MaxStep(s) < 20_000, "no jump in the waveform"));
    }

    private static int MaxStep(short[] s)
    {
        int max = 0;
        for (int i = 1; i < s.Length; i++) max = Math.Max(max, Math.Abs(s[i] - s[i - 1]));
        return max;
    }

    [Theory]
    [InlineData(2.0, 0.5)]
    [InlineData(0.5, 2.0)]
    [InlineData(1.5, 1d / 1.5)]
    public void A_rate_scales_the_length_of_the_words_it_covers(double rate, double factor)
    {
        SpeechResult plain = FliteSpeechEngine.Render(Request("a remember tomorrow b"));
        SpeechResult tagged = FliteSpeechEngine.Render(Request($"a <rate {rate.ToString(System.Globalization.CultureInfo.InvariantCulture)}>remember tomorrow</rate> b"));

        double Length(SpeechResult r) => r.Words[2].EndSample - r.Words[1].StartSample;
        Assert.InRange(Length(tagged) / Length(plain), factor * 0.93, factor * 1.07);
        Assert.Equal(plain.Words[0].EndSample - plain.Words[0].StartSample, tagged.Words[0].EndSample - tagged.Words[0].StartSample);   // the words outside are as long as before
    }

    [Fact]
    public void Emph_raises_the_pitch_by_three_semitones_and_makes_the_word_longer()
    {
        SpeechResult plain = FliteSpeechEngine.Render(Request("mama mama mama"));
        SpeechResult stressed = FliteSpeechEngine.Render(Request("mama <emph>mama</emph> mama"));
        double Pitch(SpeechResult r, int w) => MedianPitch(r.Samples[r.Words[w].StartSample..r.Words[w].EndSample]);

        Assert.InRange(Cents(Pitch(stressed, 1), Pitch(plain, 1)), 290, 310);   // 300 cents
        Assert.InRange((double)(stressed.Words[1].EndSample - stressed.Words[1].StartSample) / (plain.Words[1].EndSample - plain.Words[1].StartSample), 1.08, 1.22);
    }

    [Fact]
    public void A_range_of_zero_is_a_flat_voice_and_a_wide_range_moves_the_pitch_from_word_to_word()
    {
        double Spread(string text)
        {
            SpeechResult made = FliteSpeechEngine.Render(Request(text, pitch: 150, range: 11));
            List<double> pitches = [];
            foreach (SpeechWordTiming word in made.Words)
            {
                short[] part = made.Samples[word.StartSample..word.EndSample];
                if (part.Length < Rate / 10) continue;
                pitches.Add(MedianPitch(part));
            }

            return Math.Abs(Cents(pitches.Max(), pitches.Min()));
        }

        const string words = "mama mama mama mama mama mama mama mama";
        double flat = Spread($"<range 0>{words}</range>");
        double wide = Spread($"<range 40>{words}</range>");
        Assert.True(flat < 40, $"flat {flat:0} cents");
        Assert.True(wide > flat + 100, $"wide {wide:0} cents against flat {flat:0} cents");
    }

    [Fact]
    public void The_word_marks_follow_the_text_as_it_is_written_with_its_tags()
    {
        const string text = "say <emph>hello</emph> to <pitch +3st>the</pitch> &amp; world";
        SpeechResult made = FliteSpeechEngine.Render(Request(text));

        Assert.NotNull(made.Spans);
        Assert.Equal("hello", text[made.Spans![1].From..made.Spans[1].To]);
        Assert.Equal("the", text[made.Spans[3].From..made.Spans[3].To]);
        Assert.Equal("&amp;", text[made.Spans[4].From..made.Spans[4].To]);
        Assert.Equal("world", text[made.Spans[5].From..made.Spans[5].To]);
    }

    [Fact]
    public void A_text_without_tags_gives_the_samples_it_always_gave_and_tags_do_not_end_up_in_the_cache_of_the_plain_text()
    {
        const string plain = "This is music today again.";
        short[] before = FliteSpeechEngine.Render(Request(plain)).Samples;
        _ = FliteSpeechEngine.Render(Request("This is <rate 2>music</rate> today again."));
        short[] after = FliteSpeechEngine.Render(Request(plain)).Samples;

        Assert.Equal(before, after);
        Assert.NotEqual(before, FliteSpeechEngine.Render(Request("This is <rate 2>music</rate> today again.")).Samples);
    }
}
