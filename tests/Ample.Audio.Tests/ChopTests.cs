using Ample.Core.Abstractions;
using Ample.Audio.Engine;

namespace Ample.Core.Tests;

public sealed class ChopTests
{
    private const int Rate = 22050;

    // three "words" of tones with a different level: 0.1-0.4 s, 0.7-1.0 s, 1.3-1.6 s, silence between them
    private static double[] Words()
    {
        double[] x = new double[(int)(2 * Rate)];
        foreach ((double from, double to, double level) in new[] { (0.1, 0.4, 4000.0), (0.7, 1.0, 8000.0), (1.3, 1.6, 12000.0) })
        {
            for (int i = (int)(from * Rate); i < (int)(to * Rate); i++)
            {
                x[i] = level * Math.Sin(2 * Math.PI * 300 * i / Rate);
            }
        }

        return x;
    }

    private static double Rms(double[] x, double from, double to) => Math.Sqrt(x.Skip((int)(from * Rate)).Take((int)((to - from) * Rate)).Average(v => v * v));

    [Fact]
    public void AutoSlicesFindTheWordsAndShortGapsDoNotSplit()
    {
        IReadOnlyList<int> starts = Chop.AutoSlices(Words(), Rate, 50, 150);
        Assert.Equal(3, starts.Count);
        Assert.InRange(starts[0] / (double)Rate, 0.05, 0.1);
        Assert.InRange(starts[1] / (double)Rate, 0.65, 0.7);
        // a gap of 500 ms closes the silences between the words: one slice
        Assert.Single(Chop.AutoSlices(Words(), Rate, 50, 700));
    }

    [Fact]
    public void PatternReadsStepsRestsAndRepeats()
    {
        IReadOnlyList<(int Slice, int Times)> pattern = Chop.ParsePattern("1 3 - 2*3", 3);
        Assert.Equal([(0, 1), (2, 1), (-1, 1), (1, 3)], pattern);
        Assert.Equal(3, Chop.ParsePattern("", 3).Count);
        Assert.Throws<AmpleException>(() => Chop.ParsePattern("1 4", 3));
        Assert.Throws<AmpleException>(() => Chop.ParsePattern("1*0", 3));
    }

    [Fact]
    public void SlicesLandOnTheGridInTheOrderOfThePattern()
    {
        double[] words = Words();
        IReadOnlyList<int> starts = Chop.AutoSlices(words, Rate, 50, 150);
        double beat = Rate / 2.0;   // 120 bpm
        // slice 3 (loud), then a rest, then slice 1 (quiet): steps of one beat
        double[] made = Chop.Process(words, starts, Chop.ParsePattern("3 - 1", 3), Rate, beat, new Chop.Settings(Step: 1, Gate: 0.9));
        Assert.Equal((int)(beat * 3), made.Length);
        double first = Rms(made, 0.05, 0.3), rest = Rms(made, 0.55, 0.95), third = Rms(made, 1.05, 1.3);
        Assert.True(first > 8000, $"slice 3 is loud: {first}");
        Assert.True(rest < 1, $"the rest is silent: {rest}");
        Assert.InRange(third, 2000, 6000);
    }

    [Fact]
    public void RepeatInsideAStepStuttersAndRepeatPlaysThePatternAgain()
    {
        double[] words = Words();
        IReadOnlyList<int> starts = Chop.AutoSlices(words, Rate, 50, 150);
        double beat = Rate / 2.0;
        double[] stutter = Chop.Process(words, starts, Chop.ParsePattern("2*2", 3), Rate, beat, new Chop.Settings(Step: 1, Gate: 0.5));
        // two short sounds in one beat (0 to 0.125 s and 0.25 to 0.375 s) with a gap between them
        Assert.True(Rms(stutter, 0.01, 0.11) > 4000);
        Assert.True(Rms(stutter, 0.14, 0.23) < 100);
        Assert.True(Rms(stutter, 0.26, 0.36) > 4000);
        double[] twice = Chop.Process(words, starts, Chop.ParsePattern("1", 3), Rate, beat, new Chop.Settings(Repeat: 2));
        Assert.Equal((int)(beat * 2), twice.Length);
    }

    [Fact]
    public void FitMakesASliceFillItsStep()
    {
        double[] words = Words();
        IReadOnlyList<int> starts = [(int)(0.7 * Rate), (int)(1.0 * Rate)];   // slice 1 is 0.3 s long, a step is 0.5 s
        double[] natural = Chop.Process(words, starts, Chop.ParsePattern("1", 2), Rate, Rate / 2.0, new Chop.Settings(Gate: 1, Fit: 0));
        double[] fitted = Chop.Process(words, starts, Chop.ParsePattern("1", 2), Rate, Rate / 2.0, new Chop.Settings(Gate: 1, Fit: 1));
        Assert.True(Rms(natural, 0.36, 0.5) < 100, "natural speed ends at 0.3 s");
        Assert.True(Rms(fitted, 0.36, 0.45) > 2000, "fitted runs to the end of the step");
    }
}
