namespace Ample.Speech;

/// <summary>
/// The shaping of a Flite render that AMPLE adds on top of the translated pipeline: it works on the finished samples and never inside the pipeline, so the exact render
/// (and its parity with the C program, see plan/speech/flite) is untouched when no option is written. The leading silence of the voice can be dropped, the pauses between
/// the words can be scaled, and after the resampling a high shelf can brighten the sound of the 16 kHz voice.
/// </summary>
internal static class FliteShaping
{
    /// <summary>Gets the length in samples at 16 kHz (1.5 ms) of the fade at every cut and join, so that a splice never clicks.</summary>
    public const int FadeSamples = 24;

    /// <summary>Gets the length in samples at 16 kHz (1 ms) of the silence that stays before the first word when the leading silence is dropped.</summary>
    public const int LeadKeepSamples = 16;

    /// <summary>Gets the corner frequency of the brightness shelf in Hz.</summary>
    public const double ShelfHz = 3_500;

    /// <summary>Drops the leading silence and scales the pauses between the words of a render.</summary>
    /// <param name="render">The exact render of the pipeline at 16 kHz.</param>
    /// <param name="leadTrim">True to drop the silence before the first word, down to <see cref="LeadKeepSamples"/>.</param>
    /// <param name="pauseScale">The factor of the pauses between the words: 1 leaves them, 0 removes them, 2 doubles them.</param>
    /// <returns>The render itself when nothing is asked; otherwise the shaped samples and the words with their new places.</returns>
    public static FliteRender Shape(FliteRender render, bool leadTrim, double pauseScale)
    {
        ArgumentNullException.ThrowIfNull(render);
        if ((!leadTrim && pauseScale == 1) || render.Words.Count == 0 || render.Samples.Length == 0) return render;
        short[] source = render.Samples;
        List<short> output = new(source.Length);
        List<FliteSpokenWord> words = new(render.Words.Count);
        int cursor = 0;
        if (leadTrim)
        {
            cursor = Math.Max(0, Math.Min(render.Words[0].StartSample - LeadKeepSamples, source.Length));
        }

        for (int i = 0; i < render.Words.Count; i++)
        {
            FliteSpokenWord word = render.Words[i];
            int start = Math.Clamp(word.StartSample, 0, source.Length), end = Math.Clamp(word.EndSample, start, source.Length);
            if (i > 0)
            {
                // the gap between the previous word and this one: the pause that Flite put there
                FliteSpokenWord previous = render.Words[i - 1];
                int gapStart = Math.Clamp(previous.EndSample, cursor, source.Length), gapEnd = Math.Max(gapStart, start);
                AppendGap(source, gapStart, gapEnd, pauseScale, output);
                cursor = gapEnd;
            }
            else
            {
                // from the cut (or the start) up to the first word
                Append(source, cursor, start, output);
                cursor = start;
            }

            int newStart = output.Count;
            Append(source, cursor, end, output);
            cursor = end;
            words.Add(word with { StartSample = newStart, EndSample = output.Count });
        }

        // whatever follows the last word (the trailing silence of the voice) stays as it was
        Append(source, cursor, source.Length, output);
        short[] shaped = [.. output];
        if (leadTrim) FadeIn(shaped, 0, Math.Min(FadeSamples, words[0].StartSample));
        return new(shaped, words, render.Breaks);
    }

    /// <summary>Puts the pauses of the markup into a render: each one is silence of exactly its length, between the end of the word before it and what follows
    /// (the pause that the voice made there stays).</summary>
    /// <param name="render">The shaped render, with its breaks.</param>
    /// <returns>The render itself when it has no breaks; otherwise the samples with the silences and the words at their new places.</returns>
    public static FliteRender InsertBreaks(FliteRender render)
    {
        ArgumentNullException.ThrowIfNull(render);
        if (render.Breaks is not { Count: > 0 } breaks || render.Words.Count == 0) return render with { Breaks = null };
        short[] source = render.Samples;
        List<short> output = new(source.Length + breaks.Sum(b => b.Samples));
        int[] shift = new int[render.Words.Count + 1];   // the samples inserted in front of each word (and after the last one)
        int cursor = 0;
        foreach (FliteBreak mark in breaks.OrderBy(b => b.BeforeWord))
        {
            int at = mark.BeforeWord == 0 ? 0 : Math.Clamp(render.Words[Math.Min(mark.BeforeWord, render.Words.Count) - 1].EndSample, cursor, source.Length);
            Append(source, cursor, at, output);
            cursor = at;
            int silence = Math.Max(0, mark.Samples);
            int fadeOut = Math.Min(FadeSamples, output.Count);
            FadeOut(output, output.Count, fadeOut);
            for (int i = 0; i < silence; i++) output.Add(0);
            for (int w = Math.Min(mark.BeforeWord, render.Words.Count); w < shift.Length; w++) shift[w] += silence;
            // the sound after the pause comes in with a short fade, as it does after a cut
            int resume = output.Count;
            Append(source, cursor, Math.Min(source.Length, cursor + FadeSamples), output);
            cursor = Math.Min(source.Length, cursor + FadeSamples);
            FadeInList(output, resume, Math.Min(FadeSamples, output.Count - resume));
        }

        Append(source, cursor, source.Length, output);
        List<FliteSpokenWord> words = new(render.Words.Count);
        for (int i = 0; i < render.Words.Count; i++) words.Add(render.Words[i] with { StartSample = render.Words[i].StartSample + shift[i], EndSample = render.Words[i].EndSample + shift[i] });
        return new([.. output], words);
    }

    /// <summary>Applies a high shelf (RBJ cookbook, slope 1) to samples: it adds <paramref name="gainDb"/> above <see cref="ShelfHz"/> and leaves the low end alone.</summary>
    /// <param name="samples">The samples at <paramref name="sampleRate"/>; changed in place.</param>
    /// <param name="sampleRate">The sample rate in Hz.</param>
    /// <param name="gainDb">The gain of the high end in dB; 0 leaves the samples as they are.</param>
    public static void Brighten(short[] samples, int sampleRate, double gainDb)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (gainDb == 0 || samples.Length == 0) return;
        // the power, the sine and the cosine come from DeterministicMath, never from the C library of the platform: the coefficients (and so the rounded samples) are the same bits everywhere
        double a = DeterministicMath.Exp10(gainDb / 40), w0 = 2 * Math.PI * ShelfHz / sampleRate, cos = DeterministicMath.Cos(w0);
        double alpha = DeterministicMath.Sin(w0) / 2 * Math.Sqrt(2), root = 2 * Math.Sqrt(a) * alpha;
        double b0 = a * ((a + 1) + (a - 1) * cos + root), b1 = -2 * a * ((a - 1) + (a + 1) * cos), b2 = a * ((a + 1) + (a - 1) * cos - root);
        double a0 = (a + 1) - (a - 1) * cos + root, a1 = 2 * ((a - 1) - (a + 1) * cos), a2 = (a + 1) - (a - 1) * cos - root;
        b0 /= a0; b1 /= a0; b2 /= a0; a1 /= a0; a2 /= a0;
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double x = samples[i];
            double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x; y2 = y1; y1 = y;
            samples[i] = (short)Math.Clamp(Math.Round(y, MidpointRounding.ToEven), short.MinValue, short.MaxValue);
        }
    }

    private static void Append(short[] source, int from, int to, List<short> output)
    {
        for (int i = from; i < to; i++) output.Add(source[i]);
    }

    /// <summary>Appends a pause of <paramref name="scale"/> times its length: the middle is cut out of a longer one, or silence is put into the middle of a shorter one.</summary>
    private static void AppendGap(short[] source, int from, int to, double scale, List<short> output)
    {
        int length = to - from, wanted = (int)Math.Round(length * scale, MidpointRounding.ToEven);
        if (length <= 0 || wanted == length)
        {
            Append(source, from, to, output);
            return;
        }

        int left = Math.Min(wanted, length) / 2, right = Math.Min(wanted, length) - left;
        int leftEnd = output.Count + left;
        Append(source, from, from + left, output);
        for (int i = length; i < wanted; i++) output.Add(0);
        int rightStart = output.Count;
        Append(source, to - right, to, output);
        // the join sits in the quiet of a pause; a short fade on both sides makes certain that no step is left
        FadeOut(output, leftEnd, Math.Min(FadeSamples, left));
        FadeInList(output, rightStart, Math.Min(FadeSamples, right));
    }

    private static void FadeOut(List<short> samples, int end, int length)
    {
        for (int i = 0; i < length; i++) samples[end - length + i] = (short)Math.Round(samples[end - length + i] * (1.0 - (i + 1.0) / (length + 1.0)), MidpointRounding.ToEven);
    }

    private static void FadeInList(List<short> samples, int start, int length)
    {
        for (int i = 0; i < length; i++) samples[start + i] = (short)Math.Round(samples[start + i] * ((i + 1.0) / (length + 1.0)), MidpointRounding.ToEven);
    }

    private static void FadeIn(short[] samples, int start, int length)
    {
        for (int i = 0; i < length; i++) samples[start + i] = (short)Math.Round(samples[start + i] * ((i + 1.0) / (length + 1.0)), MidpointRounding.ToEven);
    }
}
