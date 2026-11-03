using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Audio.Engine;
using Ample.Audio.Random;
using Ample.Storage.Takes;
using Ample.Language;

namespace Ample.Engine.Engine;

/// <summary>An immutable source recipe and its effects, in execution order.</summary>
public sealed record RenderChain(BlockNode Source, IReadOnlyList<BlockNode> Effects)
{
    /// <summary>Resolves a render-connected effect without materializing intermediate audio.</summary>
    public static RenderChain? Resolve(BlockNode target, AmpleDocument document, SpecData spec)
    {
        if (!target.Froms.Where(f => !f.Disabled).Any(f => f.Input == "render_in")) return null;
        List<BlockNode> effects = [];
        HashSet<string> visited = new(StringComparer.Ordinal);
        BlockNode current = target;
        while (current.Froms.Where(f => !f.Disabled).FirstOrDefault(f => f.Input == "render_in") is { } input)
        {
            if (!visited.Add(current.Name)) throw new AmpleException("AMP5250", "The render recipe contains a cycle.");
            if (current.Froms.Where(f => !f.Disabled).Any(f => f.Input == "in")) throw new AmpleException("AMP2020", "An effect accepts audio or render input, not both.");
            if (!AudioEffects.All.Any(r => r.BlockType == current.Type)) throw new AmpleException("AMP5251", "The block cannot process a render recipe.");
            effects.Add(current);
            current = document.Blocks.FirstOrDefault(b => b.Name == input.Block) ?? throw new AmpleException("AMP5251", "The render source is missing.");
            if (!spec.Templates[current.Type].Outputs.Any(p => p.Name == input.Output && p.Type == "render")) throw new AmpleException("AMP2007", "The recipe must come from a render output.");
        }
        if (!visited.Add(current.Name)) throw new AmpleException("AMP5250", "The render recipe contains a cycle.");
        effects.Reverse();
        return new RenderChain(current, effects.AsReadOnly());
    }

    /// <summary>True when this node only forwards a recipe in the current build.</summary>
    public static bool IsDeferred(BlockNode block, AmpleDocument document, SpecData spec, IReadOnlySet<string> wanted, string? target)
    {
        if (block.Name == target || block.Flags.Contains("disabled")) return false;
        FromLine[] consumers = [.. document.Blocks.Where(b => wanted.Contains(b.Name)).SelectMany(b => b.Froms.Where(f => !f.Disabled)).Where(f => f.Block == block.Name)];
        return consumers.Length > 0 && consumers.All(f => spec.Templates[block.Type].Outputs.Any(p => p.Name == f.Output && p.Type == "render"));
    }

    /// <summary>Gets a stable identity of the source and ordered effects.</summary>
    public string Fingerprint() => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', new[] { Source }.Concat(Effects).Select(BuildEngine.Fingerprint)))));
}

internal sealed class RenderChainRenderer(IBlockRenderer source, RenderChain chain, SpecData spec, IReadOnlyDictionary<string, IBlockRenderer> renderers, ulong masterSeed, IBuildControl? control, string? overrideBlock, ulong? overrideSeed) : IBlockRenderer, IConfigurableRenderer
{
    public string BlockType => source.BlockType;
    public string Id => "builtin.render-chain/" + source.Id;
    public string Version => "1";
    public string Determinism => source.Determinism;
    public string SettingsKey => source is IConfigurableRenderer configurable ? configurable.SettingsKey : string.Empty;
    public IReadOnlyList<PlanSeed> EffectSeeds => [.. chain.Effects.Select(effect => new PlanSeed("effect:" + effect.Name, SeedFor(effect)))];
    public string ToolprintJson => JsonSerializer.Serialize(new
    {
        source = new { source.Id, source.Version, source.ToolprintJson, defaults = spec.Templates[chain.Source.Type].Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray() },
        effects = chain.Effects.Select(e => new { e.Name, renderer = renderers[e.Type].Id, version = renderers[e.Type].Version, toolprint = renderers[e.Type].ToolprintJson, defaults = spec.Templates[e.Type].Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(), parameters = e.Parameters.Select(p => new { p.Name, p.Value }).ToArray() }).ToArray(),
    });

    private ulong SeedFor(BlockNode effect)
    {
        if (BuildEngine.IsSeedFree(effect.Type)) return 0;   // the audio effects never read a seed, so a new master seed does not make the chain a new take
        Parameter? typed = effect.Parameters.FirstOrDefault(p => p.Name == "seed" && ulong.TryParse(p.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _));
        return overrideBlock == effect.Name && overrideSeed is not null ? overrideSeed.Value : control?.SeedFor(effect.Name) ?? (typed is null ? SeedDeriver.Derive(masterSeed, "block", effect.Name, "seed", "seed") : ulong.Parse(typed.Value, CultureInfo.InvariantCulture));
    }

    public async ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<RenderOutput> outputs = await source.RenderAsync(request, cancellationToken).ConfigureAwait(false);
        List<JsonElement> metadata = [.. outputs.Where(o => o.MetadataJson is not null).Select(o => JsonSerializer.Deserialize<JsonElement>(o.MetadataJson!))];
        string sourcePort = request.Template.Outputs.First(p => p.Type == "audio").Name;
        byte[] audio = outputs.FirstOrDefault(o => o.Role == sourcePort)?.Data ?? throw new AmpleException("AMP5251", "The render source produced no audio.");
        foreach (BlockNode effect in chain.Effects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TemplateSpec template = spec.Templates[effect.Type];
            ulong seed = SeedFor(effect);
            outputs = await renderers[effect.Type].RenderAsync(request with { Block = effect, Template = template, Inputs = new Dictionary<string, byte[]> { ["in"] = audio }, Seed = seed }, cancellationToken).ConfigureAwait(false);
            metadata.AddRange(outputs.Where(o => o.MetadataJson is not null).Select(o => JsonSerializer.Deserialize<JsonElement>(o.MetadataJson!)));
            string port = template.Outputs.First(p => p.Type == "audio").Name;
            audio = outputs.FirstOrDefault(o => o.Role == port)?.Data ?? throw new AmpleException("AMP5251", "A render effect produced no audio.");
        }
        return metadata.Count == 0 ? outputs : outputs.Select(o => o with { MetadataJson = JsonSerializer.Serialize(new { stages = metadata }) }).ToArray();
    }
}
