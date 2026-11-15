using System.Security.Cryptography;
using Ample.Audio.Engine;
using Ample.Engine.Engine;

namespace Ample.Core.Tests;

/// <summary>One source feeding several effects: the readers share one blob and none writes into it (issue 295), and the timings of a cold, an unchanged and an edited build tell where the time went (issue 291).</summary>
[Trait("Category", "Engine")]
public sealed class BuildFanOutTests
{
    private sealed class Guard(IBlockRenderer inner) : IBlockRenderer
    {
        private readonly object gate = new();

        public List<(byte[] Input, string Before, string After)> Seen { get; } = [];

        public string BlockType => inner.BlockType;

        public string Id => inner.Id;

        public string Version => inner.Version;

        public string Determinism => inner.Determinism;

        public string ToolprintJson => inner.ToolprintJson;

        public async ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
        {
            byte[] input = request.Inputs["in"];
            string before = Convert.ToHexStringLower(SHA256.HashData(input));
            IReadOnlyList<RenderOutput> outputs = await inner.RenderAsync(request, cancellationToken);
            string after = Convert.ToHexStringLower(SHA256.HashData(input));
            lock (gate)
            {
                Seen.Add((input, before, after));
            }

            return outputs;
        }
    }

    private static string Song(string reverbMix = "30") =>
        "composition \"demo\" { ample 1  tempo 120  master-seed 7 }\nmidi-clip chords at 1 for 2 {\n  chords Am F\n}\n"
        + "piano keys at 1 for 2 {\n  from chords.notes -> notes\n}\n"
        + "reverb wet {\n  from keys.audio -> audio\n  mix " + reverbMix + "\n}\n"
        + "filter dark {\n  from keys.audio -> audio\n}\n"
        + "mixer mix {\n  from wet.audio -> audio1\n  from dark.audio -> audio2\n}\n"
        + "master master bake {\n  from mix.audio -> audio\n}\n";

    [Fact]
    public async Task Two_effects_that_read_the_same_take_get_the_same_array_and_neither_writes_into_it()
    {
        using EngineRig r = await EngineRig.CreateAsync();
        Guard reverb = new(BuiltInRenderers.All.Concat(AudioEffects.All).First(x => x.BlockType == "reverb"));
        Guard filter = new(BuiltInRenderers.All.Concat(AudioEffects.All).First(x => x.BlockType == "filter"));

        BuildReport report = await r.Engine.BuildAsync(EngineRig.Parse(Song()), new BuildOptions(Parallelism: 4, ExtraRenderers: [reverb, filter]));

        Assert.DoesNotContain(report.Blocks, b => b.Status == "failed");
        Assert.Single(reverb.Seen);
        Assert.Single(filter.Seen);
        Assert.All(reverb.Seen.Concat(filter.Seen), seen => Assert.Equal(seen.Before, seen.After));
        Assert.Same(reverb.Seen[0].Input, filter.Seen[0].Input);   // read from the take bank once, shared
    }

    [Fact]
    public async Task A_cold_an_unchanged_and_an_edited_build_report_what_was_rendered_and_how_long_it_took()
    {
        using EngineRig r = await EngineRig.CreateAsync();

        BuildReport cold = await r.Engine.BuildAsync(EngineRig.Parse(Song()));
        BuildReport unchanged = await r.Engine.BuildAsync(EngineRig.Parse(Song()));
        BuildReport edited = await r.Engine.BuildAsync(EngineRig.Parse(Song("60")));

        Assert.Equal(0, cold.Hits);
        Assert.True(cold.Misses >= 5);
        Assert.Equal(0, unchanged.Misses);   // nothing rendered: everything came from the take bank
        Assert.True(unchanged.Hits >= 5);
        Assert.Equal("cached", edited.Blocks.Single(b => b.Block == "keys").Status);   // the source is not made again
        Assert.Equal("rendered", edited.Blocks.Single(b => b.Block == "wet").Status);   // the edited effect and what follows it are
        Assert.Equal("cached", edited.Blocks.Single(b => b.Block == "dark").Status);
        Assert.All(new[] { cold, unchanged, edited }, report => Assert.True(report.ElapsedSeconds > 0));
        Assert.Contains("no block had to render", BuildTimings.Summarize(unchanged).Describe(), StringComparison.Ordinal);
        Assert.Contains("wet", BuildTimings.Summarize(edited).Describe(), StringComparison.Ordinal);
        BuildTimingSummary phases = BuildTimings.Summarize(cold);
        Assert.True(phases.RenderSeconds > 0, "the renderers worked");
        Assert.True(phases.StoreSeconds > 0, "the takes were written");
        Assert.All(cold.Blocks.Where(b => b.Seconds is > 0), b => Assert.True(b.LoadSeconds + b.RenderSeconds + b.StoreSeconds <= b.Seconds + 0.05, b.Block));
        Assert.Contains("reading inputs", phases.Describe(), StringComparison.Ordinal);
    }
}
