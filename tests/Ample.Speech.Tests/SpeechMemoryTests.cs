using Xunit.Abstractions;

namespace Ample.Speech.Tests;

/// <summary>The estimate that Remix reserves must stay above what a render really allocates, for both engines and a long text.</summary>
public sealed class SpeechMemoryTests(ITestOutputHelper output)
{
    private static string Words(int count) => string.Join(' ', Enumerable.Repeat("the quick brown fox jumps over", count / 6 + 1).SelectMany(sentence => sentence.Split(' ')).Take(count));

    [Theory]
    [InlineData("humanico", 110)]   // about a minute of speech
    [InlineData("retro", 110)]
    [InlineData("humanico", 12)]
    public void The_estimate_is_above_what_a_render_allocates(string engine, int words)
    {
        // warm up (dictionary, tables), so that the one-off loading is not counted as the cost of the render
        _ = HumanicoSpeechEngine.Render(new("hello", 44_100) { Engine = engine });
        long before = GC.GetAllocatedBytesForCurrentThread();
        SpeechResult result = HumanicoSpeechEngine.Render(new(Words(words), 44_100, Seed: 3) { Engine = engine });
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long estimate = SpeechMemory.EstimateBytes(result.Samples.Length);

        output.WriteLine($"{engine}, {words} words: {result.Samples.Length / 44_100.0:0.0} s, allocated {allocated / 1048576.0:0.0} MiB, estimate {estimate / 1048576.0:0.0} MiB ({allocated / (double)(result.Samples.Length * 2):0.0}x the output)");
        Assert.True(allocated <= estimate, $"{engine} allocated {allocated} bytes, the estimate says {estimate}");
    }

    [Theory]
    [InlineData(110)]   // about a minute of speech
    [InlineData(12)]
    public void The_estimate_is_above_what_a_flite_render_allocates(int words)
    {
        // the first render loads the data of the voice (a few MiB, once per process): it is not the cost of a render
        _ = FliteSpeechEngine.Render(new("hello", 44_100) { Engine = "flite" });
        long before = GC.GetAllocatedBytesForCurrentThread();
        SpeechResult result = FliteSpeechEngine.Render(new(Words(words), 44_100) { Engine = "flite" });
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long estimate = SpeechMemory.EstimateBytes(result.Samples.Length);

        double times = allocated / (double)(result.Samples.Length * 2L);
        double seconds = result.Samples.Length / 44_100.0, mib = allocated / 1048576.0, estimateMib = estimate / 1048576.0;
        output.WriteLine($"flite, {words} words: {seconds:0.0} s, allocated {mib:0.0} MiB, estimate {estimateMib:0.0} MiB ({times:0.0} times the output)");
        Assert.True(allocated <= estimate, $"flite allocated {allocated} bytes, the estimate says {estimate}");
    }

    [Fact]
    public void The_estimate_is_the_output_in_bytes_times_the_factor()
    {
        Assert.Equal(0, SpeechMemory.EstimateBytes(0));
        Assert.Equal(1000L * 2 * SpeechMemory.AllocationFactor, SpeechMemory.EstimateBytes(1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeechMemory.EstimateBytes(-1));
    }
}
