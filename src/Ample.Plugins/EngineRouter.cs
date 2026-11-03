using Ample.Audio.Engine;
using Ample.Language;

namespace Ample.Plugins;

/// <summary>
/// The renderer of an ACE block type (<c>ace-music</c>, <c>ai-voice</c>, <c>voice-clone</c>) that lets each block choose its own engine with the <c>engine</c> parameter (plan engine-per-block).
/// Without the parameter a block is made by the renderer the project routes decide; with it, by the place the block names. The build asks <see cref="RendererFor"/> before the plan is made,
/// so the chosen renderer's identity and the model it sets are part of the plan.
/// </summary>
public sealed class EngineRouter(IBlockRenderer project, string projectRoute, Func<string, IBlockRenderer> variant, string? defaultModel) : IBlockRenderer, IConfigurableRenderer, IBlockRouter
{
    /// <inheritdoc />
    public string BlockType => project.BlockType;

    /// <inheritdoc />
    public string Id => project.Id;

    /// <inheritdoc />
    public string Version => project.Version;

    /// <inheritdoc />
    public string Determinism => project.Determinism;

    /// <inheritdoc />
    public string ToolprintJson => project.ToolprintJson;

    /// <inheritdoc />
    public string SettingsKey => project is IConfigurableRenderer configurable ? configurable.SettingsKey : string.Empty;

    /// <summary>Gets the renderer the project routes decide, used when a block does not name an engine.</summary>
    public IBlockRenderer ProjectRenderer => project;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken) => project.RenderAsync(request, cancellationToken);

    /// <inheritdoc />
    public RoutedRenderer RendererFor(BlockNode block, IReadOnlyDictionary<string, string> songSettings)
    {
        EngineChoice? choice = EngineChoice.Parse(block.Parameters.FirstOrDefault(p => p.Name == "engine")?.Value, block.Type);
        IBlockRenderer chosen = choice is null || choice.Route == projectRoute ? project : variant(choice.Route);
        IReadOnlyDictionary<string, string> overrides = choice?.Settings() ?? new Dictionary<string, string>();
        if (overrides.Count == 0 && !string.IsNullOrWhiteSpace(defaultModel) && !songSettings.ContainsKey("synthModel") && !songSettings.ContainsKey("synthModelFile"))
        {
            // the project's default model applies when neither the block nor the song says which model to use
            overrides = EngineChoice.Parse("local:" + defaultModel, block.Type)?.Settings() ?? overrides;
        }

        return new RoutedRenderer(chosen, overrides);
    }
}
