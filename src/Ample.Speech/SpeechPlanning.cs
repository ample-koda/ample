namespace Ample.Speech;

/// <summary>Immutable duration and timing plan shared by preflight and rendering.</summary>
public sealed record SpeechPlan(IReadOnlyList<string> Words, IReadOnlyList<IReadOnlyList<SpeechPhone>> PhoneWords, int WordFrames, IReadOnlyList<int> WordFrameCounts, IReadOnlyList<int> WordSampleOffsets, int InternalSampleRate, int InternalSamplesPerFrame, int InternalSamples, int OutputSamples, IReadOnlyList<SpeechWordTiming> Timings);

public static class SpeechPlanner
{
    public static IReadOnlyList<SpeechFrame> PlanWord(string word, double pitchHz, double rate) =>
        PlanPhones(HumanicoFrontEnd.WordToPhones(word), pitchHz, rate);

    public static IReadOnlyList<SpeechFrame> PlanWord(string word, double pitchHz, double rate, CancellationToken cancellationToken) =>
        PlanPhones(HumanicoFrontEnd.WordToPhones(word), pitchHz, rate, cancellationToken);

    public static IReadOnlyList<SpeechFrame> PlanPhones(IReadOnlyList<SpeechPhone> phones, double pitchHz, double rate) =>
        HumanicoFramePlanner.Plan(HumanicoSegmentPlanner.PlanPhones(phones), pitchHz, rate);

    public static IReadOnlyList<SpeechFrame> PlanPhones(IReadOnlyList<SpeechPhone> phones, double pitchHz, double rate, CancellationToken cancellationToken) =>
        HumanicoFramePlanner.Plan(HumanicoSegmentPlanner.PlanPhones(phones, cancellationToken), pitchHz, rate, cancellationToken);

    public static SpeechPlan Plan(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Engine == "flite") return FliteSpeechEngine.Plan(request, cancellationToken);   // Flite decides its own durations: the plan is the render
        string[] words = request.Phonemes is { Length: > 0 } ? ["[phonemes]"] : [.. SpeechText.Words(request.Text)];
        IReadOnlyList<IReadOnlyList<SpeechPhone>> phoneWords = request.Phonemes is { Length: > 0 }
            ? [HumanicoFrontEnd.ParsePhones(request.Phonemes)]
            : [.. words.Select(HumanicoFrontEnd.WordToPhones)];
        cancellationToken.ThrowIfCancellationRequested();
        const int sampleRate = 10_000, samplesPerFrame = 50;
        List<int> wordFrameCounts = [];
        long plannedFrames = 0;
        foreach (IReadOnlyList<SpeechPhone> phones in phoneWords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // the budget is cumulative and is charged word by word, before the next word is planned: a word may use only what is left,
            // so no large allocation is made for work that is already over the limit
            long framesLeft = request.MaxWorkingSamples > 0 ? Math.Max(0, request.MaxWorkingSamples / samplesPerFrame - plannedFrames) : 0;
            if (request.MaxWorkingSamples > 0 && framesLeft == 0)
                throw new SpeechResourceLimitException($"Speech work needs more than {request.MaxWorkingSamples} internal samples, exceeding the configured limit.");
            int frames = request.Engine == "humanico"
                ? HumanicoCalibratedVoice.PlanPhones(phones, request.Rate, (int)Math.Min(int.MaxValue, framesLeft), cancellationToken).Count
                : PlanPhones(phones, request.PitchHz, request.Rate, cancellationToken).Count;
            plannedFrames = checked(plannedFrames + frames);
            if (request.MaxWorkingSamples > 0 && plannedFrames * samplesPerFrame > request.MaxWorkingSamples)
                throw new SpeechResourceLimitException($"Speech work needs {plannedFrames * samplesPerFrame} internal samples, exceeding the configured limit.");
            wordFrameCounts.Add(frames);
        }
        int wordFrames = wordFrameCounts.Count == 0 ? 0 : wordFrameCounts.Max();
        List<int> wordSampleOffsets = [];
        long internalSamples = 0;
        foreach (int frames in wordFrameCounts)
        {
            wordSampleOffsets.Add(checked((int)internalSamples));
            internalSamples = checked(internalSamples + (long)frames * samplesPerFrame);
        }
        if (request.MaxWorkingSamples > 0 && internalSamples > request.MaxWorkingSamples)
            throw new SpeechResourceLimitException($"Speech work needs {internalSamples} internal samples, exceeding the configured limit.");
        long outputSamples = checked((internalSamples * request.SampleRate + sampleRate / 2) / sampleRate);
        if (outputSamples > int.MaxValue || request.MaxOutputSamples > 0 && outputSamples > request.MaxOutputSamples)
            throw new SpeechResourceLimitException($"Speech output needs {outputSamples} samples, exceeding the configured limit.");
        List<SpeechWordTiming> timings = [];
        for (int i = 0; i < words.Length; i++)
        {
            int start = wordSampleOffsets[i];
            int end = checked(start + wordFrameCounts[i] * samplesPerFrame);
            timings.Add(new(i, Scale(start, request.SampleRate), Scale(end, request.SampleRate), words[i]));
        }
        return new(words, phoneWords, wordFrames, wordFrameCounts, wordSampleOffsets, sampleRate, samplesPerFrame, checked((int)internalSamples), checked((int)outputSamples), timings);
    }

    private static int Scale(int sample, int outputRate) => checked((int)(((long)sample * outputRate + 10_000 / 2) / 10_000));
}
