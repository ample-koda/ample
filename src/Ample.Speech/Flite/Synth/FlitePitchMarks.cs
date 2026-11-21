namespace Ample.Speech.Flite.Synth;

// Port of Flite (CMU, commit 6c9f20dc), src/wavesynth/cst_units.c: f0_targets_to_pm. Translated to C# and modified: see FliteSynthesizer.cs.

/// <summary>One item of the Target relation: the pitch the voice has at a time.</summary>
/// <param name="Pos">The time in seconds (<c>pos</c>), a 32-bit float.</param>
/// <param name="F0">The pitch in Hz (<c>f0</c>), a 32-bit float.</param>
internal readonly record struct FliteTarget(float Pos, float F0);

/// <summary>The plan of the pitch marks: when, in output samples, every pitch period ends.</summary>
internal static class FlitePitchMarks
{
    /// <summary>The largest number of pitch marks the plan may hold. The C code has no limit; a target list that makes the time stand still would run for ever there.</summary>
    internal const int MaxMarks = 20_000_000;

    /// <summary>The times of the pitch marks in samples of the voice (<c>f0_targets_to_pm</c>). Every mark is the sum of the periods <c>1 / f0</c> of a pitch that is interpolated linearly between the targets.</summary>
    /// <param name="targets">The Target relation in order.</param>
    /// <param name="sampleRate">The sample rate of the voice.</param>
    internal static int[] Plan(IReadOnlyList<FliteTarget> targets, int sampleRate)
    {
        int count = Walk(targets, sampleRate, null);   // first pass: how many marks
        int[] times = new int[count];
        Walk(targets, sampleRate, times);              // second pass: the values
        return times;
    }

    private static int Walk(IReadOnlyList<FliteTarget> targets, int sampleRate, int[]? times)
    {
        float pos = 0, lpos = 0, f0 = 0;
        float lf0 = 120;   // hmm (the comment of the C code)
        int pm = 0;
        double time = 0;
        // the increment runs after a 'continue' too, as in the C for loop
        for (int t = 0; t < targets.Count; t++, lf0 = f0, lpos = pos)
        {
            pos = targets[t].Pos;
            f0 = targets[t].F0;
            if (time == pos) continue;
            float m = (f0 - lf0) / (pos - lpos);
            for (; time < pos; pm++)
            {
                if (pm >= MaxMarks) throw new InvalidOperationException("The pitch targets do not advance the time.");
                time += 1 / (lf0 + ((time - lpos) * m));
                if (times is not null) times[pm] = (int)(sampleRate * time);
            }
        }

        return pm;
    }
}
