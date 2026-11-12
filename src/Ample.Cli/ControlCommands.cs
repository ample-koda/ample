using System.CommandLine;
using System.Globalization;
using Ample.Audio.IO;
using Ample.Audio.Random;
using Ample.Core.Abstractions;
using Ample.Docs;
using Ample.Engine.Control;
using Ample.Engine.Engine;
using Ample.Language;
using Ample.Storage.BlackBox;
using Ample.Storage.Projects;
using Ample.Storage.Takes;

namespace Ample.Cli;

/// <summary>The producer's controls: seed, variants, choose, regen, verify-reproduction and explain.</summary>
internal static class ControlCommands
{
    private static readonly PhysicalFileSystem Fs = new();
    private static readonly SystemClock Clock = new();

    internal static IReadOnlyList<Command> Create(TextWriter output)
    {
        Argument<string> song = new("song") { Description = "The .amplet song file." };
        Argument<string> block = new("block") { Description = "The block." };
        Option<string?> project = new("--project") { Description = "The project folder (default: found above the song)." };
        Option<string?> root = new("--root") { Description = "The AMPLE folder with spec/." };
        Option<string> reason = new("--reason") { Description = "Why (recorded in the history).", DefaultValueFactory = _ => "changed by hand" };

        Option<bool> unlock = new("--unlock") { Description = "Unlock the seed (changing it probably makes reproduction impossible)." };
        Option<bool> lockIt = new("--lock") { Description = "Lock the seed." };
        Option<ulong?> set = new("--set") { Description = "Set the seed (needs it to be unlocked)." };
        Option<bool> randomize = new("--randomize") { Description = "Give the seed a new derived value (needs it to be unlocked)." };
        Command seed = new("seed", "Shows or changes the seed of a block. Seeds are locked by default.") { song, block, project, root, unlock, lockIt, set, randomize, reason };
        seed.SetAction(async (p, t) => await With(output, p.GetValue(song)!, p.GetValue(project), p.GetValue(root), async (c, doc) =>
        {
            string b = p.GetValue(block)!;
            if (p.GetValue(unlock))
            {
                await c.UnlockSeedAsync(b, "seed");
            }

            if (p.GetValue(set) is ulong value)
            {
                await c.SetSeedAsync(b, "seed", value, p.GetValue(reason)!);
            }

            if (p.GetValue(randomize))
            {
                await c.RandomizeSeedAsync(b, "seed", p.GetValue(reason)!);
            }

            if (p.GetValue(lockIt))
            {
                await c.LockSeedAsync(b, "seed");
            }

            SeedState s = c.GetSeed(b, "seed");
            output.WriteLine($"seed of {b}: {s.Value.ToString(CultureInfo.InvariantCulture)} ({s.Origin}, {(s.Locked ? "locked" : "UNLOCKED: changing it probably makes reproduction impossible")})");
            foreach (SeedChange h in c.SeedHistory(b, "seed"))
            {
                output.WriteLine($"  {h.At:u}  {h.OldValue.ToString(CultureInfo.InvariantCulture)} -> {h.NewValue.ToString(CultureInfo.InvariantCulture)}  {h.Reason}");
            }

            return 0;
        }));

        Option<int> count = new("--count") { Description = "How many.", DefaultValueFactory = _ => 3 };
        Option<string> kind = new("--kind") { Description = "variant, resing (new voice, same melody) or rewrite (new melody).", DefaultValueFactory = _ => "variant" };
        Command variants = new("variants", "Makes several takes of a block to choose from (the same request gives the same variants) and lists them.") { song, block, project, root, count, kind };
        variants.SetAction(async (p, t) => await With(output, p.GetValue(song)!, p.GetValue(project), p.GetValue(root), async (c, doc) =>
        {
            string b = p.GetValue(block)!;
            await c.RequestVariantsAsync(doc, b, p.GetValue(count), p.GetValue(kind)!, t);
            foreach (VariantInfo v in c.Variants(b))
            {
                output.WriteLine($"{v.Index.ToString(CultureInfo.InvariantCulture),3}  {v.State,-9}  {v.TakeId}");
            }

            return 0;
        }));

        Argument<string> take = new("take") { Description = "A take id or unique prefix." };
        Command choose = new("choose", "Selects a take for a block (it is used when the block is baked).") { song, block, take, project, root };
        choose.SetAction(async (p, t) => await With(output, p.GetValue(song)!, p.GetValue(project), p.GetValue(root), async (c, doc) =>
        {
            string b = p.GetValue(block)!;
            string wanted = p.GetValue(take)!;
            List<VariantInfo> hits = [.. c.Variants(b).Where(v => v.TakeId.StartsWith(wanted, StringComparison.Ordinal))];
            if (hits.Count != 1)
            {
                output.WriteLine($"AMP5223: '{wanted}' matches {hits.Count.ToString(CultureInfo.InvariantCulture)} variants of {b}.");
                return 1;
            }

            await c.SelectTakeAsync(b, hits[0].TakeId);
            output.WriteLine($"Selected {hits[0].TakeId} for {b}.");
            return 0;
        }));

        Option<ulong?> newSeed = new("--seed") { Description = "Seed for the regeneration (default: derived)." };
        Option<string?> decide = new("--decide") { Description = "Answer an open regeneration decision (its id)." };
        Option<bool> keepVariant = new("--keep-variant") { Description = "With --decide: keep the new take as a variant (default: restore the previous take)." };
        Command regen = new("regen", "Regenerates a block safely: the selected take is kept, the new result is verified, and a difference becomes a decision whose default is to restore the previous take.") { song, block, project, root, newSeed, decide, keepVariant };
        regen.SetAction(async (p, t) => await With(output, p.GetValue(song)!, p.GetValue(project), p.GetValue(root), async (c, doc) =>
        {
            if (p.GetValue(decide) is string id)
            {
                await c.DecideAsync(id, p.GetValue(keepVariant) ? "keep-variant" : "restore");
                output.WriteLine(p.GetValue(keepVariant) ? "Kept the new take as a variant; the previous take stays selected." : "Restored: the previous take stays selected.");
                return 0;
            }

            output.WriteLine(ControlService.RegenerationWarning);
            RegenResult r = await c.RegenerateAsync(doc, p.GetValue(block)!, p.GetValue(newSeed), t);
            if (r.Status == "same")
            {
                output.WriteLine("Same result. Nothing changed.");
                return 0;
            }

            foreach (string d in r.Differences)
            {
                output.WriteLine("  " + d);
            }

            output.WriteLine($"The new result differs. The previous take stays selected. Decide with: ample regen {p.GetValue(song)} {p.GetValue(block)} --decide {r.Id} [--keep-variant]  (default: restore the previous take)");
            return 0;
        }));

        Command verify = new("verify-reproduction", "Re-renders the selected take's own plan only to check reproduction; never replaces anything.") { song, block, project, root };
        verify.SetAction(async (p, t) => await With(output, p.GetValue(song)!, p.GetValue(project), p.GetValue(root), async (c, doc) =>
        {
            RegenResult r = await c.VerifyReproductionAsync(doc, p.GetValue(block)!, t);
            foreach (string warning in r.Warnings) output.WriteLine("Warning: " + warning);
            output.WriteLine(r.Status == "same" ? "Reproduction verified: the same result." : "The result is different: " + string.Join("; ", r.Differences));
            return r.Status == "same" ? 0 : 1;
        }));

        Command explain = new("explain", "Says what each kind of edit does to a block at its freeze level.") { song, block, project, root };
        explain.SetAction(async (p, t) => await With(output, p.GetValue(song)!, p.GetValue(project), p.GetValue(root), (c, doc) =>
        {
            BlockNode b = doc.Blocks.FirstOrDefault(x => x.Name == p.GetValue(block)) ?? throw new AmpleException("AMP5243", $"There is no block '{p.GetValue(block)}'.");
            Explainer e = c.Explain(b);
            output.WriteLine($"{b.Name}: freeze level {e.Level}");
            foreach (string w in e.Warnings)
            {
                output.WriteLine("  " + w);
            }

            foreach (EditEffect ef in e.Effects)
            {
                output.WriteLine($"  {ef.Edit}: {ef.Outcome}");
            }

            return Task.FromResult(0);
        }));

        return [seed, variants, choose, regen, verify, explain];
    }

    private static async Task<int> With(TextWriter output, string songPath, string? projectFolder, string? rootFolder, Func<ControlService, AmpleDocument, Task<int>> action)
    {
        try
        {
            string songFile = Path.GetFullPath(songPath);
            string projectRoot = projectFolder is not null ? Path.GetFullPath(projectFolder) : FindProject(Path.GetDirectoryName(songFile)!);
            SpecData spec = (rootFolder is null ? SpecRoot.Find(Directory.GetCurrentDirectory()) : SpecRoot.At(rootFolder)).LoadSpecData();
            using Ample.Engine.Modules.ModuleService modules = Ample.Engine.Modules.ModuleService.WithUserLibrary(projectRoot, Clock);
            AmpleDocument doc = modules.ParseExpanded(await File.ReadAllTextAsync(songFile), spec);
            using OpenProject open = await ProjectStore.OpenAsync(Fs, Clock, projectRoot);
            using Journal journal = await Journal.OpenAsync(Fs, Clock, projectRoot, open.Manifest.Id.ToString("D"));
            Takebank bank = Takebank.Open(Fs, Clock, projectRoot);
            CommandBus bus = new(journal, new CommandContext(Fs, projectRoot, Clock), "cli");
            ControlService control = new(Clock, projectRoot, bank, new BuildEngine(bank, spec, open.Manifest.Id, projectRoot), bus, (ulong)(doc.Project?.MasterSeed ?? 0));
            return await action(control, doc);
        }
        catch (AmpleException ex)
        {
            output.WriteLine($"{ex.DiagnosticCode}: {ex.Message}");
            return 1;
        }
    }

    private static string FindProject(string start)
    {
        for (DirectoryInfo? dir = new(start); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile)))
            {
                return dir.FullName;
            }
        }

        throw new AmpleException("AMP5210", $"No AMPLE project found at or above '{start}'.");
    }
}
