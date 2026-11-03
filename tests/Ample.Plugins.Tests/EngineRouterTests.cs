using Ample.Core.Abstractions;
using Ample.Audio.Engine;
using Ample.Language;

namespace Ample.Plugins.Tests;

public sealed class EngineRouterTests
{
    private sealed class Stub(string id, string type = "ace-music") : IBlockRenderer, IConfigurableRenderer
    {
        public string BlockType => type;
        public string Id => id;
        public string Version => "1";
        public string SettingsKey => "acestep";
        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<RenderOutput>>([]);
    }

    private static BlockNode Block(string type, string? engine)
    {
        BlockNode block = new() { Type = type, Name = "b" };
        if (engine is not null)
        {
            block.Parameters.Add(new Parameter("engine", engine, 1));
        }

        return block;
    }

    private static readonly Dictionary<string, string> None = [];

    private static EngineRouter Router(IBlockRenderer project, string route, Func<string, IBlockRenderer>? variant = null, string? defaultModel = null) =>
        new(project, route, variant ?? (name => new Stub("variant." + name)), defaultModel);

    [Fact]
    public void Without_an_engine_the_project_renderer_makes_the_block_and_nothing_is_overridden()
    {
        Stub project = new("project");
        RoutedRenderer routed = Router(project, "local").RendererFor(Block("ace-music", null), None);
        Assert.Same(project, routed.Renderer);
        Assert.Empty(routed.Overrides);
    }

    [Fact]
    public void The_route_of_the_project_is_not_a_second_renderer()
    {
        Stub project = new("project");
        RoutedRenderer routed = Router(project, "faer").RendererFor(Block("ace-music", "faer:xl-sft"), None);
        Assert.Same(project, routed.Renderer);
        Assert.Equal("xl-sft", routed.Overrides["synthModel"]);
    }

    [Fact]
    public void Another_route_asks_for_that_variant_and_a_model_is_an_override()
    {
        List<string> asked = [];
        EngineRouter router = Router(new Stub("project"), "local", name => { asked.Add(name); return new Stub("variant." + name); });
        RoutedRenderer routed = router.RendererFor(Block("ai-voice", "faer:turbo"), None);
        Assert.Equal("variant.faer", routed.Renderer.Id);
        Assert.Equal(["faer"], asked);
        Assert.Equal("turbo", routed.Overrides["synthModel"]);
        Assert.Equal("variant.faer-else-local", router.RendererFor(Block("ai-voice", "faer-else-local"), None).Renderer.Id);
    }

    [Fact]
    public void The_project_default_model_applies_only_when_neither_the_block_nor_the_song_names_one()
    {
        EngineRouter router = Router(new Stub("project"), "local", defaultModel: "xl-sft");
        Assert.Equal("xl-sft", router.RendererFor(Block("ace-music", null), None).Overrides["synthModel"]);
        Assert.Empty(router.RendererFor(Block("ace-music", null), new Dictionary<string, string> { ["synthModelFile"] = "mine.gguf" }).Overrides);
        Assert.Empty(router.RendererFor(Block("ace-music", null), new Dictionary<string, string> { ["synthModel"] = "sft" }).Overrides);
        Assert.Equal("turbo", router.RendererFor(Block("ace-music", "local:turbo"), None).Overrides["synthModel"]);
    }

    [Fact]
    public void A_default_model_that_is_a_file_name_sets_the_synth_model_file()
    {
        EngineRouter router = Router(new Stub("project"), "local", defaultModel: "acestep-v15-xl-sft-Q8_0.gguf");
        Assert.Equal("acestep-v15-xl-sft-Q8_0.gguf", router.RendererFor(Block("ace-music", null), None).Overrides["synthModelFile"]);
    }

    [Fact]
    public void An_engine_that_is_not_valid_and_a_faer_that_cannot_be_reached_are_errors_not_fallbacks()
    {
        EngineRouter router = Router(new Stub("project"), "local", name => name == "faer" ? throw new AmpleException("AMP5380", "no Faer connection") : new Stub("variant." + name));
        Assert.Equal("AMP5383", Assert.Throws<AmpleException>(() => router.RendererFor(Block("ace-music", "cloud"), None)).DiagnosticCode);
        Assert.Equal("AMP5380", Assert.Throws<AmpleException>(() => router.RendererFor(Block("ace-music", "faer"), None)).DiagnosticCode);
        Assert.Equal("AMP5383", Assert.Throws<AmpleException>(() => router.RendererFor(Block("voice-clone", "faer:sft"), None)).DiagnosticCode);
    }

    [Fact]
    public void The_router_itself_acts_as_the_project_renderer()
    {
        Stub project = new("project");
        EngineRouter router = Router(project, "local");
        Assert.Equal("project", router.Id);
        Assert.Equal("ace-music", router.BlockType);
        Assert.Equal("acestep", router.SettingsKey);
        Assert.Same(project, router.ProjectRenderer);
    }
}
