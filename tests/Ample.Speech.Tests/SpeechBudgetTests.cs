using System.Diagnostics;
using Xunit.Abstractions;

namespace Ample.Speech.Tests;

public sealed class SpeechBudgetTests(ITestOutputHelper output)
{
    private static string Words(int count) => string.Join(' ', Enumerable.Repeat("robot", count));

    [Fact]
    public void The_working_budget_is_charged_word_by_word_and_stops_planning_early()
    {
        // 20,000 internal samples is a few words; 5,000 words would take much longer to plan than to refuse
        Stopwatch watch = Stopwatch.StartNew();
        SpeechResourceLimitException error = Assert.Throws<SpeechResourceLimitException>(() =>
            HumanicoSpeechEngine.Render(new(Words(5_000), MaxWorkingSamples: 20_000)));
        watch.Stop();

        output.WriteLine($"refused after {watch.Elapsed.TotalMilliseconds:0.0} ms: {error.Message}");
        Assert.Contains("limit", error.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed.TotalMilliseconds < 2_000);
    }

    [Fact]
    public void Cumulative_frames_over_many_words_are_refused_even_when_each_word_fits()
    {
        // one "robot" fits in 20,000 internal samples, ten of them do not
        _ = HumanicoSpeechEngine.Render(new("robot", MaxWorkingSamples: 20_000));

        Assert.Throws<SpeechResourceLimitException>(() => HumanicoSpeechEngine.Render(new(Words(10), MaxWorkingSamples: 20_000)));
    }

    [Fact]
    public void Output_work_and_allocation_are_measured_separately()
    {
        // not an enforced ceiling: the numbers that define "working samples" (cumulative frames x 50), "output" (samples at the output rate)
        // and what the managed heap was asked for while rendering (an upper bound of the peak, not the peak itself)
        string text = Words(60);
        long before = GC.GetAllocatedBytesForCurrentThread();
        SpeechResult result = HumanicoSpeechEngine.Render(new(text, SampleRate: 48_000, Seed: 1));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long outputBytes = (long)result.Samples.Length * sizeof(short);

        output.WriteLine($"{text.Length} characters, {result.Samples.Length / 48_000.0:0.0} s of output ({outputBytes / 1024} KiB), allocated {allocated / 1024} KiB ({allocated / (double)Math.Max(1, outputBytes):0.0}x the output)");
        Assert.True(result.Samples.Length > 0);
        Assert.True(allocated > 0);
    }
}
