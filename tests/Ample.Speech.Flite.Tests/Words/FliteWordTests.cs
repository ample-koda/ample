namespace Ample.Speech.Flite.Tests.Words;

/// <summary>
/// One word of words mode (plan/speech/flite/EXTENSIONS.md, X1): <c>FliteSpeechEngine.RenderWord</c> speaks a word at one pitch, without the pauses of an utterance.
/// The pitch is measured (autocorrelation), not listened to; the sound itself is the exact path of the voice, which has its own parity tests against the C program.
/// </summary>
public class FliteWordTests
{
    [Theory]
    [InlineData(80)]
    [InlineData(110)]
    [InlineData(160)]
    [InlineData(220)]
    [InlineData(330)]
    public void A_word_is_sung_at_the_pitch_that_was_asked(double hz)
    {
        short[] sound = FliteSpeechEngine.RenderWord("mama", hz, 1, 0);

        double measured = MedianPitch(sound);
        Assert.InRange(measured / hz, 0.97, 1.03);
    }

    [Fact]
    public void The_pauses_of_the_utterance_are_cut_away_so_the_word_starts_at_once()
    {
        short[] sound = FliteSpeechEngine.RenderWord("hello", 110, 1, 0);
        FliteRenderResult whole = FliteEngine.Default.Render("hello", new FliteOptions { F0Mean = 110, F0Stddev = 0 });

        Assert.True(sound.Length > 1_000, "the word has sound");
        Assert.True(sound.Length < whole.Samples.Length - 2_000, $"the word ({sound.Length}) is shorter than the utterance ({whole.Samples.Length}) by its pauses");
        Assert.Contains(sound[..800], sample => Math.Abs(sample) > 500);   // loud within 50 ms: no silence in front
    }

    [Fact]
    public void Both_ends_are_faded_so_that_the_cut_does_not_click()
    {
        short[] sound = FliteSpeechEngine.RenderWord("hello", 110, 1, 0);

        Assert.True(Math.Abs(sound[0]) < 300, $"first sample {sound[0]}");
        Assert.True(Math.Abs(sound[^1]) < 300, $"last sample {sound[^1]}");
    }

    [Fact]
    public void A_faster_rate_makes_the_word_shorter_in_proportion()
    {
        int slow = FliteSpeechEngine.RenderWord("remember", 110, 0.5, 0).Length;
        int normal = FliteSpeechEngine.RenderWord("remember", 110, 1, 0).Length;
        int fast = FliteSpeechEngine.RenderWord("remember", 110, 2, 0).Length;

        Assert.InRange((double)slow / normal, 1.8, 2.2);
        Assert.InRange((double)normal / fast, 1.8, 2.2);
    }

    [Fact]
    public void The_same_word_gives_the_same_samples_every_time()
    {
        short[] first = FliteSpeechEngine.RenderWord("sing", 196, 1, 0);
        short[] second = FliteSpeechEngine.RenderWord("sing", 196, 1, 0);

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_number_is_several_words_and_all_of_them_are_kept()
    {
        int one = FliteSpeechEngine.RenderWord("one", 110, 1, 0).Length;
        int number = FliteSpeechEngine.RenderWord("123", 110, 1, 0).Length;

        Assert.True(number > 2 * one, $"{number} samples for 'one hundred twenty three' against {one} for 'one'");
    }

    [Theory]
    [InlineData(49.9, 1)]
    [InlineData(480.1, 1)]
    [InlineData(double.NaN, 1)]
    [InlineData(110, 0.05)]
    [InlineData(110, 2.5)]
    public void A_pitch_or_rate_outside_the_range_is_refused(double hz, double rate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FliteSpeechEngine.RenderWord("hello", hz, rate, 0));
    }

    [Theory]
    [InlineData(25, 50)]
    [InlineData(49.9, 99.8)]
    [InlineData(50, 50)]
    [InlineData(261.63, 261.63)]
    [InlineData(480, 480)]
    [InlineData(480.5, 240.25)]
    [InlineData(523.25, 261.625)]
    [InlineData(1046.5, 261.625)]
    [InlineData(2093, 261.625)]
    [InlineData(3000, 375)]
    public void A_pitch_outside_the_range_is_folded_by_octaves_into_it(double asked, double sung)
    {
        (double hz, bool folded) = FliteSpeechEngine.FoldPitch(asked);

        Assert.Equal(sung, hz, 3);
        Assert.Equal(asked != sung, folded);
        Assert.InRange(hz, FliteSpeechEngine.WordsMinPitchHz, FliteSpeechEngine.WordsMaxPitchHz);
    }

    [Fact]
    public void A_fold_keeps_the_note_name_and_every_note_of_the_range_stays_where_it_is()
    {
        for (double hz = 50; hz <= 480; hz *= 1.05)
        {
            Assert.Equal(hz, FliteSpeechEngine.FoldPitch(hz).Hz, 9);
        }

        for (double asked = 5; asked < 20_000; asked *= 1.07)
        {
            double sung = FliteSpeechEngine.FoldPitch(asked).Hz;
            double octaves = Math.Log2(asked / sung);
            Assert.Equal(Math.Round(octaves), octaves, 9);   // whole octaves only
        }
    }

    [Fact]
    public void The_pitch_that_is_sung_follows_the_note_up_to_the_safe_end_of_the_range()
    {
        short[] low = FliteSpeechEngine.RenderWord("mama", 50, 1, 0);
        short[] high = FliteSpeechEngine.RenderWord("mama", 350, 1, 0);

        Assert.InRange(MedianPitch(low, 45, 60) / 50, 0.97, 1.03);
        Assert.InRange(MedianPitch(high, 300, 400) / 350, 0.97, 1.03);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("yellow")]
    [InlineData("four")]
    [InlineData("code")]
    public void The_words_that_click_at_the_top_of_the_range_are_sung_an_octave_lower_instead(string word)
    {
        short[] sound = FliteSpeechEngine.RenderSungWord(word, FliteSpeechEngine.WordsMaxPitchHz, 1, 0, out double sung);

        Assert.False(FliteSpeechEngine.HasWrapAround(sound), $"'{word}' clicks at {sung} Hz");
        Assert.True(sung == FliteSpeechEngine.WordsMaxPitchHz || sung == FliteSpeechEngine.WordsMaxPitchHz / 2, $"sung at {sung} Hz");
    }

    [Fact]
    public void The_plain_word_clicks_where_C_clicks_which_is_why_a_sung_word_is_checked()
    {
        // 'yes' wraps at 480 Hz in the C program too (plan/speech/flite/x1a-pitch-range.md); the port is the same, the check above is what keeps it out of a take
        Assert.True(FliteSpeechEngine.HasWrapAround(FliteSpeechEngine.RenderWord("yes", 480, 1, 0)));
        Assert.False(FliteSpeechEngine.HasWrapAround(FliteSpeechEngine.RenderWord("yes", 300, 1, 0)));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(2)]
    public void No_word_of_a_sentence_clicks_at_any_pitch_of_the_range(double rate)
    {
        string[] words = ["was", "island", "nguyen", "yes", "yellow", "four", "code", "water", "people", "because",
            "through", "again", "music", "voice", "sing", "love", "light", "night", "dream", "heart"];
        foreach (string word in words)
        {
            for (double hz = 300; hz <= FliteSpeechEngine.WordsMaxPitchHz; hz += 15)
            {
                short[] sound = FliteSpeechEngine.RenderSungWord(word, hz, rate, 0, out double sung);
                Assert.False(FliteSpeechEngine.HasWrapAround(sound), $"'{word}' at {hz} Hz (sung {sung}) at rate {rate}");
            }
        }
    }

    [Fact]
    public void A_word_that_needs_more_than_the_budget_is_refused()
    {
        Assert.Throws<SpeechResourceLimitException>(() => FliteSpeechEngine.RenderWord("remember", 110, 1, 500));
    }

    [Fact]
    public void A_cancelled_render_stops()
    {
        using CancellationTokenSource cancel = new();
        cancel.Cancel();

        Assert.Throws<OperationCanceledException>(() => FliteSpeechEngine.RenderWord("hello", 110, 1, 0, cancel.Token));
    }

    [Fact]
    public void Punctuation_without_a_word_is_silence()
    {
        Assert.Empty(FliteSpeechEngine.RenderWord("...", 110, 1, 0));
    }

    /// <summary>The median pitch in Hz of the voiced part: the lag of the best autocorrelation of 40 ms windows that are loud and periodic.</summary>
    private static double MedianPitch(short[] sound, double lowHz = 55, double highHz = 450)
    {
        const int rate = 16_000, window = 640, hop = 320;
        int minLag = (int)(rate / highHz), maxLag = (int)(rate / lowHz);
        List<double> found = [];
        for (int start = sound.Length / 5; start + window + maxLag < sound.Length * 9 / 10; start += hop)
        {
            double energy = 0;
            for (int i = 0; i < window; i++) energy += (double)sound[start + i] * sound[start + i];
            if (energy / window < 200 * 200) continue;
            double best = 0;
            int bestLag = 0;
            for (int lag = minLag; lag <= maxLag; lag++)
            {
                double sum = 0, other = 0;
                for (int i = 0; i < window; i++)
                {
                    sum += (double)sound[start + i] * sound[start + i + lag];
                    other += (double)sound[start + i + lag] * sound[start + i + lag];
                }

                double normalised = sum / Math.Sqrt(energy * other + 1);
                if (normalised > best) { best = normalised; bestLag = lag; }
            }

            if (best > 0.7) found.Add((double)rate / bestLag);
        }

        Assert.True(found.Count >= 3, $"only {found.Count} periodic windows");
        found.Sort();
        return found[found.Count / 2];
    }
}
