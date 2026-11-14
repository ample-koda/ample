using Ample.Audio.Engine;
using Ample.Core.Abstractions;
using Ample.Language;
using Ample.Storage.Projects;

namespace Ample.Plugins.Tests;

public sealed class FaerRoutingTests : IDisposable
{
    private static readonly Dictionary<string, string> None = [];
    private readonly string folder = Directory.CreateTempSubdirectory("ample-faerrouting-").FullName;
    private readonly PluginHost host = new();

    public void Dispose()
    {
        host.Dispose();
        Directory.Delete(folder, recursive: true);
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

    private async Task<IReadOnlyList<IBlockRenderer>> Renderers() =>
        await PluginRenderer.CreateAsync(host, [Paths.Find("acestep")], Path.Combine(folder, "jobs"), "{}");

    [Fact]
    public async Task A_strict_Faer_route_without_a_profile_fails_on_the_block_that_needs_it_and_not_before()
    {
        IReadOnlyList<IBlockRenderer> renderers = await Renderers();
        FaerRoutes routes = new("faer", "local", "local");

        // applying the routes does not stop the build: a project that only uses local audio blocks must still build on a machine without a Faer profile
        IReadOnlyList<IBlockRenderer> routed = FaerRouting.Apply(renderers, routes, Path.Combine(folder, "jobs"), FaerSpark.Local, null);

        EngineRouter music = Assert.IsType<EngineRouter>(routed.Single(renderer => renderer.BlockType == "ace-music"));
        IBlockRenderer project = music.RendererFor(Block("ace-music", null), None).Renderer;
        AmpleException refused = await Assert.ThrowsAsync<AmpleException>(async () => await project.RenderAsync(null!, CancellationToken.None));
        Assert.Equal("AMP5380", refused.DiagnosticCode);
        Assert.Contains("no Faer connection profile", refused.Message, StringComparison.Ordinal);

        // a block that chooses its own engine is not held back by the route of the project
        IBlockRenderer own = music.RendererFor(Block("ace-music", "local"), None).Renderer;
        Assert.NotSame(project, own);

        // the voice route stays local: its blocks use the local renderer
        EngineRouter voice = Assert.IsType<EngineRouter>(routed.Single(renderer => renderer.BlockType == "ai-voice"));
        Assert.NotEqual(project.GetType(), voice.RendererFor(Block("ai-voice", null), None).Renderer.GetType());
    }

    [Fact]
    public async Task All_local_routes_do_not_need_a_profile()
    {
        IReadOnlyList<IBlockRenderer> routed = FaerRouting.Apply(await Renderers(), FaerRoutes.Default, Path.Combine(folder, "jobs"), FaerSpark.Local, null);
        Assert.All(routed, renderer => Assert.IsType<EngineRouter>(renderer));
    }
}
