using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ample.Core.Abstractions;
using Ample.Audio.Engine;
using Ample.Audio.Instruments;
using Ample.Audio.Midi;
using Ample.Audio.Random;
using Ample.Storage.Takes;
using Ample.Language;

namespace Ample.Engine.Engine;

/// <summary>A range of bars to render (a preview of part of the song).</summary>
/// <param name="FirstBar">First bar (1-based).</param>
/// <param name="Bars">Number of bars.</param>
public sealed record BarRange(int FirstBar, int Bars);

/// <summary>Options of a build.</summary>
/// <param name="Intent"><c>bake</c> (full quality) or <c>preview</c> (low sample rate, optional range).</param>
/// <param name="Range">Only these bars (previews).</param>
/// <param name="Only">Build only this block and what it needs.</param>
/// <param name="Parallelism">How many blocks may render at the same time: 0 for no wish (the project's number, else the cores); a number is a wish that the project and the server can only lower (see <see cref="BuildParallelism"/>).</param>
/// <param name="ExtraRenderers">Renderers added to the built-in ones (plugins, tests).</param>
/// <param name="Progress">Called with a block name and status when a block is done.</param>
/// <param name="Control">Seeds, selected takes and pinned melodies (freeze ladder); null for none.</param>
/// <param name="Override">A one-block override (variant seed, intent, forced render, parent plan).</param>
/// <param name="Detail">Called while blocks are queued and render, with how far each block is (and its sub-steps).</param>
public sealed record BuildOptions(string Intent = "bake", BarRange? Range = null, string? Only = null, int Parallelism = 0, IReadOnlyList<IBlockRenderer>? ExtraRenderers = null, Action<string, string>? Progress = null, IBuildControl? Control = null, BlockOverride? Override = null, Action<BlockProgress>? Detail = null)
{
    /// <summary>Gets unsaved project module sources to use for this build, keyed by relative project path.</summary>
    public IReadOnlyDictionary<string, string>? ModuleSources { get; init; }

    /// <summary>Gets whether to only work out the plan of every block and look for its take (a dry run): nothing is rendered or stored, a block without a take is <c>stale</c>.</summary>
    public bool PlanOnly { get; init; }

    /// <summary>
    /// Gets a hand-over of saved audio (plan bundles BU02): asked for a block that has no take yet, it may return the outputs of that block, which are then stored as its take
    /// instead of rendering. Used when a library bundle is placed, so a generative block sounds exactly as it was saved.
    /// </summary>
    public Func<string, IReadOnlyList<RenderOutput>?>? Adopt { get; init; }

    /// <summary>Gets a recorder of the sample files and SoundFont presets the blocks use. With it the build renders every block again (no take is reused), so the record is complete.</summary>
    public UsageTrace? Trace { get; init; }
}

/// <summary>One sub-step of a block and how far it is (null: busy, end unknown).</summary>
/// <param name="Name">The name of the step.</param>
/// <param name="Fraction">How far it is from 0 to 1, or null.</param>
public sealed record StepProgress(string Name, double? Fraction);

/// <summary>How far a block is while a build runs.</summary>
/// <param name="Block">The block name.</param>
/// <param name="State"><c>queued</c> or <c>running</c>.</param>
/// <param name="Fraction">The whole block from 0 to 1.</param>
/// <param name="Steps">The sub-steps the renderer reported, in order.</param>
public sealed record BlockProgress(string Block, string State, double Fraction, IReadOnlyList<StepProgress> Steps);

/// <summary>What the freeze ladder tells the engine.</summary>
public interface IBuildControl
{
    /// <summary>Gets the seed to use for a block, or null for the derived default.</summary>
    ulong? SeedFor(string block);

    /// <summary>Gets the take selected for a block, or null.</summary>
    string? SelectedTake(string block);

    /// <summary>Gets the data id of the melody plan pinned for a block (<c>freeze plan</c>), or null.</summary>
    string? PinnedMelodyDataId(string block);
}

/// <summary>A one-block override of a build.</summary>
/// <param name="Block">The block.</param>
/// <param name="Seed">Seed to use instead of the block's seed.</param>
/// <param name="Intent">Plan intent for this block (for example <c>variant</c> or <c>verify</c>).</param>
/// <param name="Force">Render even when a take for the plan exists (to check reproduction).</param>
/// <param name="ParentPlanId">The plan this one regenerates.</param>
/// <param name="Parameters">Temporary parameter values for a variant; they are included in its render plan and never written to the song.</param>
/// <param name="ParameterOriginals">The source values for the temporary variant fields.</param>
/// <param name="VariantIndex">The stable zero-based position in a variant request.</param>
public sealed record BlockOverride(string Block, ulong? Seed, string? Intent, bool Force, string? ParentPlanId, IReadOnlyDictionary<string, string>? Parameters = null, IReadOnlyDictionary<string, string>? ParameterOriginals = null, int? VariantIndex = null);

/// <summary>What happened to one block.</summary>
/// <param name="Block">Block name.</param>
/// <param name="Status"><c>cached</c>, <c>rendered</c>, <c>deferred</c>, <c>skipped</c>, <c>failed</c> or <c>cancelled</c>.</param>
/// <param name="TakeId">The take, when there is one.</param>
/// <param name="Message">Why it was skipped or failed.</param>
public sealed record BlockResult(string Block, string Status, string? TakeId, string? Message);

/// <summary>The outcome of a build.</summary>
/// <param name="Blocks">Per block, in build order.</param>
/// <param name="MasterTakeId">The take of the master block (or the last audio block), if built.</param>
/// <param name="Hits">Blocks served from the cache.</param>
/// <param name="Misses">Blocks that had to be rendered.</param>
public sealed record BuildReport(IReadOnlyList<BlockResult> Blocks, string? MasterTakeId, int Hits, int Misses);

/// <summary>Builds a song: orders the blocks by their connections, and for each block either finds the take of an identical plan (cache hit) or stores the plan first (the Always rule), renders and commits a take. The cache key is the plan id, which includes the source of the block, the takes of its inputs, its seeds, the renderer and its settings, so changing one block re-renders it and what depends on it, and nothing else.</summary>
public sealed class BuildEngine(Takebank bank, SpecData spec, Guid projectId, string projectRoot, string? ampleRoot, Takebank? readOnlyCache)
{
    // What a disabled source does to an input is decided here and only here: a required input disables the block (and says so with AMP5253), an optional one is just left out.
    // A later template flag per input (for example onMissing: silence | error) plugs into NeedsInput without touching the callers.
    private static bool NeedsInput(TemplateSpec template, string input) => template.Inputs.Any(p => p.Name == input && p.Required);

    /// <summary>
    /// The length of an unplaced sequencer (no placement, no <c>bars</c> parameter): the end of the last clip of its playlist, so that a long performance is not cut at the default of 8 bars.
    /// Null when the block is placed, has a length of its own, or its sequence cannot be read.
    /// </summary>
    private static int? PlaylistBars(string projectRoot, BlockNode block)
    {
        if (block.Type != "sequencer" || block.Parts.Count > 0 || block.Parameters.Any(p => p.Name == "bars"))
        {
            return null;
        }

        string? embedded = block.Parameters.FirstOrDefault(p => p.Name == "sequence")?.Value;
        string file = block.Parameters.FirstOrDefault(p => p.Name == "file")?.Value.Trim().Trim('"') ?? string.Empty;
        try
        {
            string text;
            if (!string.IsNullOrWhiteSpace(embedded))
            {
                text = embedded;
            }
            else if (file.Length > 0 && Ample.Audio.IO.ProjectPath.Resolve(projectRoot, file) is { } full && File.Exists(full))
            {
                text = File.ReadAllText(full);
            }
            else
            {
                return null;
            }

            SequenceFile seq = SequenceFile.Parse(text);
            return seq.Clips.Count == 0 ? null : seq.Clips.Max(c => c.At + c.Bars - 1);
        }
        catch (AmpleException)
        {
            return null;
        }
    }

    private static string? FileParameter(string projectRoot, BlockNode block, string name)
    {
        string value = block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"') ?? string.Empty;
        if (value.Length == 0) return null;
        string full = Ample.Audio.IO.ProjectPath.Resolve(projectRoot, value);
        return File.Exists(full) ? full : null;
    }

    /// <summary>The recording a block takes as reference: the voice to clone (voice-clone, parameter file) or the voice an ACE voice should come close to (ai-voice, parameter voice_sample).</summary>
    private static string? ReferenceFile(string projectRoot, BlockNode block) => block.Type switch
    {
        "voice-clone" => FileParameter(projectRoot, block, "file"),
        // a recording linked to the reference port wins over the file parameter
        "ai-voice" => block.Froms.Any(link => link.Input == "reference" && !link.Disabled) ? null : FileParameter(projectRoot, block, "voice_sample"),
        _ => null,
    };

    private static string ImplicitlyDisabled(string input, string upstream) => $"AMP5253: implicitly disabled, its required input '{input}' comes from '{upstream}', which is disabled.";

    // Blocks whose renderer never reads its seed: their plan carries seed 0, so changing the master seed (or the block name) does not make them a new take.
    private static readonly HashSet<string> SeedFree = new(StringComparer.Ordinal)
    {
        "midi-clip", "midi", "midi-render", "mixer", "bus", "master", "group", "lyrics", "mood-scene", "splitter", "synth", "click", "import-audio", "sampler", "send", "volume", "duck", "scratch", "pan", "fade", "crossfade", "sequencer",
    };

    /// <summary>Gets whether a block type never reads its seed (the plan then carries seed 0, whatever the master seed is).</summary>
    public static bool IsSeedFree(string blockType) => SeedFree.Contains(blockType) || ControlBlocks.Types.Contains(blockType) || blockType is "trim" or "silence" || AudioEffects.All.Any(r => r.BlockType == blockType) || (ExtraEffects.Types.Contains(blockType) && blockType is not ("granular" or "ambience")) || (MidiEffects.All.Any(r => r.BlockType == blockType) && blockType is not ("midi-humanize" or "arpeggiator"));

    /// <summary>Gets whether the seed of a block follows the master seed: it has no custom seed, is not locked (<paramref name="locked"/>) and its type reads a seed.</summary>
    public static bool FollowsMasterSeed(BlockNode block, bool locked) => !locked && !IsSeedFree(block.Type) && !block.Parameters.Any(p => p.Name == "seed" && ulong.TryParse(p.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _));

    private const int FullRate = 44100;
    /// <summary>
    /// The sample rate of a preview build (Play preview): lower than a full build so a preview renders fast. 22050 Hz keeps sound up to 11 kHz (cymbals, attacks);
    /// 11025 Hz sounded dull and hissed (it was the rate until 2026-11-02). To change it, change this number and PREVIEW_RATE in web/lib/preview-rate.mjs
    /// (the browser preview renders at the same rate; a test checks that the two agree).
    /// </summary>
    public const int PreviewRate = 22050;
    private static readonly Regex MidiModuleUse = new(@"^\s*use\s+(?<module>[A-Za-z0-9_-]+)(?:@[^\s{]+)?\s+as\s+(?<instance>[A-Za-z0-9_-]+)\s*\{", RegexOptions.CultureInvariant);
    private static readonly Regex MidiModuleHeader = new(@"^# midi-source version=1 format=[01] ppq=(\d+)\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex MidiSourceEvent = new(@"^\s*# source-event track=(\d+) tick=(\d+) kind=(\w+) channel=(\d+) data1=(\d+) data2=(\d+) meta=(none|[0-9A-Fa-f]{2}) payload=([0-9A-Fa-f]*)\s*$", RegexOptions.CultureInvariant);

    private sealed record ModuleMidiSource(string Module, string Instance, string Path, string Hash, int Ppq, IReadOnlyList<(int Track, SmfEvent Event)> Events, IReadOnlyList<SmfTempo> Tempos);

    /// <summary>Creates an engine for a direct CLI or test build without a separately supplied AMPLE root.</summary>
    public BuildEngine(Takebank bank, SpecData spec, Guid projectId, string projectRoot) : this(bank, spec, projectId, projectRoot, null) { }

    /// <summary>Creates an engine that can reuse exact-plan takes from an additional read-only cache bank.</summary>
    public BuildEngine(Takebank bank, SpecData spec, Guid projectId, string projectRoot, string? ampleRoot) : this(bank, spec, projectId, projectRoot, ampleRoot, null) { }

    private sealed class MidiTempoTimeline
    {
        private readonly SmfTempo[] tempos;
        private readonly BigInteger[] elapsed;

        public MidiTempoTimeline(IReadOnlyList<SmfTempo> source)
        {
            tempos = [.. source];
            elapsed = new BigInteger[tempos.Length];
            for (int i = 1; i < tempos.Length; i++) elapsed[i] = elapsed[i - 1] + (BigInteger)(tempos[i].Tick - tempos[i - 1].Tick) * tempos[i - 1].MicrosecondsPerQuarter;
        }

        public int Sample(long tick, int ppq, int sampleRate, int samplesPerBar, int firstBar, int maximumSamples)
        {
            int low = 0, high = tempos.Length - 1;
            while (low < high)
            {
                int mid = low + (high - low + 1) / 2;
                if (tempos[mid].Tick <= tick) low = mid; else high = mid - 1;
            }
            BigInteger time = elapsed[low] + (BigInteger)(tick - tempos[low].Tick) * tempos[low].MicrosecondsPerQuarter;
            BigInteger sample = time * sampleRate / ((BigInteger)ppq * 1_000_000) - (BigInteger)(firstBar - 1) * samplesPerBar;
            return sample <= 0 ? 0 : sample >= maximumSamples ? maximumSamples : (int)sample;
        }
    }

    /// <summary>Gets the length of the song in seconds as a build would use it (the base of the size and time estimate).</summary>
    public double SongSeconds(AmpleDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        List<BlockNode> blocks = [.. doc.Blocks.Where(b => !b.Skipped && spec.Templates.ContainsKey(b.Type) && b.Class.Length > 0)];
        return SongBars(blocks, b => PlaylistBars(projectRoot, b)) * 4 * 60.0 / (doc.Project?.Tempo ?? 92);
    }

    /// <summary>Builds the document.</summary>
    public async Task<BuildReport> BuildAsync(AmpleDocument doc, BuildOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(doc);
        options ??= new BuildOptions();
        bool preview = options.Intent == "preview";
        int rate = preview ? PreviewRate : FullRate;
        double tempo = doc.Project?.Tempo ?? 92;
        long tempoMilli = (long)(tempo * 1000 + 0.5);
        Dictionary<string, ModuleMidiSource> midiModules = ReadMidiModules(doc, tempoMilli, options.ModuleSources);
        Dictionary<string, IBlockRenderer> renderers = [];
        foreach (IBlockRenderer r in BuiltInRenderers.All.Concat(options.ExtraRenderers ?? []))
        {
            renderers[r.BlockType] = r;
        }

        List<BlockNode> blocks = [.. doc.Blocks.Where(b => !b.Skipped && spec.Templates.ContainsKey(b.Type) && b.Class.Length > 0)];
        HashSet<string> wanted = options.Only is null ? [.. blocks.Select(b => b.Name)] : Needed(blocks, options.Only);
        List<List<BlockNode>> levels = Levels(blocks.Where(b => wanted.Contains(b.Name)).ToList());

        int songBars = SongBars(blocks, b => PlaylistBars(projectRoot, b));
        BarRange range = options.Range ?? new BarRange(1, songBars);
        int spb = FixedAudio.SamplesPerBar(rate, tempoMilli, doc.Project?.MeterNumerator ?? 4, doc.Project?.MeterDenominator ?? 4);
        ConcurrentDictionaryLike results = new();
        List<BlockResult> report = [];
        using SemaphoreSlim gate = new(BuildParallelism.Effective(options.Parallelism, BuildParallelism.OfProject(projectRoot)));
        foreach (BlockNode queued in levels.SelectMany(l => l))
        {
            options.Detail?.Invoke(new BlockProgress(queued.Name, "queued", 0, []));
        }

        for (int levelIndex = 0; levelIndex < levels.Count; levelIndex++)
        {
            List<BlockNode> level = levels[levelIndex];
            Task<BlockResult>[] tasks = [.. level.Select(async b =>
            {
                try { await gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return new BlockResult(b.Name, "cancelled", null, "The build was cancelled before this block started."); }
                try
                {
                    if (b.Flags.Contains("disabled")) return new BlockResult(b.Name, "disabled", null, "This block is disabled.");
                    if (b.Type is "mixer" or "bus" && b.Froms.All(f => f.Disabled || results.Get(f.Block)?.Status == "disabled")) return new BlockResult(b.Name, "disabled", null, "No active mixer inputs.");
                    if (b.Froms.Any(f => f.Disabled) && !b.Froms.Any(f => !f.Disabled) && b.Type is not ("mixer" or "bus")) return new BlockResult(b.Name, "disabled", null, "No active connections.");
                    FromLine? disabledInput = b.Froms.FirstOrDefault(f => (f.Disabled || results.Get(f.Block)?.Status == "disabled") && (NeedsInput(spec.Templates[b.Type], f.Input) || spec.Templates[b.Type].Inputs.Any(p => p.Name == "render_in") && f.Input is "in" or "render_in"));
                    if (disabledInput is not null) return new BlockResult(b.Name, "disabled", null, ImplicitlyDisabled(disabledInput.Input, disabledInput.Block));
                    if (RenderChain.IsDeferred(b, doc, spec, wanted, options.Only)) return new BlockResult(b.Name, "deferred", null, null);
                    try { return await BuildBlockAsync(b, doc, renderers, results, preview, rate, spb, range, songBars, tempoMilli, midiModules, options.Intent, options, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return new BlockResult(b.Name, "cancelled", null, "The build was cancelled before this block completed."); }
                }
                finally
                {
                    gate.Release();
                }
            })];
            List<Task<BlockResult>> pending = [.. tasks];
            while (pending.Count > 0)
            {
                Task<BlockResult> finished = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(finished);
                if (finished.IsCompletedSuccessfully) options.Progress?.Invoke(finished.Result.Block, finished.Result.Status);
            }
            BlockResult[] done = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (BlockResult r in done)
            {
                results.Set(r);
                report.Add(r);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                for (int remaining = levelIndex + 1; remaining < levels.Count; remaining++)
                {
                    foreach (BlockNode block in levels[remaining])
                    {
                        FromLine? lost = block.Froms.FirstOrDefault(f => !f.Disabled && (results.Get(f.Block)?.Status is "cancelled" or "failed") && (NeedsInput(spec.Templates[block.Type], f.Input) || (spec.Templates[block.Type].Inputs.Any(p => p.Name == "render_in") && f.Input is ("in" or "render_in"))));
                        BlockResult result = lost is null
                            ? new BlockResult(block.Name, "cancelled", null, "The build was cancelled before this block started.")
                            : new BlockResult(block.Name, "failed", null, $"Required input '{lost.Input}' from '{lost.Block}' did not complete because the build was cancelled.");
                        results.Set(result);
                        report.Add(result);
                        options.Progress?.Invoke(result.Block, result.Status);
                    }
                }
                break;
            }
        }

        BlockNode? master = blocks.LastOrDefault(b => b.Type == "master") ?? blocks.LastOrDefault(b => spec.Templates[b.Type].Outputs.Any(o => o.Type == "audio"));
        return new BuildReport(report, master is null ? null : results.Get(master.Name)?.TakeId, report.Count(r => r.Status is "cached" or "frozen" or "adopted"), report.Count(r => r.Status is "rendered" or "failed" or "cancelled"));
    }

    private async Task<BlockResult> BuildBlockAsync(BlockNode block, AmpleDocument doc, Dictionary<string, IBlockRenderer> renderers, ConcurrentDictionaryLike results, bool preview, int rate, int spb, BarRange range, int songBars, long tempoMilli, IReadOnlyDictionary<string, ModuleMidiSource> midiModules, string intent, BuildOptions options, CancellationToken cancellationToken)
    {
        BlockNode owner = block;
        RenderChain? recipe = RenderChain.Resolve(block, doc, spec);
        BlockOverride? ov = options.Override is { } requested && requested.Block == owner.Name ? requested : null;
        if (ov?.Parameters is { Count: > 0 } parameterOverrides)
        {
            if (recipe is null) block = WithParameters(block, parameterOverrides);
            else recipe = recipe with { Source = recipe.Source.Name == owner.Name ? WithParameters(recipe.Source, parameterOverrides) : recipe.Source, Effects = [.. recipe.Effects.Select(effect => effect.Name == owner.Name ? WithParameters(effect, parameterOverrides) : effect)] };
        }
        if (recipe is not null)
        {
            if (new[] { recipe.Source }.Concat(recipe.Effects).Any(b => b.Flags.Contains("disabled"))) return new BlockResult(owner.Name, "disabled", null, "The render recipe contains a disabled block.");
            block = recipe.Source;
        }
        block = ResolveSing(block, doc);
        block = ApplyBusSolo(block, doc);
        // a sequencer that is not placed is as long as its playlist, not the default of 8 bars, and so is every unplaced block that follows it
        int localBars = BlockTiming.Bars(block, doc, [], b => PlaylistBars(projectRoot, b));
        block = BlockTiming.Local(block);
        TemplateSpec template = spec.Templates[block.Type];
        if (block.Flags.Contains("disabled")) return new BlockResult(block.Name, "disabled", null, "AMP5254: this block is disabled.");
        if (!renderers.TryGetValue(block.Type, out IBlockRenderer? renderer) || (template.Dynamic && !renderers.ContainsKey(block.Type)))
        {
            return new BlockResult(owner.Name, "skipped", null, $"AMP5251: there is no renderer for '{block.Type}' blocks.");
        }

        // a router (the ACE blocks) picks the renderer and the model for this block from its engine parameter; the plan below then holds that choice
        Dictionary<string, string>? engineSettings = null;
        if (renderer is IBlockRouter router)
        {
            string routerPrefix = (renderer is IConfigurableRenderer routed ? routed.SettingsKey : string.Empty) + ".";
            try
            {
                RoutedRenderer choice = router.RendererFor(block, doc.Settings.Where(s => s.Key.StartsWith(routerPrefix, StringComparison.Ordinal)).ToDictionary(s => s.Key[routerPrefix.Length..], s => s.Value, StringComparer.Ordinal));
                renderer = choice.Renderer;
                engineSettings = choice.Overrides.Count > 0 ? new Dictionary<string, string>(choice.Overrides, StringComparer.Ordinal) : null;
            }
            catch (AmpleException ex)
            {
                // an engine that is not valid, or Faer asked for without a connection: this block fails with the reason, the rest of the song still builds
                return new BlockResult(owner.Name, "failed", null, ex.DiagnosticCode + ": " + ex.Message);
            }
        }

        if (recipe is not null) renderer = new RenderChainRenderer(renderer, recipe, spec, renderers, (ulong)(doc.Project?.MasterSeed ?? 0), options.Control, ov?.Block, ov?.Seed);
        if (template.Inputs.Any(p => p.Name == "render_in") && !block.Froms.Any(f => f.Input is "in" or "render_in" or "region_in")) return new BlockResult(owner.Name, "skipped", null, "AMP5255: the effect has no audio or render input connected, so it is left out of the build.");

        if (ov is null && owner.Flags.Contains("bake") && options.Control?.SelectedTake(owner.Name) is string selected)
        {
            TakeRead frozen = bank.ReadTake(selected);
            return frozen.Missing.Count > 0
                ? new BlockResult(owner.Name, "failed", selected, "AMP5221: the frozen take's data is not on disk; restore it from a backup or re-render it on purpose.")
                : new BlockResult(owner.Name, "frozen", selected, null);
        }

        SortedDictionary<string, (string TakeId, string Role)> sources = new(StringComparer.Ordinal);
        FromLine? dropped = null;
        Dictionary<string, byte[]> inputs = [];
        foreach (FromLine f in block.Dependencies().Where(f => !f.Disabled))
        {
            BlockResult? up = results.Get(f.Block);
            if (up?.TakeId is null)
            {
                if (up?.Status == "disabled")
                {
                    if (NeedsInput(template, f.Input)) return new BlockResult(owner.Name, "disabled", null, ImplicitlyDisabled(f.Input, f.Block));
                    continue;
                }

                if (up?.Status is "failed" or "cancelled" && NeedsInput(template, f.Input))
                    return new BlockResult(owner.Name, "failed", null, $"Required input '{f.Input}' from '{f.Block}' did not complete ({up.Status}).");

                // a dry run cannot know the plan of a block whose input has no take yet: it waits for that block
                if (options.PlanOnly && up?.Status is "stale" or "waiting") return new BlockResult(owner.Name, "waiting", null, $"waits for '{f.Block}'");
                // a skipped block (an optional input of a component that is not connected) only takes away what it would have given an optional input, such as a mix of a few sounds, and does not stop the mix
                if (up?.Status == "skipped" && !NeedsInput(template, f.Input)) { dropped ??= f; continue; }

                return new BlockResult(block.Name, "skipped", null, $"AMP5251: its input '{f.Input}' comes from '{f.Block}', which has no take.");
            }

            TakeRecord take = bank.GetTake(up.TakeId);
            TakeDataRef? exact = take.Data.FirstOrDefault(d => d.Role == f.Output);
            if (exact is null && doc.Blocks.FirstOrDefault(b => b.Name == f.Block)?.Type == "sequencer") return new BlockResult(block.Name, "skipped", null, $"AMP5251: its input '{f.Input}' comes from '{f.Block}.{f.Output}', which carries no channels: name them in the lanes of '{f.Block}' (for example lanes lane1=kick).");
            TakeDataRef data = exact ?? take.Data[0];
            byte[] input = [];
            if (!options.PlanOnly)
            {
                await using Stream s = bank.OpenData(data.DataId);
                using MemoryStream ms = new();
                await s.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                input = ms.ToArray();
            }

            BlockNode? upstream = doc.Blocks.FirstOrDefault(b => b.Name == f.Block);
            if (!options.PlanOnly && BlockTiming.IsTimeline(owner) && upstream is not null && !BlockTiming.IsArranged(upstream, doc, []) && spec.Templates[upstream.Type].Outputs.Any(p => p.Name == f.Output && p.Type == "audio")) input = BlockTiming.Place(input, BlockTiming.Origin(upstream, doc, []), range, spb);
            string slot = f.Input == "region_in" ? "in" : f.Input;   // a region goes through an effect as the sound it is
            inputs[slot] = input;
            sources[slot] = (up.TakeId, data.Role);
        }

        // every input was a skipped block: there is nothing to mix, so this block is skipped too
        if (dropped is not null && inputs.Count == 0) return new BlockResult(block.Name, "skipped", null, $"AMP5251: its input '{dropped.Input}' comes from '{dropped.Block}', which has no take.");

        if (ReferenceFile(projectRoot, block) is { } voiceSample)
        {
            if (!options.PlanOnly) inputs["reference"] = await File.ReadAllBytesAsync(voiceSample, cancellationToken).ConfigureAwait(false);
        }

        List<PlanInput> pinned = [];
        if (BlockTiming.IsTimeline(owner))
        {
            string placement = string.Join('\n', owner.Froms.Where(f => !f.Disabled).Select(f => $"{f.Input}:{BlockTiming.Origin(doc.Blocks.First(b => b.Name == f.Block), doc, [])}"));
            pinned.Add(new PlanInput("placement", "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(placement))), null));
        }
        ModuleMidiSource? moduleMidi = FindMidiModule(block, midiModules);
        if (moduleMidi is not null) pinned.Add(new PlanInput("midi-events", "sha256:" + moduleMidi.Hash, moduleMidi.Path));
        string? melodyId = block.Freeze == "plan" ? options.Control?.PinnedMelodyDataId(block.Name) : null;
        if (melodyId is not null)
        {
            if (!options.PlanOnly)
            {
                await using Stream melodyStream = bank.OpenData(melodyId);
                using MemoryStream melody = new();
                await melodyStream.CopyToAsync(melody, cancellationToken).ConfigureAwait(false);
                inputs["melody"] = melody.ToArray();
            }

            pinned.Add(new PlanInput("data", melodyId, null));
        }

        // The samples a block plays are part of its plan: change a sample and the block is a new take; the plan says which files were used.
        if (SampleSet.FolderOf(projectRoot, ampleRoot, block) is { } sampleFolder)
        {
            pinned.AddRange(SampleSet.Hashes(projectRoot!, ampleRoot, sampleFolder).Select(h => new PlanInput("sample", h.Hash, SamplePlanPath(sampleFolder, h.Name))));
            if (SampleSet.SettingsHash(projectRoot!, ampleRoot, sampleFolder) is { } settingsHash)
            {
                pinned.Add(new PlanInput("sample", settingsHash, $"{sampleFolder}/{SampleSet.SettingsFile}"));
            }

            // Kits named by the voice map or as the fallback are part of the plan as well.
            foreach (string extra in SampledInstruments.ExtraKitFolders(block).Where(f => f != sampleFolder))
            {
                try
                {
                    pinned.AddRange(SampleSet.Hashes(projectRoot!, ampleRoot, extra).Select(h => new PlanInput("sample", h.Hash, SamplePlanPath(extra, h.Name))));
                }
                catch (Exception ex) when (ex is AmpleException or IOException)
                {
                    // A kit that is not there is reported by the renderer with its own message.
                }
            }
        }

        static string SamplePlanPath(string folder, string name)
        {
            if (!SampleSet.IsSfz(folder))
            {
                return $"{folder}/{name}.wav";
            }

            int slash = folder.LastIndexOfAny(['/', '\\']);
            return slash < 0 ? name : $"{folder[..slash]}/{name}";
        }

        // A sequencer reads its sequence file and the samples, loops and sample sets of its channels: all of them are part of the plan.
        if (block.Type == "sequencer")
        {
            foreach (string sequenceFile in SequencerFiles.Of(projectRoot!, ampleRoot, block))
            {
                pinned.Add(new PlanInput("file", "sha256:" + FileHashCache.Sha256Hex(sequenceFile), Path.GetRelativePath(projectRoot!, sequenceFile).Replace('\\', '/')));
            }
        }

        // The lyrics file a block reads is part of its plan too, so editing the words in the lyrics editor makes a new take.
        if (BuiltInRenderers.LyricsFile(projectRoot, block) is { } lyricsFile)
        {
            pinned.Add(new PlanInput("file", "sha256:" + FileHashCache.Sha256Hex(lyricsFile), Path.GetRelativePath(projectRoot!, lyricsFile).Replace('\\', '/')));
        }

        // an audio file a block plays (import-audio) is part of its plan, so a new recording or another file makes a new take
        if (block.Type is "import-audio" or "sampler" or "convolution" or "ambience" && ImportAudioRenderer.FileOf(projectRoot, block, ampleRoot) is { } audioFile)
        {
            pinned.Add(new PlanInput("sample", "sha256:" + FileHashCache.Sha256Hex(audioFile), Path.GetRelativePath(projectRoot!, audioFile).Replace('\\', '/')));
        }

        if (ReferenceFile(projectRoot!, block) is { } voiceSampleFile)
        {
            pinned.Add(new PlanInput("sample", "sha256:" + FileHashCache.Sha256Hex(voiceSampleFile), Path.GetRelativePath(projectRoot!, voiceSampleFile).Replace('\\', '/')));
        }

        if (block.Type == "midi" && BuiltInRenderers.MidiFile(projectRoot!, block) is { } midiFile)
        {
            pinned.Add(new PlanInput("file", "sha256:" + Convert.ToHexStringLower(SHA256.HashData(BuiltInRenderers.ReadMidiFile(midiFile))), Path.GetRelativePath(projectRoot!, midiFile).Replace('\\', '/')));
        }

        if (block.Type is "gm" or "midi-render" or "midi")
        {
            string soundFontRef = block.Type == "midi" ? SoundFontAsset.GeneralMidi : block.Parameters.FirstOrDefault(p => p.Name == "file")?.Value.Trim().Trim('"') ?? template.Parameters.GetValueOrDefault("file") ?? string.Empty;
            if (soundFontRef.Length > 0)
            {
                string soundFont = SoundFontAsset.Resolve(projectRoot!, ampleRoot, soundFontRef);
                if (File.Exists(soundFont))
                {
                    FileInfo info = new(soundFont);
                    if (info.Length > 512L * 1024 * 1024) throw new AmpleException("AMP5311", "The GM SoundFont exceeds the 512 MiB size limit.");
                    pinned.Add(new PlanInput("file", "sha256:" + FileHashCache.Sha256Hex(soundFont), soundFontRef.StartsWith("pack:", StringComparison.Ordinal) ? soundFontRef : soundFontRef.Replace('\\', '/')));
                }
            }
        }

        // The settings of this song for the renderer's plugin are part of its plan: choose another model and the block is a new take.
        Dictionary<string, string>? songSettings = null;
        if (renderer is IConfigurableRenderer configurable)
        {
            string prefix = configurable.SettingsKey + ".";
            songSettings = doc.Settings.Where(s => s.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(s => s.Key[prefix.Length..], s => s.Value, StringComparer.Ordinal);
            if (engineSettings is not null)
            {
                if (engineSettings.ContainsKey("synthModel") && !engineSettings.ContainsKey("synthModelFile"))
                {
                    songSettings.Remove("synthModelFile");   // a file of the song would win over the family the block asks for
                }

                foreach (KeyValuePair<string, string> over in engineSettings)
                {
                    songSettings[over.Key] = over.Value;
                }
            }

            if (songSettings.Count > 0)
            {
                string canonical = string.Join('\n', songSettings.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => s.Key + "=" + s.Value));
                pinned.Add(new PlanInput("setting", "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))), configurable.SettingsKey));
            }
        }

        // the channel mode is part of the plan (a mode set only by the master block is pinned here, a mode on the block is in its fingerprint)
        string channelMode = ChannelMode.Of(block, doc);
        if (channelMode != ChannelMode.Auto && !block.Parameters.Any(p => p.Name == "channels")) pinned.Add(new PlanInput("setting", "channels:" + channelMode, "channels"));

        bool arranged = BlockTiming.IsArranged(owner, doc, []);
        int first = arranged ? range.FirstBar : 1;
        int count = arranged ? range.Bars : localBars;
        HashSet<int> active = [.. block.Parts.Count == 0 ? Enumerable.Range(1, count) : block.Parts.SelectMany(p => Enumerable.Range(p.At, p.Length))];
        ulong master = (ulong)(doc.Project?.MasterSeed ?? 0);
        Parameter? typed = block.Parameters.FirstOrDefault(p => p.Name == "seed" && ulong.TryParse(p.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _));
        ulong seed = recipe is null && ov?.Seed is not null ? ov.Seed.Value : options.Control?.SeedFor(block.Name) ?? (typed is null ? SeedDeriver.Derive(master, "block", block.Name, "seed", "seed") : ulong.Parse(typed.Value, CultureInfo.InvariantCulture));
        if (ov?.Seed is null && IsSeedFree(block.Type)) seed = 0;   // block is the source of a render recipe when there is one; a variant asked for with its own seed keeps it

        Dictionary<string, object?> settings = new(StringComparer.Ordinal)
        {
            ["timeline"] = "local-v1", ["sampleRate"] = rate, ["firstBar"] = first, ["barCount"] = count, ["tempoMilli"] = tempoMilli,
            ["bars"] = string.Join(',', active.Order()), ["templateDefaults"] = template.Parameters.Where(p => !(p.Key == "engine" && p.Value.Length == 0)).OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(),   // an empty engine is not a default: old plans keep their identity
            ["parameters"] = block.Parameters.Where(p => p.Name != "tint").OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new { p.Name, p.Value }).ToArray(),
            ["pluginSettings"] = songSettings, ["source"] = new { block.Type, block.Name, block.Class, block.Version, block.Parts, Froms = block.Froms.Select(f => new { f.Block, f.Output, f.Input, f.Disabled }) },
        };
        if (doc.Project is { } meterOf && (meterOf.MeterNumerator != 4 || meterOf.MeterDenominator != 4)) settings["meter"] = $"{meterOf.MeterNumerator}/{meterOf.MeterDenominator}";
        if (ov?.Parameters is { Count: > 0 } effectiveParameters)
            settings["variantChanges"] = effectiveParameters.Select(parameter => new { name = parameter.Key, from = ov.ParameterOriginals?.GetValueOrDefault(parameter.Key) ?? string.Empty, to = parameter.Value }).ToArray();
        if (ov?.VariantIndex is int variantIndex) settings["variantIndex"] = variantIndex;

        PlanRecord plan = new(
            projectId,
            "block:" + owner.Name,
            owner.Name,
            recipe?.Fingerprint() ?? Fingerprint(block),
            [.. sources.Select(s => new PlanInput("take", s.Value.TakeId, s.Key + ":" + s.Value.Role)), .. pinned],
            [new PlanSeed("seed", seed), .. (renderer is RenderChainRenderer chained ? chained.EffectSeeds : Array.Empty<PlanSeed>())],
            RngVersion.Current,
            renderer.Id,
            renderer.Version,
            renderer.ToolprintJson,
            JsonSerializer.Serialize(settings),
            ov?.Intent ?? intent,
            ov?.ParentPlanId);
        string planId = PlanCanonical.PlanId(plan);
        TakeRecord? cached = ov?.Force == true || options.Trace is not null ? null : bank.FindTakeForPlan(planId);
        if (cached is null && ov?.Force != true && options.Trace is null && readOnlyCache?.FindTakeForPlan(planId) is { } external && external.Data.All(d => readOnlyCache.DataExists(d.DataId)))
        {
            await readOnlyCache.CopyPlanLineageToAsync(bank, plan, cancellationToken).ConfigureAwait(false);
            TakeTicket import = await bank.BeginAsync(plan, cancellationToken).ConfigureAwait(false);
            List<(string Role, Stream Data)> data = [];
            try
            {
                foreach (TakeDataRef item in external.Data) data.Add((item.Role, readOnlyCache.OpenData(item.DataId)));
                cached = await import.CommitAsync(data, external.MetaJson, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                foreach ((_, Stream stream) in data) await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        // a take whose data was moved away or lost is not a cache hit: the block renders again instead of failing on the missing file
        if (cached is not null && !cached.Data.All(d => bank.DataExists(d.DataId))) cached = null;
        if (cached is not null)
        {
            string? cachedReport = null;
            if (owner.Type is "meter" or "trim" && cached.Data.FirstOrDefault(d => d.Role == "report") is { } reportRef)
            {
                await using Stream reportStream = bank.OpenData(reportRef.DataId);
                using StreamReader reportReader = new(reportStream);
                cachedReport = await reportReader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);   // a meter says what it measured, also when it comes from the cache
            }

            return new BlockResult(owner.Name, "cached", cached.TakeId, cachedReport);
        }

        if (options.PlanOnly) return new BlockResult(owner.Name, "stale", null, null);
        if (options.Adopt?.Invoke(owner.Name) is { } adopted)
        {
            TakeTicket handOver = await bank.BeginAsync(plan, cancellationToken).ConfigureAwait(false);
            TakeRecord kept = await handOver.CommitAsync([.. adopted.Select(o => (o.Role, (Stream)new MemoryStream(o.Data)))], "{\"adopted\":\"bundle\"}", cancellationToken).ConfigureAwait(false);
            return new BlockResult(owner.Name, "adopted", kept.TakeId, null);
        }

        TakeTicket ticket = await bank.BeginAsync(plan, cancellationToken).ConfigureAwait(false);
        try
        {
            UsageTrace.Begin(options.Trace, owner.Name);
            ProgressTracker tracker = new(owner.Name, options.Detail);
            tracker.Report(new ProgressReport(0));
            // the controls that steer parameters of this block become curves of the song for the renderer (it follows them like auto(...))
            (BlockNode renderBlock, IReadOnlyDictionary<string, IReadOnlyList<(long Sample, int Value)>> controlPoints) = ControlBinding.Resolve(block, template, inputs, rate, spb, first, count);
            using IDisposable controls = ControlBinding.Use(controlPoints);
            RenderRequest request = new(renderBlock, template, rate, spb, first, count, active, inputs, seed, projectRoot, options.Detail is null ? null : tracker.Report, songSettings) { BeatsPerBar = doc.Project?.BeatsPerBar ?? 4.0, AmpleRoot = ampleRoot, ForceRender = ov?.Force == true };
            if (moduleMidi is not null) request = request with { MidiPpq = moduleMidi.Ppq, MidiTempoMap = moduleMidi.Tempos, PerformanceEvents = PerformanceEvents(block, moduleMidi, rate, spb, first + BlockTiming.Origin(owner, doc, []) - 1, count) };
            // a pitch bend that comes with the notes (the curve of a midi-clip, the bend of single notes) bends the sound of the block that plays them, whatever instrument it is
            IReadOnlyList<RenderOutput> outputs = ChannelMode.Apply(PitchBend.Apply(ClipControls.Apply(await renderer.RenderAsync(request, cancellationToken).ConfigureAwait(false), request, cancellationToken), request, cancellationToken), channelMode);
            string metadata = JsonSerializer.Serialize(outputs.Where(o => o.MetadataJson is not null).ToDictionary(o => o.Role, o => JsonSerializer.Deserialize<JsonElement>(o.MetadataJson!)));
            List<(string Role, Stream Data)> storedOutputs = [.. outputs.Select(o => (o.Role, (Stream)new MemoryStream(o.Data)))];
            if (metadata != "{}" && !outputs.Any(o => o.Role == "render-recipe")) storedOutputs.Add(("render-recipe", new MemoryStream(Encoding.UTF8.GetBytes(metadata))));
            TakeRecord take = await ticket.CommitAsync(storedOutputs, metadata, cancellationToken).ConfigureAwait(false);
            string? report = owner.Type is "meter" or "trim" && outputs.FirstOrDefault(o => o.Role == "report") is { } measured ? System.Text.Encoding.UTF8.GetString(measured.Data) : null;
            return new BlockResult(owner.Name, "rendered", take.TakeId, report);
        }
        catch (OperationCanceledException)
        {
            await ticket.CancelAsync("the build was cancelled").ConfigureAwait(false);
            return new BlockResult(owner.Name, "cancelled", null, "cancelled");
        }
        catch (Exception ex) when (ex is not AmpleException || ex.Message.Length > 0)
        {
            string message = ex is AmpleException known ? $"{known.DiagnosticCode}: {known.Message}" : ex.Message;
            await ticket.FailAsync(message).ConfigureAwait(false);
            return new BlockResult(owner.Name, "failed", null, message);
        }
    }

    private static BlockNode WithParameters(BlockNode block, IReadOnlyDictionary<string, string> values)
    {
        BlockNode copy = new() { Type = block.Type, Name = block.Name, RawName = block.RawName, Class = block.Class, Version = block.Version, Freeze = block.Freeze, Line = block.Line, CloseLine = block.CloseLine, Skipped = block.Skipped };
        copy.Parts.AddRange(block.Parts);
        copy.Flags.AddRange(block.Flags);
        copy.Parameters.AddRange(block.Parameters.Select(parameter => values.TryGetValue(parameter.Name, out string? value) ? parameter with { Value = value } : parameter));
        copy.Froms.AddRange(block.Froms);
        copy.ControlRefs.AddRange(block.ControlRefs);
        foreach (string name in values.Keys.Where(name => name != "$seed" && copy.Parameters.All(parameter => parameter.Name != name))) copy.Parameters.Add(new Parameter(name, values[name], block.Line));
        return copy;
    }

    private Dictionary<string, ModuleMidiSource> ReadMidiModules(AmpleDocument doc, long tempoMilli, IReadOnlyDictionary<string, string>? sourceOverrides)
    {
        Dictionary<string, ModuleMidiSource> modules = new(StringComparer.Ordinal);
        foreach (string line in doc.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            Match use = MidiModuleUse.Match(line);
            if (!use.Success) continue;
            string name = use.Groups["module"].Value, instance = use.Groups["instance"].Value;
            string relative = "modules/" + name + ".module", path = Path.Combine(projectRoot, "modules", name + ".module");
            string source;
            if (sourceOverrides?.TryGetValue(relative, out string? workingSource) == true) source = workingSource;
            else
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 64L * 1024 * 1024) continue;
                source = File.ReadAllText(path);
            }
            if (Encoding.UTF8.GetByteCount(source) > 64L * 1024 * 1024) continue;
            Match header = MidiModuleHeader.Match(source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').FirstOrDefault(text => text.StartsWith("# midi-source ", StringComparison.Ordinal)) ?? string.Empty);
            if (!header.Success || !int.TryParse(header.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int ppq) || ppq is < 1 or > 0x7FFF) continue;
            List<(int Track, SmfEvent Event)> events = [];
            foreach (string eventLine in source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                Match item = MidiSourceEvent.Match(eventLine);
                if (!item.Success || !int.TryParse(item.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int track) || track is < 0 or > 255 ||
                    !long.TryParse(item.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long tick) ||
                    !Enum.TryParse(item.Groups[3].Value, false, out SmfEventKind kind) ||
                    !int.TryParse(item.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int channel) || channel is < 0 or > 15 ||
                    !int.TryParse(item.Groups[5].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int data1) || data1 < 0 || data1 > (kind is SmfEventKind.Meta or SmfEventKind.SystemExclusive ? 255 : 127) ||
                    !int.TryParse(item.Groups[6].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int data2) || data2 is < 0 or > 127 || item.Groups[8].Value.Length > 131070 || item.Groups[8].Value.Length % 2 != 0) continue;
                byte? meta = item.Groups[7].Value == "none" ? null : byte.TryParse(item.Groups[7].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte parsedMeta) ? parsedMeta : null;
                byte[] payload = Convert.FromHexString(item.Groups[8].Value);
                events.Add((track, new SmfEvent(tick, kind, channel, data1, data2, meta, payload)));
                if (events.Count > 1_000_000) throw new AmpleException("AMP5504", "The module MIDI event timeline exceeds its event limit.");
            }
            long initialTempo = Math.Clamp(60_000_000_000L / tempoMilli, 1, 0xFFFFFF);
            SortedDictionary<long, int> tempos = new() { [0] = (int)initialTempo };
            foreach (SmfEvent item in events.Select(x => x.Event).Where(e => e.Kind == SmfEventKind.Meta && e.MetaType == 0x51 && e.Payload.Length == 3))
            {
                int micros = (item.Payload[0] << 16) | (item.Payload[1] << 8) | item.Payload[2];
                if (micros > 0) tempos[item.Tick] = micros;
            }
            string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
            modules[instance] = new ModuleMidiSource(name, instance, relative, hash, ppq, events, [.. tempos.Select(entry => new SmfTempo(entry.Key, entry.Value))]);
        }
        return modules;
    }

    private static ModuleMidiSource? FindMidiModule(BlockNode block, IReadOnlyDictionary<string, ModuleMidiSource> modules)
    {
        if (block.Type != "gm") return null;
        foreach (ModuleMidiSource module in modules.Values)
        {
            string prefix = module.Instance + "/";
            if (!block.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            Match instrument = Regex.Match(block.Name[prefix.Length..], @"^instrument-(\d{3})-gm-", RegexOptions.CultureInvariant);
            if (!instrument.Success || !int.TryParse(instrument.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int track) || track < 1) continue;
            return module;
        }
        return null;
    }

    private static IReadOnlyList<MidiPerformanceEvent> PerformanceEvents(BlockNode block, ModuleMidiSource module, int sampleRate, int samplesPerBar, int firstBar, int barCount)
    {
        Match instrument = Regex.Match(block.Name[(module.Instance.Length + 1)..], @"^instrument-(\d{3})-gm-", RegexOptions.CultureInvariant);
        int track = instrument.Success && int.TryParse(instrument.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int trackNumber) ? trackNumber - 1 : -1;
        int maximum = checked(samplesPerBar * barCount);
        MidiTempoTimeline timeline = new(module.Tempos);
        return [.. module.Events.Where(entry => entry.Track == track).Select(entry => entry.Event).Where(e => e.Kind is SmfEventKind.ControlChange or SmfEventKind.PitchBend or SmfEventKind.ChannelPressure or SmfEventKind.PolyphonicPressure)
            .Select(e => new MidiPerformanceEvent(timeline.Sample(e.Tick, module.Ppq, sampleRate, samplesPerBar, firstBar, maximum), e.Kind, e.Channel, e.Data1, e.Data2))
            .OrderBy(e => e.Sample)];
    }

    internal static int MidiSampleAtTick(long tick, int ppq, IReadOnlyList<SmfTempo> tempos, int sampleRate, int samplesPerBar, int firstBar, int maximumSamples)
    {
        return new MidiTempoTimeline(tempos).Sample(tick, ppq, sampleRate, samplesPerBar, firstBar, maximumSamples);
    }

    /// <summary>
    /// <c>sing auto</c> on an ai-voice block: the voice sings as long as the linked lyrics file needs, from the start of the block to the end of the last line
    /// (the last line is given as many bars as the one before it, 2 when it is the only one). The block is copied with the number in place of the word, so the plan holds the real length.
    /// </summary>
    /// <summary>When one bus of the song has solo on, every other bus is muted (the mute is put on the copy that is built, the song text stays as written).</summary>
    private static BlockNode ApplyBusSolo(BlockNode block, AmpleDocument doc)
    {
        static bool Is(BlockNode b, string name, string value) => b.Type == "bus" && b.Parameters.Any(p => p.Name == name && p.Value.Trim().Trim('"') == value);
        if (block.Type != "bus" || Is(block, "solo", "on") || !doc.Blocks.Any(b => Is(b, "solo", "on"))) return block;
        BlockNode copy = new() { Type = block.Type, Name = block.Name, RawName = block.RawName, Class = block.Class, Version = block.Version, Freeze = block.Freeze, Line = block.Line, CloseLine = block.CloseLine, Skipped = block.Skipped };
        copy.Parts.AddRange(block.Parts);
        copy.Flags.AddRange(block.Flags);
        copy.Froms.AddRange(block.Froms);
        copy.ControlRefs.AddRange(block.ControlRefs);
        copy.Parameters.AddRange(block.Parameters.Where(p => p.Name != "mute"));
        copy.Parameters.Add(new Parameter("mute", "on", 0));
        return copy;
    }

    private BlockNode ResolveSing(BlockNode block, AmpleDocument doc)
    {
        Parameter? sing = block.Parameters.FirstOrDefault(p => p.Name == "sing");
        if (block.Type != "ai-voice" || sing is null || !sing.Value.Trim().Trim('"').Equals("auto", StringComparison.OrdinalIgnoreCase) || projectRoot is null)
        {
            return block;
        }

        BlockNode? words = doc.Blocks.FirstOrDefault(b => b.Name == block.Froms.Where(f => !f.Disabled).FirstOrDefault(f => f.Input == "lyrics")?.Block);
        if (words is null || BuiltInRenderers.LyricsFile(projectRoot, words) is not { } file)
        {
            return block;
        }

        List<double> bars = [];
        foreach (string line in File.ReadAllLines(file))
        {
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(line, @"^\[bar\s+([\d.]+)\]", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double bar))
            {
                bars.Add(bar);
            }
        }

        if (bars.Count == 0)
        {
            return block;
        }

        bars.Sort();
        double gap = bars.Count > 1 ? Math.Max(1, bars[^1] - bars[^2]) : 2;
        int start = 1;
        int length = Math.Max(1, (int)Math.Ceiling(bars[^1] + gap - start));

        // The words set a second limit: about four syllables a second plus a short breath per line, so a short text is not stretched over a long block
        // (ACE fills all the time it is given and leaves the rest empty or repeats sounds).
        int syllables = 0;
        int lines = 0;
        foreach (string line in File.ReadAllLines(file))
        {
            string spoken = System.Text.RegularExpressions.Regex.Replace(line, @"\[[^\]]*\]", " ", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
            int count = System.Text.RegularExpressions.Regex.Count(spoken, @"[aeiouyAEIOUY]+", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
            if (count > 0)
            {
                syllables += count;
                lines++;
            }
        }

        double barSeconds = 240.0 / (doc.Project?.Tempo > 0 ? doc.Project.Tempo : 120.0);
        int estimate = Math.Max(1, (int)Math.Ceiling(((syllables / 4.0) + (lines * 0.6)) * 1.15 / barSeconds));
        length = Math.Min(length, estimate);
        BlockNode copy = new() { Type = block.Type, Name = block.Name, RawName = block.RawName, Class = block.Class, Version = block.Version, Freeze = block.Freeze, Line = block.Line, CloseLine = block.CloseLine, Skipped = block.Skipped };
        copy.Parts.AddRange(block.Parts);
        copy.Flags.AddRange(block.Flags);
        copy.Froms.AddRange(block.Froms);
        copy.ControlRefs.AddRange(block.ControlRefs);
        copy.Parameters.AddRange(block.Parameters.Select(p => p.Name == "sing" ? p with { Value = length.ToString(CultureInfo.InvariantCulture) } : p));
        return copy;
    }

    /// <summary>The identity of a block's source for caching: type, name, class and version, parts, parameters, connections. The flags (bake, lock, refresh) and layout are not part of it, so baking a block does not change its key.</summary>
    public static string Fingerprint(BlockNode b)
    {
        b = BlockTiming.Local(b);
        string text = $"{b.Type}\n{b.Name}\n{b.Class}@{b.Version}\n{string.Join(',', b.Parts.Select(p => $"{p.At}:{p.Length}:{p.Name}"))}\n{string.Join('\n', b.Parameters.Select(p => p.Name + "=" + p.Value))}\n{string.Join('\n', b.Froms.Where(f => !f.Disabled).Select(f => $"{f.Block}.{f.Output}>{f.Input}"))}";
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static int SongBars(List<BlockNode> blocks, Func<BlockNode, int?> inferred)
    {
        int bars = 0;
        foreach (BlockNode b in blocks)
        {
            // a placement, a bars parameter, or (a sequencer that is not placed) the end of its playlist
            int? end = b.Parts.Count > 0 ? b.Parts.Max(p => p.At + p.Length - 1) : b.Parameters.FirstOrDefault(p => p.Name == "bars") is { Number: double n } ? (int)n : inferred(b);
            bars = Math.Max(bars, end ?? 0);
        }

        return bars == 0 ? 8 : bars;
    }

    private static HashSet<string> Needed(List<BlockNode> blocks, string only)
    {
        HashSet<string> set = [];
        void Walk(string name)
        {
            if (set.Add(name))
            {
                foreach (FromLine f in blocks.FirstOrDefault(b => b.Name == name)?.Dependencies() ?? [])
                {
                    Walk(f.Block);
                }
            }
        }

        Walk(only);
        return set;
    }

    private static List<List<BlockNode>> Levels(List<BlockNode> blocks)
    {
        Dictionary<string, int> depth = [];
        int Depth(BlockNode b, HashSet<string> path)
        {
            if (depth.TryGetValue(b.Name, out int d))
            {
                return d;
            }

            if (!path.Add(b.Name))
            {
                throw new AmpleException("AMP5250", $"The blocks form a cycle at '{b.Name}'.");
            }

            int value = b.Dependencies().Where(f => !f.Disabled).Select(f => blocks.FirstOrDefault(x => x.Name == f.Block)).OfType<BlockNode>().Select(x => Depth(x, path) + 1).DefaultIfEmpty(0).Max();
            path.Remove(b.Name);
            return depth[b.Name] = value;
        }

        foreach (BlockNode b in blocks)
        {
            Depth(b, []);
        }

        return [.. blocks.GroupBy(b => depth[b.Name]).OrderBy(g => g.Key).Select(g => g.ToList())];
    }

    private sealed class ConcurrentDictionaryLike
    {
        private readonly Dictionary<string, BlockResult> map = [];

        public void Set(BlockResult r)
        {
            lock (map)
            {
                map[r.Block] = r;
            }
        }

        public BlockResult? Get(string name)
        {
            lock (map)
            {
                return map.GetValueOrDefault(name);
            }
        }
    }
}
