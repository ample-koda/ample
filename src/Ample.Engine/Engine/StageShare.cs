using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Ample.Audio.Engine;

namespace Ample.Engine.Engine;

/// <summary>
/// The stages of render recipes of one build: the source and every prefix of its effects, kept for the build so that two endpoints that share a stage render it once (a dry/wet or parallel-effects
/// layout renders its instrument one time, issue 298). A stage is identified by everything that decides its output: the renderers and blocks up to it, the seeds, the timing, the settings and the
/// bytes of the inputs, so a branch with another length, input, seed or setting gets its own render. Stages that are being made are shared by the ones that wait for them; the kept bytes are
/// bounded and a stage over the budget is simply not kept.
/// </summary>
internal sealed class StageShare(long budgetBytes)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<RenderOutput>>>> stages = new(StringComparer.Ordinal);
    private long held;
    private int renders;

    /// <summary>How many stages were really rendered (the others came from a stage that was rendered or being rendered by another endpoint).</summary>
    public int Renders => Volatile.Read(ref renders);

    /// <summary>Whether a request can share its stages: a request that has side effects on the caller (a probe, a forced render) or event data that is not part of the key renders alone.</summary>
    public static bool CanShare(RenderRequest request) => !request.ForceRender && request.Probe is null && request.PerformanceEvents is null && request.MidiTempoMap is null;

    /// <summary>The identity of what a request asks, without the block: the timing, the seed, the settings and a hash of every input.</summary>
    public static string RequestKey(RenderRequest request)
    {
        StringBuilder key = new();
        key.Append(request.SampleRate).Append('|').Append(request.SamplesPerBar).Append('|').Append(request.FirstBar).Append('|').Append(request.BarCount).Append('|');
        key.AppendJoin(',', request.ActiveBars.Order()).Append('|').Append(request.BeatsPerBar.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|');
        key.Append(request.MidiPpq).Append('|').Append(request.SampleChannel).Append('|').Append(request.ProjectRoot).Append('|').Append(request.AmpleRoot).Append('|');
        foreach (KeyValuePair<string, string> setting in (request.Settings ?? new Dictionary<string, string>()).OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            key.Append(setting.Key).Append('=').Append(setting.Value).Append(';');
        }

        key.Append('|');
        foreach (KeyValuePair<string, byte[]> input in request.Inputs.OrderBy(i => i.Key, StringComparer.Ordinal))
        {
            key.Append(input.Key).Append(':').Append(input.Value.Length).Append(':').Append(Convert.ToHexStringLower(SHA256.HashData(input.Value))).Append(';');
        }

        return key.ToString();
    }

    /// <summary>The outputs of a stage: rendered with <paramref name="render"/> unless another endpoint has rendered or is rendering the same stage.</summary>
    public async Task<IReadOnlyList<RenderOutput>> GetAsync(string key, Func<Task<IReadOnlyList<RenderOutput>>> render)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(render);
        Lazy<Task<IReadOnlyList<RenderOutput>>> entry = stages.GetOrAdd(key, _ => new Lazy<Task<IReadOnlyList<RenderOutput>>>(() => Run(key, render), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await entry.Value.ConfigureAwait(false);
        }
        catch
        {
            stages.TryRemove(new KeyValuePair<string, Lazy<Task<IReadOnlyList<RenderOutput>>>>(key, entry));   // a failed or cancelled stage is not kept
            throw;
        }
    }

    private async Task<IReadOnlyList<RenderOutput>> Run(string key, Func<Task<IReadOnlyList<RenderOutput>>> render)
    {
        Interlocked.Increment(ref renders);
        IReadOnlyList<RenderOutput> outputs = await render().ConfigureAwait(false);
        long size = outputs.Sum(o => (long)o.Data.Length);
        if (Interlocked.Add(ref held, size) > budgetBytes)
        {
            stages.TryRemove(key, out _);   // over the budget: the endpoints that wait for it still get it, a later one renders again
            Interlocked.Add(ref held, -size);
        }

        return outputs;
    }

    /// <summary>Lets go of everything (the build is over).</summary>
    public void Clear()
    {
        stages.Clear();
        Interlocked.Exchange(ref held, 0);
    }
}
