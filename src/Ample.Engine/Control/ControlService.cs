using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ample.Audio.Engine;
using Ample.Audio.Random;
using Ample.Core.Abstractions;
using Ample.Engine.Engine;
using Ample.Language;
using Ample.Storage.BlackBox;
using Ample.Storage.Takes;
using Microsoft.Data.Sqlite;

namespace Ample.Engine.Control;

/// <summary>The freeze ladder of a block.</summary>
public enum FreezeLevel
{
    /// <summary>A re-render may differ.</summary>
    Free,

    /// <summary>The seeds are fixed.</summary>
    Seeds,

    /// <summary>The seeds and the melody plan are fixed; the voice may be re-rendered.</summary>
    Plan,

    /// <summary>This exact take is used; nothing is rendered.</summary>
    Take,
}

/// <summary>A seed with its lock state.</summary>
/// <param name="Name">Seed name.</param>
/// <param name="Value">Value.</param>
/// <param name="Locked">True (the default) when changing it is refused.</param>
/// <param name="Origin">Where the value came from.</param>
public sealed record SeedState(string Name, ulong Value, bool Locked, SeedOrigin Origin);

/// <summary>A variant of a block.</summary>
/// <param name="TakeId">The take.</param>
/// <param name="Index">Number within its request.</param>
/// <param name="State"><c>candidate</c>, <c>selected</c>, <c>kept</c> or <c>discarded</c>.</param>
/// <param name="Changes">The fields changed for this take.</param>
public sealed record VariantInfo(string TakeId, int Index, string State, IReadOnlyList<VariantFieldChange> Changes);

/// <summary>The outcome of a regeneration or a reproduction check.</summary>
/// <param name="Id">Id of the pending decision (empty when the result was the same).</param>
/// <param name="Status"><c>same</c>, <c>pending</c> or <c>different</c>.</param>
/// <param name="OldTake">The take that stays selected until a decision is made.</param>
/// <param name="NewTake">The new take.</param>
/// <param name="Differences">How the new result differs.</param>
public sealed record RegenResult(string Id, string Status, string OldTake, string NewTake, IReadOnlyList<string> Differences)
{
    /// <summary>Gets configuration or provenance changes; these warn without blocking the comparison.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>A one-line explanation for the producer of what an edit does at the current freeze level.</summary>
/// <param name="Edit">The kind of edit.</param>
/// <param name="Outcome"><c>free</c>, <c>changes-the-result</c>, <c>breaks-reproduction</c> or <c>ignored</c>.</param>
public sealed record EditEffect(string Edit, string Outcome);

/// <summary>What the producer may do with a block (data for the UI).</summary>
/// <param name="Level">The freeze level.</param>
/// <param name="Warnings">Notices (AMP5101 and AMP5102 text) that apply.</param>
/// <param name="Effects">What each kind of edit does.</param>
public sealed record Explainer(FreezeLevel Level, IReadOnlyList<string> Warnings, IReadOnlyList<EditEffect> Effects);

/// <summary>Seeds, the freeze ladder, variants and safe regeneration (see docs/regeneration.md): seeds are locked by default and every change is in the history; a frozen take is never re-rendered; regenerating sets the old take aside, verifies the new result, and when it differs asks what to do with <b>restore the previous take</b> as the default, as a decision that survives a crash.</summary>
public sealed class ControlService : IBuildControl
{
    /// <summary>The warning shown with every regeneration of a block whose results are not exact (AMP5101).</summary>
    public const string RegenerationWarning = "Careful: regeneration is only guaranteed to give the same result on this machine with the same plugin, model and GPU setup. On another machine, driver or version it may sound different. Your current take is kept safe either way.";

    private readonly IClock clock;
    private readonly string dbPath;
    private readonly Takebank bank;
    private readonly BuildEngine engine;
    private readonly CommandBus bus;
    private readonly ulong masterSeed;
    private readonly IReadOnlyList<IBlockRenderer> extraRenderers;

    /// <summary>Initializes a new instance of the <see cref="ControlService"/> class.</summary>
    public ControlService(IClock clock, string projectRoot, Takebank bank, BuildEngine engine, CommandBus bus, ulong masterSeed, IReadOnlyList<IBlockRenderer>? extraRenderers = null)
    {
        this.clock = clock;
        dbPath = Path.Combine(projectRoot, ".ample", "history.db");
        this.bank = bank;
        this.engine = engine;
        this.bus = bus;
        this.masterSeed = masterSeed;
        this.extraRenderers = extraRenderers ?? [];
        Run(c => Exec(c, "CREATE TABLE IF NOT EXISTS c_seeds (block TEXT NOT NULL, name TEXT NOT NULL, value TEXT NOT NULL, locked INTEGER NOT NULL, origin TEXT NOT NULL, PRIMARY KEY (block, name));" +
            "CREATE TABLE IF NOT EXISTS c_seed_history (id INTEGER PRIMARY KEY AUTOINCREMENT, block TEXT NOT NULL, name TEXT NOT NULL, old TEXT NOT NULL, new TEXT NOT NULL, reason TEXT NOT NULL, at TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS c_variants (block TEXT NOT NULL, take_id TEXT NOT NULL, idx INTEGER NOT NULL, state TEXT NOT NULL, PRIMARY KEY (block, take_id));" +
            "CREATE TABLE IF NOT EXISTS c_selected (block TEXT PRIMARY KEY, take_id TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS c_regen (id TEXT PRIMARY KEY, block TEXT NOT NULL, old_take TEXT NOT NULL, new_take TEXT NOT NULL, status TEXT NOT NULL, decision TEXT NULL, diff TEXT NOT NULL, created_at TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS c_reproduction (id INTEGER PRIMARY KEY AUTOINCREMENT, take_id TEXT NOT NULL, result TEXT NOT NULL, toolprint TEXT NOT NULL, at TEXT NOT NULL);" +
            "INSERT OR IGNORE INTO schema_parts (part, version) VALUES ('control', 1);"));
    }

    /// <inheritdoc />
    public ulong? SeedFor(string block) => Run(c => Scalar(c, "SELECT value FROM c_seeds WHERE block = $b AND name = 'seed'", ("$b", block)) is string v ? ulong.Parse(v, CultureInfo.InvariantCulture) : (ulong?)null);

    /// <inheritdoc />
    public string? SelectedTake(string block) => Run(c => Scalar(c, "SELECT take_id FROM c_selected WHERE block = $b", ("$b", block)) as string);

    /// <inheritdoc />
    public string? PinnedMelodyDataId(string block)
    {
        string? take = SelectedTake(block);
        return take is null ? null : bank.GetTake(take).Data.FirstOrDefault(d => d.Role == "melody")?.DataId;
    }

    /// <summary>Gets a seed. The first read stores the derived value, locked.</summary>
    public SeedState GetSeed(string block, string name) => Run(c =>
    {
        Exec(c, "INSERT OR IGNORE INTO c_seeds (block, name, value, locked, origin) VALUES ($b, $n, $v, 1, 'Derived')", ("$b", block), ("$n", name), ("$v", SeedDeriver.Derive(masterSeed, "block", block, "seed", name).ToString(CultureInfo.InvariantCulture)));
        return Query(c, "SELECT name, value, locked, origin FROM c_seeds WHERE block = $b AND name = $n", r => new SeedState(r.GetString(0), ulong.Parse(r.GetString(1), CultureInfo.InvariantCulture), r.GetInt64(2) != 0, Enum.Parse<SeedOrigin>(r.GetString(3))), ("$b", block), ("$n", name))[0];
    });

    /// <summary>Unlocks a seed (the warning is shown by the caller; changing it probably makes reproduction of earlier takes impossible).</summary>
    public ValueTask UnlockSeedAsync(string block, string name) => bus.RunAsync("seed.unlock", Json(new { block, name }), () => Set(block, name, locked: false));

    /// <summary>Locks a seed.</summary>
    public ValueTask LockSeedAsync(string block, string name) => bus.RunAsync("seed.lock", Json(new { block, name }), () => Set(block, name, locked: true));

    /// <summary>Changes a seed. Refused while it is locked (AMP5260); the change is kept in the seed history.</summary>
    public ValueTask SetSeedAsync(string block, string name, ulong value, string reason, SeedOrigin origin = SeedOrigin.UserSet)
    {
        SeedState current = GetSeed(block, name);
        if (current.Locked)
        {
            throw new AmpleException("AMP5260", $"The seed '{name}' of '{block}' is locked. Changing it probably makes reproduction of earlier takes impossible; unlock it first.");
        }

        return bus.RunAsync("seed.set", Json(new { block, name, old = current.Value, value, reason }), () =>
        {
            Run(c =>
            {
                Exec(c, "UPDATE c_seeds SET value = $v, origin = $o WHERE block = $b AND name = $n", ("$v", value.ToString(CultureInfo.InvariantCulture)), ("$o", origin.ToString()), ("$b", block), ("$n", name));
                Exec(c, "INSERT INTO c_seed_history (block, name, old, new, reason, at) VALUES ($b, $n, $o, $v, $r, $at)", ("$b", block), ("$n", name), ("$o", current.Value.ToString(CultureInfo.InvariantCulture)), ("$v", value.ToString(CultureInfo.InvariantCulture)), ("$r", reason), ("$at", Now()));
            });
            return ValueTask.CompletedTask;
        });
    }

    /// <summary>Gives a seed a new value derived from the master seed and the number of changes so far (needs the seed to be unlocked).</summary>
    public ValueTask RandomizeSeedAsync(string block, string name, string reason) =>
        SetSeedAsync(block, name, SeedDeriver.Derive(masterSeed, "block", block, "seed-attempt", name, (ulong)SeedHistory(block, name).Count + 1), reason, SeedOrigin.Derived);

    /// <summary>Gets the seed history, oldest first.</summary>
    public IReadOnlyList<SeedChange> SeedHistory(string block, string name) => Run(c => Query(c, "SELECT old, new, reason, at FROM c_seed_history WHERE block = $b AND name = $n ORDER BY id", r => new SeedChange(ulong.Parse(r.GetString(0), CultureInfo.InvariantCulture), ulong.Parse(r.GetString(1), CultureInfo.InvariantCulture), r.GetString(2), DateTimeOffset.Parse(r.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)), ("$b", block), ("$n", name)));

    /// <summary>Gets the freeze level of a block from its source.</summary>
    public static FreezeLevel LevelOf(BlockNode block) => block.Flags.Contains("bake") ? FreezeLevel.Take : block.Freeze == "plan" ? FreezeLevel.Plan : block.Freeze == "seeds" ? FreezeLevel.Seeds : FreezeLevel.Free;

    /// <summary>Selects a take for a block (the take used when the block is baked).</summary>
    public ValueTask SelectTakeAsync(string block, string takeId) => bus.RunAsync("take.select", Json(new { block, takeId }), () =>
    {
        bank.GetTake(takeId);
        Run(c =>
        {
            Exec(c, "INSERT OR REPLACE INTO c_selected (block, take_id) VALUES ($b, $t)", ("$b", block), ("$t", takeId));
            Exec(c, "UPDATE c_variants SET state = 'candidate' WHERE block = $b AND state = 'selected'", ("$b", block));
            Exec(c, "UPDATE c_variants SET state = 'selected' WHERE block = $b AND take_id = $t", ("$b", block), ("$t", takeId));
        });
        return ValueTask.CompletedTask;
    });

    /// <summary>Makes <paramref name="count"/> takes of one block, each with its own seed derived from the master seed (<c>kind</c> is <c>variant</c>, <c>resing</c> or <c>rewrite</c>); asking again gives the same variants.</summary>
    public async ValueTask<IReadOnlyList<TakeRecord>> RequestVariantsAsync(AmpleDocument doc, string block, int count, string kind = "variant", CancellationToken cancellationToken = default)
    {
        List<TakeRecord> made = [];
        await bus.RunAsync("variants.request", Json(new { block, count, kind }), async () =>
        {
            for (int i = 0; i < count; i++)
            {
                ulong seed = SeedDeriver.Derive(masterSeed, "block", block, kind, (ulong)i);
                BuildReport report = await engine.BuildAsync(doc, new BuildOptions(Only: block, ExtraRenderers: Extras, Control: this, Override: new BlockOverride(block, seed, "variant", false, null)), cancellationToken).ConfigureAwait(false);
                BlockResult result = report.Blocks.Single(b => b.Block == block);
                if (result.TakeId is null)
                {
                    throw new AmpleException("AMP5261", $"The variant could not be made: {result.Message}");
                }

                made.Add(bank.GetTake(result.TakeId));
                Run(c => Exec(c, "INSERT OR IGNORE INTO c_variants (block, take_id, idx, state) VALUES ($b, $t, $i, 'candidate')", ("$b", block), ("$t", result.TakeId), ("$i", i)));
            }
        }, cancellationToken).ConfigureAwait(false);
        return made;
    }

    /// <summary>Previews deterministic field changes without rendering or changing the song.</summary>
    public IReadOnlyList<IReadOnlyList<VariantFieldChange>> PreviewVariants(AmpleDocument doc, string block, int count, string kind, IReadOnlyList<string> fields, TemplateSpec template)
    {
        BlockNode node = doc.Blocks.FirstOrDefault(candidate => candidate.Name == block) ?? throw new AmpleException("AMP5261", $"There is no block named '{block}'.");
        return VariantFieldPlanner.Propose(masterSeed, node, template, count, 0, kind, fields);
    }

    /// <summary>Builds repeatable variants with only the chosen fields changed; the per-take plan records the effective values.</summary>
    public async ValueTask<IReadOnlyList<TakeRecord>> RequestVariantsAsync(AmpleDocument doc, string block, int count, int startIndex, string kind, IReadOnlyList<string> fields, TemplateSpec template, CancellationToken cancellationToken = default)
    {
        BlockNode node = doc.Blocks.FirstOrDefault(candidate => candidate.Name == block) ?? throw new AmpleException("AMP5261", $"There is no block named '{block}'.");
        IReadOnlyList<IReadOnlyList<VariantFieldChange>> changes = VariantFieldPlanner.Propose(masterSeed, node, template, count, startIndex, kind, fields);
        List<TakeRecord> made = [];
        await bus.RunAsync("variants.request", Json(new { block, count, startIndex, kind, fields }), async () =>
        {
            for (int offset = 0; offset < changes.Count; offset++)
            {
                int i = startIndex + offset;
                VariantFieldChange? seedChange = changes[offset].FirstOrDefault(change => change.Name is "$seed" or "seed");
                ulong? seed = seedChange is null ? null : ulong.Parse(seedChange.To, CultureInfo.InvariantCulture);
                VariantFieldChange[] parameterChanges = [.. changes[offset]];
                IReadOnlyDictionary<string, string> values = parameterChanges.ToDictionary(change => change.Name, change => change.To, StringComparer.Ordinal);
                IReadOnlyDictionary<string, string> originals = parameterChanges.ToDictionary(change => change.Name, change => change.From, StringComparer.Ordinal);
                BuildReport report = await engine.BuildAsync(doc, new BuildOptions(Only: block, ExtraRenderers: Extras, Control: this, Override: new BlockOverride(block, seed, "variant", false, null, values, originals, i)), cancellationToken).ConfigureAwait(false);
                BlockResult result = report.Blocks.Single(candidate => candidate.Block == block);
                if (result.TakeId is null) throw new AmpleException("AMP5261", $"The variant could not be made: {result.Message}");
                made.Add(bank.GetTake(result.TakeId));
                Run(c => Exec(c, "INSERT OR IGNORE INTO c_variants (block, take_id, idx, state) VALUES ($b, $t, $i, 'candidate')", ("$b", block), ("$t", result.TakeId), ("$i", i)));
            }
        }, cancellationToken).ConfigureAwait(false);
        return made;
    }

    /// <summary>Lists the variants of a block.</summary>
    public IReadOnlyList<VariantInfo> Variants(string block) => Run(c => Query(c, "SELECT take_id, idx, state FROM c_variants WHERE block = $b ORDER BY idx, take_id", r =>
    {
        string takeId = r.GetString(0);
        TakeRecord take = bank.GetTake(takeId);
        using JsonDocument plan = JsonDocument.Parse(bank.GetPlan(take.PlanId).Plan.SettingsJson);
        JsonElement settings = plan.RootElement;
        IReadOnlyList<VariantFieldChange> changes = settings.TryGetProperty("variantChanges", out JsonElement fields)
            ? [.. fields.EnumerateArray().Select(field => new VariantFieldChange(field.GetProperty("name").GetString()!, field.GetProperty("name").GetString()!, field.GetProperty("from").GetString()!, field.GetProperty("to").GetString()!))]
            : [];
        return new VariantInfo(takeId, r.GetInt32(1), r.GetString(2), changes);
    }, ("$b", block)));

    /// <summary>Keeps a variant (it stays selectable).</summary>
    public ValueTask KeepAsync(string block, string takeId) => bus.RunAsync("variants.keep", Json(new { block, takeId }), () =>
    {
        Run(c => Exec(c, "UPDATE c_variants SET state = 'kept' WHERE block = $b AND take_id = $t AND state = 'candidate'", ("$b", block), ("$t", takeId)));
        return ValueTask.CompletedTask;
    });

    /// <summary>Discards a variant: its take gets a tombstone (the record and the plan stay).</summary>
    public ValueTask DiscardAsync(string block, string takeId, string reason) => bus.RunAsync("variants.discard", Json(new { block, takeId, reason }), () =>
    {
        if (SelectedTake(block) == takeId)
        {
            throw new AmpleException("AMP5262", "The selected take cannot be discarded; select another first.");
        }

        bank.Tombstone(takeId, reason);
        Run(c => Exec(c, "UPDATE c_variants SET state = 'discarded' WHERE block = $b AND take_id = $t", ("$b", block), ("$t", takeId)));
        return ValueTask.CompletedTask;
    });

    /// <summary>Regenerates a block safely. The selected take is never touched: the new result is rendered next to it and verified. An identical result is reported as such; a different one becomes a pending decision whose default is to restore (keep) the previous take, stored so that a crash or a closed dialog leaves the old take in place.</summary>
    public async ValueTask<RegenResult> RegenerateAsync(AmpleDocument doc, string block, ulong? newSeed = null, CancellationToken cancellationToken = default)
    {
        string old = SelectedTake(block) ?? throw new AmpleException("AMP5261", $"'{block}' has no selected take to regenerate from. Build and select one first.");
        RegenResult? result = null;
        await bus.RunAsync("regen.started", Json(new { block, old }), async () =>
        {
            string oldPlan = bank.GetTake(old).PlanId;
            int attempt = Run(c => Query(c, "SELECT id FROM c_regen WHERE block = $b", r => r.GetString(0), ("$b", block)).Count) + 1;
            ulong seed = newSeed ?? SeedDeriver.Derive(masterSeed, "block", block, "regen", (ulong)attempt);
            BuildReport report = await engine.BuildAsync(doc, new BuildOptions(Only: block, ExtraRenderers: Extras, Control: this, Override: new BlockOverride(block, seed, "variant", true, oldPlan)), cancellationToken).ConfigureAwait(false);
            string created = report.Blocks.Single(b => b.Block == block).TakeId ?? throw new AmpleException("AMP5261", "The regeneration did not produce a take.");
            List<string> diff = Differences(old, created);
            if (diff.Count == 0)
            {
                Run(c => Exec(c, "INSERT INTO c_reproduction (take_id, result, toolprint, at) VALUES ($t, 'same', $p, $at)", ("$t", old), ("$p", ToolprintOf(old)), ("$at", Now())));
                result = new RegenResult(string.Empty, "same", old, created, []);
                return;
            }

            string id = Guid.NewGuid().ToString("N");
            Run(c =>
            {
                Exec(c, "INSERT INTO c_regen (id, block, old_take, new_take, status, diff, created_at) VALUES ($i, $b, $o, $n, 'pending', $d, $at)", ("$i", id), ("$b", block), ("$o", old), ("$n", created), ("$d", Json(diff)), ("$at", Now()));
                Exec(c, "INSERT OR IGNORE INTO c_variants (block, take_id, idx, state) VALUES ($b, $t, 0, 'candidate')", ("$b", block), ("$t", created));
            });
            result = new RegenResult(id, "pending", old, created, diff);
        }, cancellationToken).ConfigureAwait(false);
        return result!;
    }

    /// <summary>Lists decisions that are still open (shown on start and on the health page).</summary>
    public IReadOnlyList<(string Id, string Block, string OldTake, string NewTake, IReadOnlyList<string> Differences)> PendingRegens() => Run(c => Query(c, "SELECT id, block, old_take, new_take, diff FROM c_regen WHERE status = 'pending' ORDER BY created_at, id", r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), (IReadOnlyList<string>)JsonSerializer.Deserialize<List<string>>(r.GetString(4))!)));

    /// <summary>Answers a pending decision: <c>restore</c> (the default; the previous take stays selected), <c>keep-variant</c> (the new take is kept as a variant) or, through <see cref="ReplaceTakeAct"/>, replace.</summary>
    public ValueTask DecideAsync(string id, string decision = "restore") => bus.RunAsync("regen.decided", Json(new { id, decision }), () =>
    {
        if (decision is not ("restore" or "keep-variant"))
        {
            throw new AmpleException("AMP5263", "A decision is restore or keep-variant; replacing needs the dangerous-act contract (ReplaceTakeAct).");
        }

        Run(c =>
        {
            (string block, string created) = Query(c, "SELECT block, new_take FROM c_regen WHERE id = $i AND status = 'pending'", r => (r.GetString(0), r.GetString(1)), ("$i", id)).FirstOrDefault() is { Item1: not null } row ? row : throw new AmpleException("AMP5263", $"There is no open decision {id}.");
            Exec(c, "UPDATE c_regen SET status = 'decided', decision = $d WHERE id = $i", ("$d", decision), ("$i", id));
            if (decision == "keep-variant")
            {
                Exec(c, "UPDATE c_variants SET state = 'kept' WHERE block = $b AND take_id = $t AND state = 'candidate'", ("$b", block), ("$t", created));
            }
        });
        return ValueTask.CompletedTask;
    });

    internal (string Block, string NewTake) Replace(string id)
    {
        return Run(c =>
        {
            (string block, string created) = Query(c, "SELECT block, new_take FROM c_regen WHERE id = $i AND status = 'pending'", r => (r.GetString(0), r.GetString(1)), ("$i", id)).FirstOrDefault() is { Item1: not null } row ? row : throw new AmpleException("AMP5263", $"There is no open decision {id}.");
            Exec(c, "UPDATE c_regen SET status = 'decided', decision = 'replace' WHERE id = $i", ("$i", id));
            Exec(c, "INSERT OR REPLACE INTO c_selected (block, take_id) VALUES ($b, $t)", ("$b", block), ("$t", created));
            Exec(c, "UPDATE c_variants SET state = 'candidate' WHERE block = $b AND state = 'selected'", ("$b", block));
            Exec(c, "UPDATE c_variants SET state = 'selected' WHERE block = $b AND take_id = $t", ("$b", block), ("$t", created));
            return (block, created);
        });
    }

    /// <summary>Re-renders with the reference seed and current document/software to check reproduction, warning on recipe changes. Never replaces the reference; the result is added to its reproduction history.</summary>
    public async ValueTask<RegenResult> VerifyReproductionAsync(AmpleDocument doc, string block, CancellationToken cancellationToken = default)
    {
        string old = SelectedTake(block) ?? throw new AmpleException("AMP5261", $"'{block}' has no selected take to verify.");
        StoredPlan plan = bank.GetPlan(bank.GetTake(old).PlanId);
        ulong seed = plan.Plan.Seeds.First(s => s.Name == "seed").Value;
        RegenResult? result = null;
        await bus.RunAsync("reproduction.verify", Json(new { block, old }), async () =>
        {
            BuildReport report = await engine.BuildAsync(doc, new BuildOptions(plan.Plan.Intent == "preview" ? "preview" : "bake", Only: block, ExtraRenderers: Extras, Control: this, Override: new BlockOverride(block, seed, plan.Plan.Intent, true, plan.Plan.ParentPlanId)), cancellationToken).ConfigureAwait(false);
            string created = report.Blocks.Single(b => b.Block == block).TakeId ?? throw new AmpleException("AMP5261", "The reproduction check did not produce a take.");
            List<string> diff = Differences(old, created);
            PlanRecord current = bank.GetPlan(bank.GetTake(created).PlanId).Plan;
            List<string> warnings = [];
            if (plan.Plan.PluginVersion != current.PluginVersion || plan.Plan.ToolprintJson != current.ToolprintJson) warnings.Add("Renderer/software identity changed; this compares the reference take with the current renderer.");
            if (plan.Plan.SourceHash != current.SourceHash || plan.Plan.SettingsJson != current.SettingsJson) warnings.Add("Song parameters, template defaults or render settings changed; this is a comparison with the current recipe.");
            if (Provenance(bank.GetTake(old).MetaJson) != Provenance(bank.GetTake(created).MetaJson)) warnings.Add("Renderer provenance differs or was not recorded on the reference take; inspect both takes' metadata for model hashes and resolved requests.");
            Run(c => Exec(c, "INSERT INTO c_reproduction (take_id, result, toolprint, at) VALUES ($t, $r, $p, $at)", ("$t", old), ("$r", diff.Count == 0 ? "same" : "different"), ("$p", current.ToolprintJson), ("$at", Now())));
            result = new RegenResult(string.Empty, diff.Count == 0 ? "same" : "different", old, created, diff) { Warnings = warnings };
        }, cancellationToken).ConfigureAwait(false);
        return result!;
    }

    /// <summary>Gets the reproduction checks of a take: result and toolprint.</summary>
    public IReadOnlyList<(string Result, string Toolprint)> Reproductions(string takeId) => Run(c => Query(c, "SELECT result, toolprint FROM c_reproduction WHERE take_id = $t ORDER BY id", r => (r.GetString(0), r.GetString(1)), ("$t", takeId)));

    /// <summary>Gets the determinism state of a take's block type: <c>unverified</c>, <c>verified-local</c> (a reproduction check came out the same) or what the renderer claims.</summary>
    public string DeterminismOf(string block, string takeId)
    {
        string claim = RendererFor(bank.GetPlan(bank.GetTake(takeId).PlanId).Plan.PluginId)?.Determinism ?? "unverified";
        if (claim == "exact")
        {
            return "exact";
        }

        string? currentToolprint = RendererFor(bank.GetPlan(bank.GetTake(takeId).PlanId).Plan.PluginId)?.ToolprintJson;
        return Reproductions(takeId).Any(r => r.Result == "same" && r.Toolprint == currentToolprint) ? "verified-local" : "unverified";
    }

    /// <summary>Describes what each kind of edit does at the block's freeze level, with the regeneration notices that apply (data for the editor).</summary>
    public Explainer Explain(BlockNode block)
    {
        FreezeLevel level = LevelOf(block);
        IBlockRenderer? renderer = BuiltInRenderers.All.Concat(extraRenderers).LastOrDefault(r => r.BlockType == block.Type);
        List<string> warnings = [];
        if (renderer is null || renderer.Determinism != "exact")
        {
            warnings.Add("AMP5101: " + RegenerationWarning);
            if (renderer?.Determinism == "unverified" || renderer is null)
            {
                warnings.Add("AMP5102: the determinism of this block is unverified, so a regeneration may not reproduce the take.");
            }
        }

        EditEffect[] effects = level switch
        {
            FreezeLevel.Take => [new("change a parameter", "ignored"), new("change a seed", "ignored"), new("regenerate", "changes-the-result")],
            FreezeLevel.Plan => [new("change a parameter", "changes-the-result"), new("change the voice seed", "free"), new("change the melody", "ignored"), new("regenerate", "free")],
            FreezeLevel.Seeds => [new("change a parameter", "changes-the-result"), new("change a seed", "breaks-reproduction"), new("regenerate", "changes-the-result")],
            _ => [new("change a parameter", "changes-the-result"), new("change a seed", "breaks-reproduction"), new("regenerate", "free")],
        };
        return new Explainer(level, warnings, effects);
    }

    private IBlockRenderer? RendererFor(string id) => BuiltInRenderers.All.Concat(extraRenderers).FirstOrDefault(r => r.Id == id);

    private string ToolprintOf(string takeId) => bank.GetPlan(bank.GetTake(takeId).PlanId).Plan.ToolprintJson;

    private static string Provenance(string metadata)
    {
        JsonNode? root = JsonNode.Parse(metadata);
        void StripTiming(JsonNode? node)
        {
            if (node is JsonObject obj) { obj.Remove("seconds"); foreach (JsonNode? child in obj.Select(p => p.Value)) StripTiming(child); }
            else if (node is JsonArray array) foreach (JsonNode? child in array) StripTiming(child);
        }
        StripTiming(root);
        return root?.ToJsonString() ?? "{}";
    }

    private IReadOnlyList<IBlockRenderer>? Extras => extraRenderers.Count == 0 ? null : extraRenderers;

    private List<string> Differences(string oldTake, string newTake)
    {
        if (oldTake == newTake)
        {
            return [];
        }

        TakeRecord a = bank.GetTake(oldTake);
        TakeRecord b = bank.GetTake(newTake);
        List<string> diff = [];
        foreach (TakeDataRef missing in a.Data.Where(d => d.Role != "render-recipe" && !b.Data.Any(other => other.Role == d.Role))) diff.Add($"{missing.Role}: output missing");
        foreach (TakeDataRef d in b.Data.Where(d => d.Role != "render-recipe"))
        {
            TakeDataRef? other = a.Data.FirstOrDefault(x => x.Role == d.Role);
            if (other is null)
            {
                diff.Add($"{d.Role}: new output");
            }
            else if (other.DataId != d.DataId)
            {
                diff.Add(Describe(d.Role, other.DataId, d.DataId));
            }
        }

        return diff;
    }

    private string Describe(string role, string oldData, string newData)
    {
        byte[] x = Read(oldData);
        byte[] y = Read(newData);
        if (x.Length > 44 && y.Length > 44 && x[0] == 'R' && y[0] == 'R')
        {
            (short[] s1, _) = Wav.Decode(x);
            (short[] s2, _) = Wav.Decode(y);
            int differing = Enumerable.Range(0, Math.Min(s1.Length, s2.Length)).Count(i => s1[i] != s2[i]);
            return $"{role}: {differing.ToString(CultureInfo.InvariantCulture)} of {Math.Min(s1.Length, s2.Length).ToString(CultureInfo.InvariantCulture)} samples differ, length {s1.Length.ToString(CultureInfo.InvariantCulture)} to {s2.Length.ToString(CultureInfo.InvariantCulture)}, peak {AudioMeter.Format(AudioMeter.PeakDbfs(s1))} to {AudioMeter.Format(AudioMeter.PeakDbfs(s2))}";
        }

        return $"{role}: content differs ({x.Length.ToString(CultureInfo.InvariantCulture)} to {y.Length.ToString(CultureInfo.InvariantCulture)} bytes)";
    }

    private byte[] Read(string dataId)
    {
        using Stream s = bank.OpenData(dataId);
        using MemoryStream ms = new();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private ValueTask Set(string block, string name, bool locked)
    {
        GetSeed(block, name);
        Run(c => Exec(c, "UPDATE c_seeds SET locked = $l WHERE block = $b AND name = $n", ("$l", locked ? 1 : 0), ("$b", block), ("$n", name)));
        return ValueTask.CompletedTask;
    }

    private string Now() => clock.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private T Run<T>(Func<SqliteConnection, T> action)
    {
        try
        {
            using SqliteConnection c = TakeDatabase.Open(dbPath);
            return action(c);
        }
        catch (SqliteException ex)
        {
            int at = ex.Message.IndexOf("AMP52", StringComparison.Ordinal);
            throw at >= 0 ? new AmpleException(ex.Message.Substring(at, 7), ex.Message[(at + 9)..]) : ex;
        }
    }

    private void Run(Action<SqliteConnection> action) => Run<int>(c =>
    {
        action(c);
        return 0;
    });

    private static List<T> Query<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        using SqliteDataReader r = cmd.ExecuteReader();
        List<T> list = [];
        while (r.Read())
        {
            list.Add(read(r));
        }

        return list;
    }

    private static object? Scalar(SqliteConnection c, string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        object? result = cmd.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    private static void Exec(SqliteConnection c, string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        cmd.ExecuteNonQuery();
    }
}

/// <summary>Replaces the selected take of a block with a regenerated one: a dangerous act (dry run, typed acceptance, reason, tombstone). The old take stays in the takebank.</summary>
public sealed class ReplaceTakeAct(ControlService control, string decisionId) : DangerousAct
{
    /// <inheritdoc />
    public override string Title => "Replace the selected take with the regenerated one";

    /// <inheritdoc />
    public override string Acceptance
    {
        get
        {
            (string Id, string Block, string OldTake, string NewTake, IReadOnlyList<string> Differences) pending = control.PendingRegens().FirstOrDefault(p => p.Id == decisionId);
            return pending.Id is null ? throw new AmpleException("AMP5263", $"There is no open decision {decisionId}.") : "replace " + pending.Block;
        }
    }

    /// <inheritdoc />
    protected override string Kind => "regen.replace";

    /// <inheritdoc />
    public override ValueTask<DryRunResult> DryRunAsync(CommandContext context, CancellationToken cancellationToken)
    {
        (string Id, string Block, string OldTake, string NewTake, IReadOnlyList<string> Differences) p = control.PendingRegens().FirstOrDefault(x => x.Id == decisionId);
        return p.Id is null
            ? throw new AmpleException("AMP5263", $"There is no open decision {decisionId}.")
            : ValueTask.FromResult(new DryRunResult([$"'{p.Block}' would use the new take instead of {p.OldTake}.", .. p.Differences], "The old take stays in the takebank and the history; a backup before this is still advisable."));
    }

    /// <inheritdoc />
    protected override ValueTask ExecuteCoreAsync(CommandContext context, CancellationToken cancellationToken)
    {
        control.Replace(decisionId);
        return ValueTask.CompletedTask;
    }
}
