using System.CommandLine;
using System.Text;
using Ample.Core.Abstractions;
using Ample.Audio.IO;
using Ample.Engine.Modules;
using Ample.Storage.Security;
using Ample.Docs;
using Ample.Language;

namespace Ample.Cli;

/// <summary>The <c>ample components</c> commands: what a song can see, the problems of a file of components, and seal, unseal and new-version (docs/language/19-components.md).</summary>
internal static class ComponentCommands
{
    private static readonly SystemClock Clock = new();

    internal static IReadOnlyList<Command> Create(TextWriter output) => [Components(output)];

    private static Option<string?> RootOption() => new("--root") { Description = "The AMPLE folder (default: found from the current folder upwards)." };

    private static Option<string?> VersionOption() => new("--version") { Description = "The version of the component (needed when the file has several)." };

    private static SpecData Spec(string? root) => (root is not null ? SpecRoot.At(root) : SpecRoot.Find(Directory.GetCurrentDirectory())).LoadSpecData();

    private static async Task<int> Guard(TextWriter output, Func<Task<int>> body)
    {
        try
        {
            return await body();
        }
        catch (AmpleException ex)
        {
            output.WriteLine($"{ex.DiagnosticCode}: {ex.Message}");
            return 1;
        }
    }

    private static Command Components(TextWriter output)
    {
        Command root = new("components", "Reusable typed blocks of Notes code: list what a song can see, check a file of components, seal a version, make a new one.");

        Argument<string> listSong = new("song") { Description = "The .amplet file." };
        Option<string?> listRoot = RootOption();
        Option<string?> listProject = new("--project") { Description = "The project folder (default: the folder of the song)." };
        Command list = new("list", "Lists the components a song can see (its own and the imported ones) with every version, its seal and signature.") { listSong, listProject, listRoot };
        list.SetAction((parse, token) => Guard(output, async () =>
        {
            string song = Path.GetFullPath(parse.GetValue(listSong)!);
            string project = Path.GetFullPath(parse.GetValue(listProject) ?? Path.GetDirectoryName(song)!);
            using ModuleService service = ModuleService.WithUserLibrary(project, Clock);
            (IReadOnlyList<ComponentListing> components, IReadOnlyList<Diagnostic> issues) = ComponentCatalog.List(service, await File.ReadAllTextAsync(song, token), Spec(parse.GetValue(listRoot)));
            foreach (ComponentListing c in components)
            {
                output.WriteLine($"{c.Path}  (id {c.Id}; default {c.DefaultVersion}){(c.Tags.Count > 0 ? "  " + string.Join(" ", c.Tags.Select(t => "#" + t)) : string.Empty)}");
                foreach (ComponentVersionInfo v in c.Versions)
                {
                    output.WriteLine($"  {v.Version}{(v.Current ? " current" : string.Empty)}{(v.Prerelease ? " pre-release" : string.Empty)}  {v.Seal}{(v.SealReason.Length > 0 ? " (" + v.SealReason + ")" : string.Empty)}{(v.SignedBy is not null ? "  signed by " + v.SignedBy + (v.SignatureValid == false ? " (SIGNATURE DOES NOT MATCH)" : string.Empty) : string.Empty)}{(v.Deprecated is not null ? "  deprecated" : string.Empty)}  [{v.Origin}]  semantic {ComponentHashing.Abbreviate(v.Semantic)}");
                }
            }

            foreach (Diagnostic d in issues) output.WriteLine($"{song}:{d.Line}: {d.Severity.ToString().ToLowerInvariant()} {d.Code} {d.Message}");
            if (components.Count == 0) output.WriteLine("This song has no components.");
            return issues.Any(i => i.Severity == Severity.Error) ? 1 : 0;
        }));

        Argument<string> checkFile = new("file") { Description = "A .amplec or .amplet file." };
        Option<string?> checkRoot = RootOption();
        Command check = new("check", "Reads a file of components and prints the problems of its components (contract, attributes, defaults, process).") { checkFile, checkRoot };
        check.SetAction((parse, token) => Guard(output, async () =>
        {
            string file = Path.GetFullPath(parse.GetValue(checkFile)!);
            AmpleDocument doc = Parser.Parse(await File.ReadAllTextAsync(file, token), Spec(parse.GetValue(checkRoot)), new ParseOptions { RequireProjectLine = file.EndsWith(".amplet", StringComparison.OrdinalIgnoreCase) });
            foreach (Diagnostic d in doc.Diagnostics) output.WriteLine($"{file}:{d.Line}: {d.Severity.ToString().ToLowerInvariant()} {d.Code} {d.Message}");
            output.WriteLine($"{doc.Components.Count} component(s): {string.Join(", ", doc.Components.Select(c => c.DeclaredName + " " + c.Version))}");
            return doc.Diagnostics.Any(d => d.Severity == Severity.Error) ? 1 : 0;
        }));

        Argument<string> sealFile = new("file") { Description = "The .amplec or .amplet file that holds the component." };
        Argument<string> sealName = new("name") { Description = "The component (its name as declared)." };
        Option<string?> sealVersion = VersionOption();
        Option<bool> sealSign = new("--sign") { Description = "Sign the seal with the active key of the library (the passphrase comes from " + PassphraseVault.PassphraseVariable + ")." };
        Option<string?> sealSigner = new("--signed-by") { Description = "The name written next to the signature (default: the user name)." };
        Option<string?> sealHome = new("--home") { Description = "The library folder that holds the keys (default: the user library)." };
        Option<string?> sealRoot = RootOption();
        Command seal = new("seal", "Seals a version: writes the seal block (payload and semantic hash, optionally a signature). A sealed version is immutable.") { sealFile, sealName, sealVersion, sealSign, sealSigner, sealHome, sealRoot };
        seal.SetAction((parse, token) => Guard(output, async () =>
        {
            string file = Path.GetFullPath(parse.GetValue(sealFile)!);
            string text = await File.ReadAllTextAsync(file, token);
            Func<string, ComponentSignature>? sign = null;
            if (parse.GetValue(sealSign))
            {
                string passphrase = Environment.GetEnvironmentVariable(PassphraseVault.PassphraseVariable) is { Length: > 0 } p ? p : throw new AmpleException("AMP5311", $"Give the passphrase of the signing key in the environment variable {PassphraseVault.PassphraseVariable}.");
                SigningKeys keys = new(parse.GetValue(sealHome) ?? Ample.Storage.Library.Library.DefaultHome());
                string who = parse.GetValue(sealSigner) ?? Environment.UserName;
                sign = payload =>
                {
                    (string _, string publicKey, string signature) = keys.Sign(passphrase, Encoding.UTF8.GetBytes(payload));
                    return new ComponentSignature(who, publicKey, signature);
                };
            }

            ComponentEditResult result = ComponentEdits.Seal(text, parse.GetValue(sealName)!, parse.GetValue(sealVersion), Spec(parse.GetValue(sealRoot)), sign);
            if (!result.Ok) throw new AmpleException("AMP2224", result.Message);
            await File.WriteAllTextAsync(file, result.Text, token);
            output.WriteLine(result.Message);
            return 0;
        }));

        Argument<string> unsealFile = new("file") { Description = "The file that holds the component." };
        Argument<string> unsealName = new("name") { Description = "The component." };
        Option<string?> unsealVersion = VersionOption();
        Option<string?> unsealRoot = RootOption();
        Command unseal = new("unseal", "Removes the seal of a version, so that it can be changed again. Anything that pinned its hash sees the change (AMP2203).") { unsealFile, unsealName, unsealVersion, unsealRoot };
        unseal.SetAction((parse, token) => Guard(output, async () =>
        {
            string file = Path.GetFullPath(parse.GetValue(unsealFile)!);
            ComponentEditResult result = ComponentEdits.Unseal(await File.ReadAllTextAsync(file, token), parse.GetValue(unsealName)!, parse.GetValue(unsealVersion), Spec(parse.GetValue(unsealRoot)));
            if (!result.Ok) throw new AmpleException("AMP2210", result.Message);
            await File.WriteAllTextAsync(file, result.Text, token);
            output.WriteLine(result.Message);
            return 0;
        }));

        Argument<string> newFile = new("file") { Description = "The file that holds the component." };
        Argument<string> newName = new("name") { Description = "The component." };
        Argument<string> newVersionNumber = new("new-version") { Description = "The version of the copy (1.2.1)." };
        Option<string?> newFrom = new("--from") { Description = "The version to copy (needed when the file has several)." };
        Option<string?> newRoot = RootOption();
        Command newVersion = new("new-version", "Makes a working copy of a version with a new version number, without its seal; the original is untouched.") { newFile, newName, newVersionNumber, newFrom, newRoot };
        newVersion.SetAction((parse, token) => Guard(output, async () =>
        {
            string file = Path.GetFullPath(parse.GetValue(newFile)!);
            ComponentEditResult result = ComponentEdits.NewVersion(await File.ReadAllTextAsync(file, token), parse.GetValue(newName)!, parse.GetValue(newFrom), parse.GetValue(newVersionNumber)!, Spec(parse.GetValue(newRoot)));
            if (!result.Ok) throw new AmpleException("AMP2223", result.Message);
            await File.WriteAllTextAsync(file, result.Text, token);
            output.WriteLine(result.Message);
            return 0;
        }));

        // embedding and the source of a component in a song
        Argument<string> songArg = new("song") { Description = "The .amplet file." };
        Argument<string[]> embedNames = new("components") { Description = "The components to embed (their dependencies come with them).", Arity = ArgumentArity.OneOrMore };
        Option<string?> embedModule = new("--module") { Description = "The name of the embedded module (default: the file the first component comes from)." };
        Option<bool> embedWhole = new("--whole") { Description = "Embed everything of the files the components come from." };
        Option<string?> sourceRoot = RootOption();
        Command embed = new("embed", "Embeds components (with their dependency closure) in the song, inside an embedded module that keeps their module boundary.") { songArg, embedNames, embedModule, embedWhole, sourceRoot };
        embed.SetAction((parse, token) => Guard(output, async () =>
        {
            string song = Path.GetFullPath(parse.GetValue(songArg)!);
            string text = await File.ReadAllTextAsync(song, token);
            SpecData spec = Spec(parse.GetValue(sourceRoot));
            using ModuleService service = ModuleService.WithUserLibrary(Path.GetDirectoryName(song)!, Clock);
            (IReadOnlyList<ComponentDefinition> imported, _) = service.ComponentsForEditors(text, spec);
            List<ComponentDefinition> roots = [];
            foreach (string n in parse.GetValue(embedNames)!)
            {
                roots.Add(ComponentVersions.Default(imported.Where(d => d.DeclaredName == n || d.Name == n)) ?? throw new AmpleException("AMP2216", $"No imported file has the component \"{n}\"."));
            }

            IReadOnlyList<ComponentDefinition> closure = parse.GetValue(embedWhole) ? [.. imported.Where(d => roots.Any(r => r.Origin == d.Origin))] : ComponentEmbedding.Closure(imported, roots);
            string module = parse.GetValue(embedModule) ?? (Path.GetFileNameWithoutExtension(roots[0].Origin) is { Length: > 0 } stem ? stem : "embedded");
            ComponentEditResult result = ComponentEmbedding.Embed(text, closure, module, null, null, spec);
            if (!result.Ok) throw new AmpleException("AMP2212", result.Message);
            await File.WriteAllTextAsync(song, result.Text, token);
            output.WriteLine(result.Message);
            return 0;
        }));

        Argument<string> sourceName = new("name") { Description = "The component (as declared)." };
        Option<string?> sourceVersion = VersionOption();
        Command useTemplate = new("use-template", "Uses the template source again for an embedded component. Only when id, version and payload are exactly the same.") { songArg, sourceName, sourceVersion, sourceRoot };
        useTemplate.SetAction((parse, token) => Guard(output, async () =>
        {
            string song = Path.GetFullPath(parse.GetValue(songArg)!);
            string text = await File.ReadAllTextAsync(song, token);
            SpecData spec = Spec(parse.GetValue(sourceRoot));
            using ModuleService service = ModuleService.WithUserLibrary(Path.GetDirectoryName(song)!, Clock);
            (IReadOnlyList<ComponentDefinition> imported, _) = service.ComponentsForEditors(text, spec);
            string name = parse.GetValue(sourceName)!;
            string? version = parse.GetValue(sourceVersion);
            ComponentDefinition template = imported.FirstOrDefault(d => d.DeclaredName == name && (version is null || (SemVer.TryParse(version, out SemVer v) && d.Version.SameAs(v)))) ?? throw new AmpleException("AMP2216", $"No imported file has the component \"{name}\": import the template first.");
            ComponentEditResult result = ComponentEmbedding.UseTemplate(text, template, spec);
            if (!result.Ok) throw new AmpleException("AMP2212", result.Message);
            await File.WriteAllTextAsync(song, result.Text, token);
            output.WriteLine(result.Message);
            return 0;
        }));

        Argument<string> extractFile = new("file") { Description = "The new .amplec file, relative to the song (also the path the song imports it by)." };
        Command extract = new("extract", "Extracts an embedded component to a new .amplec file and makes the song import it. Identity, version and seal are kept.") { songArg, sourceName, extractFile, sourceVersion, sourceRoot };
        extract.SetAction((parse, token) => Guard(output, async () =>
        {
            string song = Path.GetFullPath(parse.GetValue(songArg)!);
            string target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(song)!, parse.GetValue(extractFile)!));
            if (File.Exists(target)) throw new AmpleException("AMP2216", $"{target} exists already: nothing is overwritten.");
            ComponentExtraction result = ComponentEmbedding.Extract(await File.ReadAllTextAsync(song, token), parse.GetValue(sourceName)!, parse.GetValue(sourceVersion), parse.GetValue(extractFile)!, Spec(parse.GetValue(sourceRoot)));
            if (!result.Ok) throw new AmpleException("AMP2208", result.Message);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, result.FileText, token);
            await File.WriteAllTextAsync(song, result.SongText, token);
            output.WriteLine(result.Message);
            return 0;
        }));

        Command detach = new("detach", "Detaches a sealed embedded component for editing: removes its seal, so it builds as a development version.") { songArg, sourceName, sourceVersion, sourceRoot };
        detach.SetAction((parse, token) => Guard(output, async () =>
        {
            string song = Path.GetFullPath(parse.GetValue(songArg)!);
            ComponentEditResult result = ComponentEmbedding.Detach(await File.ReadAllTextAsync(song, token), parse.GetValue(sourceName)!, parse.GetValue(sourceVersion), Spec(parse.GetValue(sourceRoot)));
            if (!result.Ok) throw new AmpleException("AMP2210", result.Message);
            await File.WriteAllTextAsync(song, result.Text, token);
            output.WriteLine(result.Message);
            return 0;
        }));

        Argument<string> migrateBlock = new("block") { Description = "The instance (block name)." };
        Argument<string> migrateTarget = new("target") { Description = "The version to migrate to (4.0.0), or id@version for another component." };
        Option<bool> migrateApply = new("--apply") { Description = "Write the migrated song (without it only the preview is printed)." };
        Option<bool> migrateAcknowledge = new("--acknowledge") { Description = "Accept an impact of musicalChange or destructive (needed with --apply)." };
        Command migrate = new("migrate", "Shows (and with --apply makes) the migration of one instance to another version: the chain, the impact and the changed properties. An impactful migration is never applied without --acknowledge.") { songArg, migrateBlock, migrateTarget, migrateApply, migrateAcknowledge, sourceRoot };
        migrate.SetAction((parse, token) => Guard(output, async () =>
        {
            string song = Path.GetFullPath(parse.GetValue(songArg)!);
            string text = await File.ReadAllTextAsync(song, token);
            SpecData spec = Spec(parse.GetValue(sourceRoot));
            using ModuleService service = ModuleService.WithUserLibrary(Path.GetDirectoryName(song)!, Clock);
            (IReadOnlyList<ComponentDefinition> imported, _) = service.ComponentsForEditors(text, spec);
            MigrationPlan plan = ComponentMigrations.Plan(text, imported, parse.GetValue(migrateBlock)!, parse.GetValue(migrateTarget)!, spec);
            if (!plan.Ok) throw new AmpleException(plan.Problem.StartsWith("AMP2221", StringComparison.Ordinal) ? "AMP2221" : "AMP2222", plan.Problem);
            output.WriteLine($"impact {plan.Impact}{(plan.NeedsAcknowledgement ? " (needs --acknowledge)" : string.Empty)}");
            foreach (MigrationStep step in plan.Steps) output.WriteLine($"  step {step.FromId} {step.From} -> {step.ToId} {step.To}  [{step.Impact}]{(step.Message.Length > 0 ? "  " + step.Message : string.Empty)}");
            foreach (MigrationChange change in plan.Changes) output.WriteLine($"  {change.Kind} {change.Target}{(change.Before is not null || change.After is not null ? $": {change.Before ?? "-"} => {change.After ?? "-"}" : string.Empty)}");
            foreach (string warning in plan.Warnings) output.WriteLine($"  warning: {warning}");
            if (!parse.GetValue(migrateApply)) return 0;
            if (plan.NeedsAcknowledgement && !parse.GetValue(migrateAcknowledge)) throw new AmpleException("AMP2222", $"The impact is {plan.Impact}: look at the preview, then run again with --acknowledge. Nothing was changed.");
            await File.WriteAllTextAsync(song, plan.Text, token);
            output.WriteLine($"Migrated {parse.GetValue(migrateBlock)} to {plan.BlockType} {plan.BlockVersion}.");
            return 0;
        }));

        Argument<string> graphBlock = new("block") { Description = "The instance of an Audio component." };
        Command graph = new("graph", "Prints the internal graph of an instance of an Audio component: its processors with parameters and links, the latency and what the browser can preview.") { songArg, graphBlock, sourceRoot };
        graph.SetAction((parse, token) => Guard(output, async () =>
        {
            string song = Path.GetFullPath(parse.GetValue(songArg)!);
            using ModuleService service = ModuleService.WithUserLibrary(Path.GetDirectoryName(song)!, Clock);
            (ComponentDefinition definition, AudioGraph audio) = ComponentCatalog.GraphOf(service, await File.ReadAllTextAsync(song, token), Spec(parse.GetValue(sourceRoot)), parse.GetValue(graphBlock)!);
            output.WriteLine($"{definition.DeclaredName} {definition.Version} ({definition.Id})");
            foreach (AudioNode n in audio.Nodes)
            {
                output.WriteLine($"  {n.Id}  {n.Template}@{n.Version}  {string.Join(", ", n.Parameters.Select(p => p.Name + "=" + p.Value))}");
                foreach ((string port, string source) in n.Inputs) output.WriteLine($"    {port} <- {source}");
            }

            foreach ((string name, string source) in audio.Outputs) output.WriteLine($"  output {name} <- {source}");
            output.WriteLine($"  latency {audio.LatencyMs} ms (lined up inside the component; between tracks with settings {{ audio.latencyCompensation on }}); browser preview: {(audio.ClientPreview ? "yes" : "no (" + string.Join(", ", audio.NoPreview) + ")")}");
            foreach (ComponentIssue i in audio.Issues) output.WriteLine($"  {i.Severity.ToString().ToLowerInvariant()} {i.Code} line {i.Line + 1}: {i.Message}");
            return audio.Issues.Any(i => i.Severity == Severity.Error) ? 1 : 0;
        }));

        foreach (Command c in new[] { list, check, seal, unseal, newVersion, embed, useTemplate, extract, detach, migrate, graph })
        {
            root.Subcommands.Add(c);
        }

        return root;
    }
}
