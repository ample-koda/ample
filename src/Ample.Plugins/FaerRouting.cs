using Ample.Audio.Engine;
using Ample.Core.Abstractions;
using Ample.Storage.Projects;

namespace Ample.Plugins;

/// <summary>Applies a project's independent capability routes to discovered renderers.</summary>
public static class FaerRouting
{
    /// <summary>Replaces selected local ACE renderers with Faer renderers; unavailable routes fail explicitly.</summary>
    public static IReadOnlyList<IBlockRenderer> Apply(IReadOnlyList<IBlockRenderer> renderers, FaerRoutes routes, string jobsFolder) => Apply(renderers, routes, jobsFolder, FaerSpark.Local);

    /// <summary>
    /// Applies the routes; the ACE renderers that stay local keep the local server running while they work when the machine can start one. Every ACE block type also gets an
    /// <see cref="EngineRouter"/>, so a block can choose its own engine with its engine parameter even when all project routes are local.
    /// </summary>
    public static IReadOnlyList<IBlockRenderer> Apply(IReadOnlyList<IBlockRenderer> renderers, FaerRoutes routes, string jobsFolder, FaerSpark spark) => Apply(renderers, routes, jobsFolder, spark, FaerProfile.LoadLocal());

    /// <summary>The same with the Faer connection profile of this machine given (null: there is none).</summary>
    public static IReadOnlyList<IBlockRenderer> Apply(IReadOnlyList<IBlockRenderer> renderers, FaerRoutes routes, string jobsFolder, FaerSpark spark, FaerProfile? profile)
    {
        bool allLocal = routes.Song == "local" && routes.Voice == "local" && routes.Separation == "local";
        IReadOnlyList<IBlockRenderer> project = allLocal ? [.. renderers.Select(renderer => AddLease(renderer, spark))] : Route(renderers, routes, jobsFolder, spark, profile);
        return [.. project.Select((renderer, index) => renderers[index] is PluginRenderer plugin && EngineChoice.Applies(plugin.BlockType) ? Router(renderer, plugin, routes, profile, jobsFolder, spark) : renderer)];
    }

    // The renderer of one ACE block type with the variants a block can ask for; a variant is made when a block first asks for it.
    private static EngineRouter Router(IBlockRenderer project, PluginRenderer plugin, FaerRoutes routes, FaerProfile? profile, string jobsFolder, FaerSpark spark)
    {
        string projectRoute = plugin.BlockType == "ace-music" ? routes.Song : routes.Voice;
        string capability = plugin.BlockType switch { "ace-music" => "song", "ai-voice" => "voice-ace", _ => "voice-clone" };
        Dictionary<string, IBlockRenderer> made = [];
        Lock gate = new();
        IBlockRenderer Variant(string route)
        {
            lock (gate)
            {
                if (made.TryGetValue(route, out IBlockRenderer? existing))
                {
                    return existing;
                }

                IBlockRenderer local = AddLease(plugin, spark);
                IBlockRenderer built = route switch
                {
                    "local" => local,
                    "faer" => profile is null ? throw new AmpleException("AMP5380", $"The block asks for engine faer, but this machine has no Faer connection profile.") : new FaerPluginRenderer(plugin, profile, jobsFolder),
                    _ => profile is null ? local : new FaerElseLocalRenderer(new FaerPluginRenderer(plugin, profile, jobsFolder), local, profile, capability),
                };
                made[route] = built;
                return built;
            }
        }

        string? defaultModel = plugin.BlockType == "ace-music" ? routes.SongModel : plugin.BlockType == "ai-voice" ? routes.VoiceModel : null;
        return new EngineRouter(project, projectRoute, Variant, defaultModel);
    }

    // "faer-else-local" is a route of its own for the song and the voice: Faer when it is ready, else the local server.
    private static bool UsesFaer(string route) => route is "faer" or "faer-else-local";

    private static IReadOnlyList<IBlockRenderer> Route(IReadOnlyList<IBlockRenderer> renderers, FaerRoutes routes, string jobsFolder, FaerSpark spark, FaerProfile? profile)
    {
        if (routes.Song == "local" && routes.Voice == "local" && routes.Separation == "local") return renderers;
        if (UsesFaer(routes.Song) && !renderers.Any(renderer => renderer is PluginRenderer && renderer.BlockType == "ace-music")) throw new AmpleException("AMP5380", "The ACE song plugin is required for Faer song routing.");
        if (UsesFaer(routes.Voice) && !renderers.Any(renderer => renderer is PluginRenderer && renderer.BlockType == "ai-voice")) throw new AmpleException("AMP5380", "The ACE voice plugin is required for Faer voice routing.");
        return renderers.Select(renderer =>
        {
            if (renderer is not PluginRenderer plugin) return renderer;
            string route = renderer.BlockType == "ace-music" ? routes.Song : renderer.BlockType is ("ai-voice" or "voice-clone") ? routes.Voice : "local";
            if (route == "faer" && profile is null) return new MissingProfileRenderer(renderer);
            if (route == "local" || profile is null) return AddLease(renderer, spark);
            IBlockRenderer onFaer = new FaerPluginRenderer(plugin, profile, jobsFolder);
            if (route == "faer") return onFaer;
            string capability = renderer.BlockType switch { "ace-music" => "song", "ai-voice" => "voice-ace", _ => "voice-clone" };
            return new FaerElseLocalRenderer(onFaer, AddLease(renderer, spark), profile, capability);
        }).ToArray();
    }

    // A strict Faer route without a profile on this machine fails on the block that needs it, so local-only builds keep working. A block whose take is already in the cache is not rendered, so it does not fail here (its result exists); stem separation checks the profile itself when it runs (FaerSeparation).
    private sealed class MissingProfileRenderer(IBlockRenderer inner) : IBlockRenderer
    {
        public string BlockType => inner.BlockType;
        public string Id => inner.Id;
        public string Version => inner.Version;
        public string Determinism => inner.Determinism;
        public string ToolprintJson => inner.ToolprintJson;
        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken) =>
            throw new AmpleException("AMP5380", "This machine has no Faer connection profile.");
    }

    private static IBlockRenderer AddLease(IBlockRenderer renderer, FaerSpark spark) =>
        spark.CanStart && renderer is PluginRenderer && renderer.BlockType is ("ace-music" or "ai-voice" or "voice-clone") ? new SparkLeaseRenderer(renderer, spark) : renderer;
}
