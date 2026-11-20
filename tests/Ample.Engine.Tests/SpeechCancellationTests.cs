using System.Diagnostics;
using System.Text;
using Ample.Audio.Engine;
using Ample.Language;
using Xunit.Abstractions;

namespace Ample.Core.Tests;

/// <summary>
/// Stop during active work: the token is cancelled from inside the stage that is running (a deterministic hook, no timers), and the time from the
/// cancel to the exception is measured. A token that was cancelled before the call proves nothing about this and is not used here.
/// </summary>
[Trait("Category", "Engine")]
public sealed class SpeechCancellationTests(ITestOutputHelper output) : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-speech-cancel-").FullName;

    private static string Notes(int count) => "{\"notes\":[" + string.Join(",", Enumerable.Range(0, count).Select(i => FormattableString.Invariant(
        $"{{\"bar\":1,\"beat\":{i * 0.05},\"beats\":0.04,\"pitch\":{48 + i % 24},\"velocity\":120}}"))) + "]}";

    private RenderRequest WordsRequest(int words, Action<ProgressReport> report)
    {
        BlockNode block = new() { Type = "speech", Name = "voice" };
        block.Parameters.Add(new Parameter("mode", "words", 0));
        block.Parameters.Add(new Parameter("text", string.Join(' ', Enumerable.Repeat("robot", words)), 0));
        block.Parameters.Add(new Parameter("duration_policy", "gate", 0));
        return new(block, EngineRig.Spec.Templates["speech"], 48_000, 48_000 * 4, 1, 1, new HashSet<int> { 1 },
            new Dictionary<string, byte[]> { ["notes"] = Encoding.UTF8.GetBytes(Notes(words)) }, 17, folder, report);
    }

    private RenderRequest TextRequest(Action<ProgressReport> report)
    {
        BlockNode block = new() { Type = "speech", Name = "voice" };
        block.Parameters.Add(new Parameter("text", string.Join(' ', Enumerable.Repeat("the quick brown fox jumps over", 12)), 0));
        return new(block, EngineRig.Spec.Templates["speech"], 48_000, 48_000 * 16, 1, 1, new HashSet<int> { 1 }, new Dictionary<string, byte[]>(), 17, folder, report);
    }

    private static IBlockRenderer Speech() => BuiltInRenderers.All.Single(renderer => renderer.BlockType == "speech");

    /// <summary>Cancels when the stage is first reported and returns the milliseconds from the cancel to the exception.</summary>
    private static async Task<double> CancelAtAsync(Func<Action<ProgressReport>, RenderRequest> make, string stage)
    {
        using CancellationTokenSource source = new();
        Stopwatch watch = new();
        bool fired = false;
        RenderRequest request = make(report =>
        {
            if (fired || report.Step != stage) return;
            fired = true;
            watch.Start();
            source.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Speech().RenderAsync(request, source.Token));
        watch.Stop();
        Assert.True(fired, $"the stage {stage} was never reported, so nothing was cancelled inside it");
        return watch.Elapsed.TotalMilliseconds;
    }

    [Theory]
    [InlineData("words-planning")]
    [InlineData("words-synthesis")]
    [InlineData("speech-resampling")]
    public async Task Stop_inside_a_words_stage_ends_the_work_without_output(string stage)
    {
        double milliseconds = await CancelAtAsync(report => WordsRequest(40, report), stage);

        output.WriteLine($"words / {stage}: {milliseconds:0.0} ms from cancel to exception");
        Assert.True(milliseconds < 1000, $"stop took {milliseconds:0} ms");
    }

    [Fact]
    public async Task Stop_inside_text_synthesis_ends_the_work_without_output()
    {
        double milliseconds = await CancelAtAsync(report => TextRequest(report), "synthesis");

        output.WriteLine($"text / synthesis: {milliseconds:0.0} ms from cancel to exception");
        Assert.True(milliseconds < 1000, $"stop took {milliseconds:0} ms");
    }

    /// <summary>Cancels from another thread a few milliseconds after the stage started, while the inner loops run, and returns the stop latency.</summary>
    private static async Task<double> CancelFromAnotherThreadAsync(Func<Action<ProgressReport>, RenderRequest> make, string stage, int delayMilliseconds)
    {
        using CancellationTokenSource source = new();
        long cancelledAt = 0;
        int started = 0;
        RenderRequest request = make(report =>
        {
            if (report.Step != stage || Interlocked.Exchange(ref started, 1) == 1) return;
            _ = Task.Run(() =>
            {
                Thread.Sleep(delayMilliseconds);
                Volatile.Write(ref cancelledAt, Stopwatch.GetTimestamp());
                source.Cancel();
            });
        });

        try
        {
            await Task.Run(async () => await Speech().RenderAsync(request, source.Token));
            return -1;   // the work finished before the cancel: nothing was measured
        }
        catch (OperationCanceledException)
        {
            return Stopwatch.GetElapsedTime(Volatile.Read(ref cancelledAt)).TotalMilliseconds;
        }
    }

    [Fact]
    public async Task Stop_from_another_thread_while_the_inner_loops_run_is_prompt()
    {
        List<double> measured = [];
        foreach (int delay in new[] { 1, 5, 20, 50 })
        {
            double text = await CancelFromAnotherThreadAsync(report => TextRequest(report), "synthesis", delay);
            double words = await CancelFromAnotherThreadAsync(report => WordsRequest(60, report), "words-synthesis", delay);
            output.WriteLine($"cancel {delay} ms into the stage: text {text:0.0} ms, words {words:0.0} ms (-1 = finished first)");
            measured.AddRange([text, words]);
        }

        Assert.Contains(measured, value => value >= 0);   // at least one run was really cancelled in flight
        Assert.All(measured, value => Assert.True(value < 250, $"stop took {value:0} ms"));
    }

    [Fact]
    public async Task A_cancelled_build_does_not_touch_another_one_that_runs_at_the_same_time()
    {
        using CancellationTokenSource stopped = new();
        using ManualResetEventSlim running = new();
        Task<IReadOnlyList<RenderOutput>> first = Task.Run(async () => await Speech().RenderAsync(WordsRequest(40, report =>
        {
            if (report.Step == "words-synthesis") { running.Set(); stopped.Cancel(); }
        }), stopped.Token).AsTask());
        Assert.True(running.Wait(TimeSpan.FromSeconds(30)));
        IReadOnlyList<RenderOutput> second = await Speech().RenderAsync(WordsRequest(40, _ => { }), CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Contains(Wavs(second), sample => sample != 0);
    }

    private static short[] Wavs(IReadOnlyList<RenderOutput> outputs) => Wav.Decode(outputs[0].Data).Samples;

    public void Dispose() => Directory.Delete(folder, true);
}
