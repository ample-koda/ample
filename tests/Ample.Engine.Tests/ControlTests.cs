using Ample.Core.Abstractions;
using Ample.Audio.Engine;
using Ample.Audio.IO;
using Ample.Audio.Random;
using Ample.Engine.Control;
using Ample.Engine.Engine;
using Ample.Storage.BlackBox;
using Ample.Storage.Projects;
using Ample.Storage.Takes;
using Ample.Language;

namespace Ample.Core.Tests;

internal sealed class LambdaRenderer(string type, string determinism, Func<RenderRequest, IReadOnlyList<RenderOutput>> render) : IBlockRenderer
{
    public string BlockType => type;

    public string Id => "test." + type;

    public string Version => "1";

    public string Determinism => determinism;

    public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(render(request));
}

internal sealed class ControlRig : IDisposable
{
    private readonly string folder;
    private readonly OpenProject project;
    public readonly string Root;
    public readonly Takebank Bank;
    public readonly BuildEngine Engine;
    public readonly Journal Journal;
    public readonly CommandBus Bus;
    public readonly IReadOnlyList<IBlockRenderer> Extras;
    public ControlService Control;

    private ControlRig(string folder, OpenProject project, string root, Takebank bank, BuildEngine engine, Journal journal, CommandBus bus, IReadOnlyList<IBlockRenderer> extras, ControlService control)
    {
        this.folder = folder;
        this.project = project;
        Root = root;
        Bank = bank;
        Engine = engine;
        Journal = journal;
        Bus = bus;
        Extras = extras;
        Control = control;
    }

    public static async Task<ControlRig> CreateAsync(params IBlockRenderer[] extras)
    {
        string folder = Directory.CreateTempSubdirectory("ample-control-").FullName;
        string root = Path.Combine(folder, "proj");
        PhysicalFileSystem fs = new();
        SystemClock clock = new();
        OpenProject project = await ProjectStore.CreateAsync(fs, clock, new GuidV7Generator(), root, "control");
        Takebank bank = Takebank.Open(fs, clock, root);
        BuildEngine engine = new(bank, EngineRig.Spec, project.Manifest.Id, root);
        Journal journal = await Journal.OpenAsync(fs, clock, root, "p");
        CommandBus bus = new(journal, new CommandContext(fs, root, clock), "test");
        return new ControlRig(folder, project, root, bank, engine, journal, bus, extras, new ControlService(clock, root, bank, engine, bus, 7, extras));
    }

    public ControlService Restart() => Control = new ControlService(new SystemClock(), Root, Bank, Engine, Bus, 7, Extras);

    public BuildOptions Options(string? only = null) => new(Only: only, ExtraRenderers: Extras.Count == 0 ? null : Extras, Control: Control);

    public async Task<string> BaselineDrumsAsync(AmpleDocument doc)
    {
        BuildReport report = await Engine.BuildAsync(doc, Options());
        string take = report.Blocks.Single(b => b.Block == "drums").TakeId!;
        await Control.SelectTakeAsync("drums", take);
        return take;
    }

    public void Dispose()
    {
        Journal.Dispose();
        project.Dispose();
        Directory.Delete(folder, recursive: true);
    }
}

[Trait("Category", "Control")]
public sealed class ControlTests
{
    private static AmpleDocument Doc(string? text = null) => EngineRig.Parse(text ?? EngineRig.Hello);

    [Fact]
    public async Task Seeds_are_locked_by_default_changes_are_kept_and_the_engine_uses_the_new_seed()
    {
        using ControlRig r = await ControlRig.CreateAsync();
        SeedState s = r.Control.GetSeed("drums", "seed");
        Assert.True(s.Locked);
        Assert.Equal(SeedDeriver.Derive(7, "block", "drums", "seed", "seed"), s.Value);

        AmpleException refused = await Assert.ThrowsAsync<AmpleException>(async () => await r.Control.SetSeedAsync("drums", "seed", 5, "try"));
        Assert.Equal("AMP5260", refused.DiagnosticCode);
        Assert.Contains("probably makes reproduction", refused.Message, StringComparison.Ordinal);

        BuildReport before = await r.Engine.BuildAsync(Doc(), r.Options());
        await r.Control.UnlockSeedAsync("drums", "seed");
        await r.Control.SetSeedAsync("drums", "seed", 5, "another take");
        await r.Control.LockSeedAsync("drums", "seed");
        Assert.Equal(5UL, r.Control.SeedFor("drums"));
        SeedChange change = Assert.Single(r.Control.SeedHistory("drums", "seed"));
        Assert.Equal((s.Value, 5UL, "another take"), (change.OldValue, change.NewValue, change.Reason));
        Assert.Equal(SeedOrigin.UserSet, r.Control.GetSeed("drums", "seed").Origin);

        BuildReport after = await r.Engine.BuildAsync(Doc(), r.Options());
        Assert.NotEqual(before.MasterTakeId, after.MasterTakeId);
        Assert.Equal(5UL, r.Bank.GetPlan(r.Bank.GetTake(after.Blocks.Single(b => b.Block == "drums").TakeId!).PlanId).Plan.Seeds.Single().Value);

        await Assert.ThrowsAsync<AmpleException>(async () => await r.Control.RandomizeSeedAsync("drums", "seed", "again"));
        await r.Control.UnlockSeedAsync("drums", "seed");
        await r.Control.RandomizeSeedAsync("drums", "seed", "roll");
        Assert.Equal(SeedDeriver.Derive(7, "block", "drums", "seed-attempt", "seed", 2UL), r.Control.GetSeed("drums", "seed").Value);
        Assert.Equal(2, r.Control.SeedHistory("drums", "seed").Count);
    }

    [Fact]
    public async Task Variants_are_reproducible_selectable_keepable_and_discardable()
    {
        using ControlRig r = await ControlRig.CreateAsync();
        AmpleDocument doc = Doc();
        IReadOnlyList<TakeRecord> made = await r.Control.RequestVariantsAsync(doc, "drums", 3);
        Assert.Equal(3, made.Select(t => t.TakeId).Distinct().Count());
        for (int i = 0; i < 3; i++)
        {
            StoredPlan plan = r.Bank.GetPlan(made[i].PlanId);
            Assert.Equal("variant", plan.Plan.Intent);
            Assert.Equal(SeedDeriver.Derive(7, "block", "drums", "variant", (ulong)i), plan.Plan.Seeds.Single().Value);
        }

        int attempts = r.Bank.ListAttempts().Count;
        IReadOnlyList<TakeRecord> again = await r.Control.RequestVariantsAsync(doc, "drums", 3);
        Assert.Equal(made.Select(t => t.TakeId), again.Select(t => t.TakeId));
        Assert.Equal(attempts, r.Bank.ListAttempts().Count);

        await r.Control.SelectTakeAsync("drums", made[1].TakeId);
        Assert.Equal(["candidate", "selected", "candidate"], r.Control.Variants("drums").Select(v => v.State));
        Assert.Equal("AMP5262", (await Assert.ThrowsAsync<AmpleException>(async () => await r.Control.DiscardAsync("drums", made[1].TakeId, "no"))).DiagnosticCode);
        await r.Control.KeepAsync("drums", made[0].TakeId);
        await r.Control.DiscardAsync("drums", made[2].TakeId, "too busy");
        Assert.Equal(["kept", "selected", "discarded"], r.Control.Variants("drums").Select(v => v.State));
        Assert.True(r.Bank.GetTake(made[2].TakeId).Tombstoned);
        Assert.Equal(made[2].PlanId, r.Bank.GetPlan(made[2].PlanId).PlanId);
    }

    [Fact]
    public void Variant_field_proposals_are_repeatable_and_only_offer_supported_output_fields()
    {
        BlockNode block = new() { Type = "voice", Name = "lead" };
        block.Parameters.Add(new Parameter("solver", "euler", 0));
        block.Parameters.Add(new Parameter("temperature", "0.8", 1));
        TemplateSpec template = new("voice", "Voice", "live", "1", true, null, [], [], new Dictionary<string, string>(), new Dictionary<string, TemplateParameterSpec>
        {
            ["solver"] = new("solver", "Solver", "text", "euler", null, false, false, "choice", ["euler", "sde"]),
            ["temperature"] = new("temperature", "Temperature", "number", "0.8", null, false, false, "number", Min: 0, Max: 2, Step: 0.01),
            ["prompt"] = new("prompt", "Prompt", "text", "", null, true, true),
        }, "test");

        string[] names = ["solver", "temperature", "$seed"];
        IReadOnlyList<VariantField> available = VariantFieldPlanner.Fields(block, template);
        Assert.Equal(names, available.Select(field => field.Name));
        Assert.DoesNotContain(available, field => field.Name == "prompt");
        IReadOnlyList<IReadOnlyList<VariantFieldChange>> first = VariantFieldPlanner.Propose(42, block, template, 2, 0, "variant", names);
        IReadOnlyList<IReadOnlyList<VariantFieldChange>> again = VariantFieldPlanner.Propose(42, block, template, 2, 0, "variant", names);
        IReadOnlyList<IReadOnlyList<VariantFieldChange>> secondPair = VariantFieldPlanner.Propose(42, block, template, 2, 2, "variant", names);
        Assert.Equal(first.SelectMany(proposal => proposal), again.SelectMany(proposal => proposal));
        Assert.Equal(VariantFieldPlanner.Propose(42, block, template, 4, 0, "variant", names).Skip(2), secondPair);
        Assert.All(first, proposal => Assert.All(proposal, change => Assert.NotEqual(change.From, change.To)));
        Assert.Equal("0.8", block.Parameters.Single(parameter => parameter.Name == "temperature").Value);
    }

    [Fact]
    public async Task A_frozen_take_is_used_without_rendering_and_missing_data_is_reported_not_re_rendered()
    {
        using ControlRig r = await ControlRig.CreateAsync();
        IReadOnlyList<TakeRecord> made = await r.Control.RequestVariantsAsync(Doc(), "drums", 2);
        await r.Control.SelectTakeAsync("drums", made[1].TakeId);
        AmpleDocument baked = Doc(EngineRig.Hello.Replace("drums drums at 1 for 4 {", "drums drums at 1 for 4 bake {", StringComparison.Ordinal));
        Assert.Equal(FreezeLevel.Take, ControlService.LevelOf(baked.Blocks.Single(b => b.Name == "drums")));

        BuildReport report = await r.Engine.BuildAsync(baked, r.Options());
        BlockResult drums = report.Blocks.Single(b => b.Block == "drums");
        Assert.Equal("frozen", drums.Status);
        Assert.Equal(made[1].TakeId, drums.TakeId);

        int attempts = r.Bank.ListAttempts().Count;
        string hex = r.Bank.GetTake(made[1].TakeId).Data[0].DataId["sha256:".Length..];
        File.Delete(Path.Combine(r.Root, ".ample", "data", hex[..2], hex));
        BuildReport missing = await r.Engine.BuildAsync(baked, r.Options("drums"));
        Assert.Equal("failed", missing.Blocks.Single().Status);
        Assert.Contains("AMP5221", missing.Blocks.Single().Message, StringComparison.Ordinal);
        Assert.Equal(attempts, r.Bank.ListAttempts().Count);
    }

    [Fact]
    public async Task Regeneration_keeps_the_old_take_verifies_and_asks_with_restore_as_the_default_even_after_a_restart()
    {
        using ControlRig r = await ControlRig.CreateAsync();
        AmpleDocument doc = Doc();
        string old = await r.BaselineDrumsAsync(doc);

        RegenResult result = await r.Control.RegenerateAsync(doc, "drums");
        Assert.Equal("pending", result.Status);
        Assert.Contains(result.Differences, d => d.Contains("samples differ", StringComparison.Ordinal));
        Assert.Equal(old, r.Control.SelectedTake("drums"));
        Assert.Equal(r.Bank.GetTake(old).PlanId, r.Bank.GetPlan(r.Bank.GetTake(result.NewTake).PlanId).Plan.ParentPlanId);
        Assert.Contains(await r.Journal.ReadAllAsync(), x => x.Kind == "regen.started");

        ControlService restarted = r.Restart();
        Assert.Equal(old, restarted.SelectedTake("drums"));
        Assert.Equal(result.Id, Assert.Single(restarted.PendingRegens()).Id);

        await restarted.DecideAsync(result.Id);
        Assert.Empty(restarted.PendingRegens());
        Assert.Equal(old, restarted.SelectedTake("drums"));
        Assert.True(r.Bank.GetTake(result.NewTake).TakeId.Length > 0);
        Assert.Equal("AMP5263", (await Assert.ThrowsAsync<AmpleException>(async () => await restarted.DecideAsync(result.Id))).DiagnosticCode);

        ulong oldSeed = r.Bank.GetPlan(r.Bank.GetTake(old).PlanId).Plan.Seeds.Single().Value;
        RegenResult same = await restarted.RegenerateAsync(doc, "drums", oldSeed);
        Assert.Equal("same", same.Status);
        Assert.Empty(restarted.PendingRegens());
        Assert.Equal("same", restarted.Reproductions(old)[^1].Result);
    }

    [Fact]
    public async Task Replacing_the_take_is_a_dangerous_act_and_keep_variant_keeps_the_new_take_selectable()
    {
        using ControlRig r = await ControlRig.CreateAsync();
        AmpleDocument doc = Doc();
        string old = await r.BaselineDrumsAsync(doc);
        RegenResult first = await r.Control.RegenerateAsync(doc, "drums");
        await r.Control.DecideAsync(first.Id, "keep-variant");
        Assert.Equal(old, r.Control.SelectedTake("drums"));
        Assert.Contains(r.Control.Variants("drums"), v => v.TakeId == first.NewTake && v.State == "kept");

        RegenResult second = await r.Control.RegenerateAsync(doc, "drums");
        ReplaceTakeAct act = new(r.Control, second.Id);
        CommandContext context = new(new PhysicalFileSystem(), r.Root, new SystemClock());
        Assert.Equal("replace drums", act.Acceptance);
        Assert.Contains(second.Differences[0], (await act.DryRunAsync(context, CancellationToken.None)).Impact);
        Assert.Equal("AMP5232", (await Assert.ThrowsAsync<AmpleException>(async () => await act.ExecuteAsync(r.Journal, context, "user", "yes", "better"))).DiagnosticCode);
        Assert.Equal(old, r.Control.SelectedTake("drums"));

        await act.ExecuteAsync(r.Journal, context, "user", act.Acceptance, "the new one breathes better");
        Assert.Equal(second.NewTake, r.Control.SelectedTake("drums"));
        Assert.Empty(r.Control.PendingRegens());
        Assert.Equal(old, r.Bank.GetTake(old).TakeId);
        Assert.Contains(await r.Journal.ReadAllAsync(), x => x.Kind == "dangerous:regen.replace" && x.Phase == "tombstone");
        Assert.Equal("AMP5263", (await Assert.ThrowsAsync<AmpleException>(async () => await r.Control.DecideAsync(second.Id, "replace"))).DiagnosticCode);
    }

    [Fact]
    public async Task Reproduction_checks_say_same_for_exact_renderers_and_different_for_unverified_ones()
    {
        int counter = 0;
        LambdaRenderer wobbly = new("drums", "unverified", r => [new RenderOutput("audio", Wav.Encode(new short[r.BarCount * r.SamplesPerBar], r.SampleRate).Concat(BitConverter.GetBytes(++counter)).ToArray())]);
        using ControlRig exact = await ControlRig.CreateAsync();
        AmpleDocument doc = Doc();
        await exact.BaselineDrumsAsync(doc);
        RegenResult ok = await exact.Control.VerifyReproductionAsync(doc, "drums");
        Assert.Equal("same", ok.Status);
        Assert.Equal("exact", exact.Control.DeterminismOf("drums", exact.Control.SelectedTake("drums")!));

        using ControlRig loose = await ControlRig.CreateAsync(wobbly);
        string take = await loose.BaselineDrumsAsync(doc);
        Assert.Equal("unverified", loose.Control.DeterminismOf("drums", take));
        RegenResult bad = await loose.Control.VerifyReproductionAsync(doc, "drums");
        Assert.Equal("different", bad.Status);
        Assert.Equal("different", loose.Control.Reproductions(take)[^1].Result);
        Assert.Equal(take, loose.Control.SelectedTake("drums"));
        Assert.Equal("unverified", loose.Control.DeterminismOf("drums", take));
    }

    [Fact]
    public async Task Reproduction_compares_audio_and_warns_on_changed_provenance_without_replacing_the_reference()
    {
        string metadata = "{\"software\":{\"version\":\"old\"}}";
        LambdaRenderer renderer = new("drums", "unverified", r => [new RenderOutput("audio", Wav.Encode(new short[r.BarCount * r.SamplesPerBar], r.SampleRate)) { MetadataJson = metadata }]);
        using ControlRig rig = await ControlRig.CreateAsync(renderer);
        AmpleDocument doc = Doc();
        string reference = await rig.BaselineDrumsAsync(doc);
        Assert.Contains("old", rig.Bank.GetTake(reference).MetaJson, StringComparison.Ordinal);
        metadata = "{\"software\":{\"version\":\"new\"}}";
        RegenResult result = await rig.Control.VerifyReproductionAsync(doc, "drums");
        Assert.Equal("same", result.Status);
        Assert.Contains(result.Warnings, warning => warning.Contains("provenance", StringComparison.Ordinal));
        Assert.Equal(reference, rig.Control.SelectedTake("drums"));
    }

    [Fact]
    public async Task The_explainer_names_the_freeze_level_the_notices_and_the_effect_of_each_edit()
    {
        LambdaRenderer wobbly = new("drums", "unverified", r => []);
        using ControlRig r = await ControlRig.CreateAsync(wobbly);
        Explainer free = r.Control.Explain(Doc().Blocks.Single(b => b.Name == "bass"));
        Assert.Equal(FreezeLevel.Free, free.Level);
        Assert.Empty(free.Warnings);
        Assert.Contains(new EditEffect("regenerate", "free"), free.Effects);

        Explainer frozen = r.Control.Explain(Doc(EngineRig.Hello.Replace("bass bass at 1 for 4 {", "bass bass at 1 for 4 bake {", StringComparison.Ordinal)).Blocks.Single(b => b.Name == "bass"));
        Assert.Equal(FreezeLevel.Take, frozen.Level);
        Assert.Contains(new EditEffect("change a seed", "ignored"), frozen.Effects);

        Explainer seeds = r.Control.Explain(Doc(EngineRig.Hello.Replace("bass bass at 1 for 4 {", "bass bass at 1 for 4 freeze seeds {", StringComparison.Ordinal)).Blocks.Single(b => b.Name == "bass"));
        Assert.Contains(new EditEffect("change a seed", "breaks-reproduction"), seeds.Effects);
        Explainer plan = r.Control.Explain(Doc(EngineRig.Hello.Replace("bass bass at 1 for 4 {", "bass bass at 1 for 4 freeze plan {", StringComparison.Ordinal)).Blocks.Single(b => b.Name == "bass"));
        Assert.Contains(new EditEffect("change the voice seed", "free"), plan.Effects);

        Explainer risky = r.Control.Explain(Doc().Blocks.Single(b => b.Name == "drums"));
        Assert.Contains(risky.Warnings, w => w.StartsWith("AMP5101", StringComparison.Ordinal));
        Assert.Contains(risky.Warnings, w => w.StartsWith("AMP5102", StringComparison.Ordinal));
        Assert.Contains("only guaranteed", ControlService.RegenerationWarning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Freeze_plan_pins_the_stored_melody_and_adds_it_to_the_plan()
    {
        List<string> received = [];
        LambdaRenderer voice = new("bass", "exact", r =>
        {
            if (r.Inputs.TryGetValue("melody", out byte[]? m))
            {
                received.Add(System.Text.Encoding.UTF8.GetString(m));
            }

            return [new RenderOutput("audio", Wav.Encode(new short[r.BarCount * r.SamplesPerBar], r.SampleRate)), new RenderOutput("melody", System.Text.Encoding.UTF8.GetBytes("melody-1"))];
        });
        using ControlRig r = await ControlRig.CreateAsync(voice);
        BuildReport first = await r.Engine.BuildAsync(Doc(), r.Options());
        string bassTake = first.Blocks.Single(b => b.Block == "bass").TakeId!;
        await r.Control.SelectTakeAsync("bass", bassTake);
        Assert.Empty(received);

        AmpleDocument planFrozen = Doc(EngineRig.Hello.Replace("bass bass at 1 for 4 {", "bass bass at 1 for 4 freeze plan {", StringComparison.Ordinal));
        BuildReport second = await r.Engine.BuildAsync(planFrozen, r.Options());
        Assert.Equal(["melody-1"], received);
        StoredPlan plan = r.Bank.GetPlan(r.Bank.GetTake(second.Blocks.Single(b => b.Block == "bass").TakeId!).PlanId);
        Assert.Contains(plan.Plan.Inputs, i => i.Kind == "data" && i.Id == r.Control.PinnedMelodyDataId("bass"));
    }
}
