using Ample.Audio.Engine;
using Ample.Engine.Engine;
using Ample.Language;
using Ample.Storage.Takes;

namespace Ample.Core.Tests;

/// <summary>The engine parameter of the ACE blocks as the build sees it (plan engine-per-block): the router chooses the renderer and the model before the plan is made.</summary>
[Trait("Category", "Engine")]
public sealed class BlockRouterTests
{
    private sealed class Place(string id, List<IReadOnlyDictionary<string, string>?> seen) : IBlockRenderer, IConfigurableRenderer
    {
        public string BlockType => "ace-music";
        public string Id => id;
        public string Version => "1";
        public string SettingsKey => "acestep";
        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
        {
            seen.Add(request.Settings);
            return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("audio", Wav.Encode(new short[2000], 44100))]);
        }
    }

    private sealed class Router(IBlockRenderer local, IBlockRenderer faer) : IBlockRenderer, IConfigurableRenderer, IBlockRouter
    {
        public string BlockType => "ace-music";
        public string Id => local.Id;
        public string Version => local.Version;
        public string SettingsKey => "acestep";
        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken) => local.RenderAsync(request, cancellationToken);
        public RoutedRenderer RendererFor(BlockNode block, IReadOnlyDictionary<string, string> songSettings)
        {
            EngineChoice? choice = EngineChoice.Parse(block.Parameters.FirstOrDefault(p => p.Name == "engine")?.Value, block.Type);
            return new RoutedRenderer(choice?.Route == "faer" ? faer : local, choice?.Settings() ?? new Dictionary<string, string>());
        }
    }

    private const string Head = "composition \"demo\" { ample 1  tempo 120  master-seed 7 }\nsettings { acestep.backend server acestep.synthModel sft }\n";

    private static async Task<(BlockResult Result, string Renderer, string PlanId, IReadOnlyDictionary<string, string>? Settings)> BuildAsync(string block)
    {
        List<IReadOnlyDictionary<string, string>?> seen = [];
        using EngineRig r = await EngineRig.CreateAsync();
        BuildReport report = await r.Engine.BuildAsync(EngineRig.Parse(Head + block), new BuildOptions(ExtraRenderers: [new Router(new Place("test.local", seen), new Place("test.faer", seen))]));
        BlockResult result = report.Blocks.Single(b => b.Block == "tune");
        if (result.TakeId is null)
        {
            return (result, string.Empty, string.Empty, null);
        }

        StoredPlan stored = r.Bank.GetPlan(r.Bank.GetTake(result.TakeId).PlanId);
        return (result, stored.Plan.PluginId, stored.PlanId, seen.Single());
    }

    private static string Tune(string engine = "") => "ace-music tune at 1 for 1 {\n" + (engine.Length > 0 ? $"  engine \"{engine}\"\n" : string.Empty) + "}\n";

    [Fact]
    public async Task A_block_without_an_engine_uses_the_project_renderer_and_the_song_settings()
    {
        (_, string renderer, _, IReadOnlyDictionary<string, string>? settings) = await BuildAsync(Tune());
        Assert.Equal("test.local", renderer);
        Assert.Equal("sft", settings!["synthModel"]);
    }

    [Fact]
    public async Task A_block_that_asks_for_faer_is_made_by_the_faer_renderer_with_its_own_model_and_gets_its_own_plan()
    {
        (_, string plainRenderer, string plainPlan, _) = await BuildAsync(Tune());
        (_, string renderer, string plan, IReadOnlyDictionary<string, string>? settings) = await BuildAsync(Tune("faer:xl-sft"));
        Assert.Equal("test.local", plainRenderer);
        Assert.Equal("test.faer", renderer);
        Assert.Equal("xl-sft", settings!["synthModel"]);
        Assert.NotEqual(plainPlan, plan);
    }

    [Fact]
    public async Task The_model_of_the_block_wins_over_the_model_of_the_song()
    {
        (_, string renderer, _, IReadOnlyDictionary<string, string>? settings) = await BuildAsync(Tune("local:turbo"));
        Assert.Equal("test.local", renderer);
        Assert.Equal("turbo", settings!["synthModel"]);
        Assert.Equal("server", settings["backend"]);
    }

    [Fact]
    public async Task A_family_of_the_block_wins_over_a_model_file_of_the_song()
    {
        List<IReadOnlyDictionary<string, string>?> seen = [];
        using EngineRig r = await EngineRig.CreateAsync();
        string song = "composition \"demo\" { ample 1  tempo 120  master-seed 7 }\nsettings { acestep.backend server acestep.synthModelFile song-sft.gguf }\n" + Tune("local:turbo");
        await r.Engine.BuildAsync(EngineRig.Parse(song), new BuildOptions(ExtraRenderers: [new Router(new Place("test.local", seen), new Place("test.faer", seen))]));
        IReadOnlyDictionary<string, string> settings = seen.Single()!;
        Assert.Equal("turbo", settings["synthModel"]);
        Assert.DoesNotContain("synthModelFile", settings.Keys);
    }

    [Fact]
    public async Task A_model_file_of_the_block_is_passed_on_as_the_synth_model_file()
    {
        (_, _, _, IReadOnlyDictionary<string, string>? settings) = await BuildAsync(Tune("faer:acestep-v15-xl-sft-Q8_0.gguf"));
        Assert.Equal("acestep-v15-xl-sft-Q8_0.gguf", settings!["synthModelFile"]);
    }

    [Fact]
    public async Task An_engine_that_is_not_valid_stops_that_block_with_AMP5383()
    {
        (BlockResult result, _, _, _) = await BuildAsync(Tune("cloud:xl"));
        Assert.Equal("failed", result.Status);
        Assert.Contains("AMP5383", result.Message, StringComparison.Ordinal);
    }
}
