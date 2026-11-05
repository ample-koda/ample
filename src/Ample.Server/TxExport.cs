using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Ample.Core.Abstractions;
using Ample.Audio.Engine;
using Ample.Audio.IO;
using Ample.Engine.Control;
using Ample.Engine.Engine;
using Ample.Storage.Package;
using Ample.Storage.Projects;
using Ample.Storage.Takes;
using Ample.Language;

namespace Ample.Server;

/// <summary>What the user asks for when exporting an <c>.ampletx</c> package.</summary>
/// <param name="Path">The saved song to export.</param>
/// <param name="Mode"><c>full</c> or <c>derived</c>.</param>
/// <param name="IncludeMaster">Whether the final master goes in as a preview.</param>
/// <param name="Compress">Whether the package is also put in a zip.</param>
/// <param name="Folder">The project folder the package is written to (default <c>exports</c>).</param>
/// <param name="Name">The file name without extension.</param>
/// <param name="WholeSets">Whether whole sample sets and SoundFonts go in; by default only what this build uses (the presets, instruments and samples it played).</param>
/// <param name="Flac">Whether the rendered audio inputs of a Derived export are stored as FLAC (needs the approval of a listening test; there is no guarantee that the audio stays identical).</param>
/// <param name="FlacApproval">The id of the approved FLAC listening test.</param>
internal sealed record TxExportRequest(string Path, string Mode, bool IncludeMaster, bool Compress, string? Folder, string? Name, bool WholeSets = false, bool Flac = false, string? FlacApproval = null);

/// <summary>One file that goes into the package.</summary>
internal sealed class TxAsset
{
    public string EntryPath { get; init; } = string.Empty;

    public string FullPath { get; init; } = string.Empty;

    public string OriginalPath { get; init; } = string.Empty;

    public string Role { get; init; } = AmpleTx.Roles.Source;

    public string Kind { get; init; } = "project";

    public long Size { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public SortedSet<string> UsedBy { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets whether the file belongs to a sample set (a folder that is played by note or by name).</summary>
    public bool IsSetFile { get; set; }

    /// <summary><c>whole</c> or <c>needed</c> (only the part of it this build uses).</summary>
    public string Selection { get; set; } = "whole";

    /// <summary>Gets or sets why the whole file is in although only a part is needed.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>The SoundFont made of the needed presets, when the file is a subset.</summary>
    public string? SubsetFile { get; set; }

    /// <summary>Gets or sets the SHA-256 of the original SoundFont when the file in the package is a subset of it.</summary>
    public string? SubsetOf { get; set; }

    public long OriginalSize { get; set; }

    public string OriginalFileName { get; set; } = string.Empty;

    public int SubsetInstruments { get; set; }

    public int SubsetSamples { get; set; }

    public List<string> Presets { get; } = [];

    /// <summary>The file the package reads its content from.</summary>
    public string Source => SubsetFile ?? FullPath;
}

/// <summary>One rendered file of a Derived export.</summary>
internal sealed class TxDerivedAsset
{
    public TxCut Cut { get; init; } = null!;

    public string InputName { get; init; } = string.Empty;

    public string File { get; init; } = string.Empty;

    public string TempFile { get; set; } = string.Empty;

    public string DataId { get; init; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;

    public long Size { get; set; }

    public int Bars { get; set; }

    public double Seconds { get; set; }

    public bool Identity { get; set; }

    public List<string> Baked { get; set; } = [];

    public List<string> Keys { get; } = [];
}

/// <summary>Everything an export knows before it writes: it is the same for the preview and for the real export, so the preview never promises something else.</summary>
internal sealed class TxPrepared
{
    public TxExportRequest Request { get; init; } = null!;

    public DocSession Doc { get; init; } = null!;

    public AmpleDocument Parsed { get; init; } = null!;

    public TxGraph Graph { get; init; } = null!;

    public TxMarks Marks { get; init; } = null!;

    public BuildReport Report { get; init; } = null!;

    public string SongText { get; set; } = string.Empty;

    public List<TxAsset> Assets { get; } = [];

    public TxDerivedPlan? Derived { get; set; }

    public List<TxDerivedAsset> DerivedAssets { get; } = [];

    public List<string> Warnings { get; } = [];

    public string? MasterDataId { get; set; }

    public string? MasterTakeId { get; set; }

    public string TempFolder { get; set; } = string.Empty;

    public IReadOnlyList<IBlockRenderer> Renderers { get; set; } = [];

    public ControlService Control { get; set; } = null!;

    public List<TxAsset> Left { get; } = [];
}

/// <summary>The export side of <c>.ampletx</c>: eligibility, the build-critical check, the assets, the Derived graph, the preview of the impact and the writing of the package.</summary>
internal static class TxExport
{
    private static readonly string[] TextExtensions = [".amplet", ".amplec", ".ampleseq", ".seq", ".lyr", ".module", ".json", ".txt", ".md", ".tsv", ".csv", ".settings"];

    /// <summary>Checks that the export is allowed and works out everything it will do. Nothing is written to the project.</summary>
    public static async Task<TxPrepared> PrepareAsync(ProjectSession s, TxExportRequest request, CancellationToken ct)
    {
        if (request.Mode is not ("full" or "derived"))
        {
            throw new AmpleException("AMP5285", "The export mode must be full or derived.");
        }

        DocSession d = await s.DocAsync(request.Path, ct).ConfigureAwait(false);
        if (d.Text != d.Saved || s.IsDocDirty(request.Path))
        {
            throw new AmpleException("AMP5430", "The song has changes that are not saved. Save it first: a package is made from the saved song only.");
        }

        // the package also holds the modules and other songs the project has: unsaved changes in any of them would be left out without a word
        string[] others = [.. s.DirtyDocs().Where(x => !string.Equals(x, request.Path, StringComparison.OrdinalIgnoreCase))];
        if (others.Length > 0)
        {
            throw new AmpleException("AMP5430", "These files of the project have changes that are not saved: " + string.Join(", ", others.Take(5)) + (others.Length > 5 ? $" and {others.Length - 5} more" : string.Empty) + ". Save them first: a package is made from the saved files only.");
        }

        string onDisk = await File.ReadAllTextAsync(ProjectPath.Resolve(s.Root, request.Path), ct).ConfigureAwait(false);
        if (onDisk != d.Saved)
        {
            throw new AmpleException("AMP5430", "The file on disk is not the saved song of this session. Reload the song and save it first.");
        }

        AmpleDocument parsed = s.ParseExpanded(d);
        TxGraph graph = new(parsed, s.Spec);
        if (graph.Master is null)
        {
            throw new AmpleException("AMP5431", "The song has no final output (a master block) to export.");
        }

        // only the blocks that contribute to the final output can stop the export
        List<string> problems = [];
        foreach (Diagnostic diag in parsed.Diagnostics.Where(x => x.Severity == Severity.Error))
        {
            BlockNode? owner = parsed.Blocks.FirstOrDefault(b => diag.Line - 1 >= b.Line && diag.Line - 1 <= (b.CloseLine < 0 ? b.Line : b.CloseLine));
            if (owner is null || graph.Active.Contains(owner.Name))
            {
                problems.Add($"line {diag.Line}: {diag.Code} {diag.Message}" + (owner is null ? string.Empty : $" (block {owner.Name})"));
            }
        }

        if (problems.Count > 0)
        {
            throw new AmpleException("AMP5431", "The song cannot be built, so it cannot be exported: " + string.Join("; ", problems));
        }

        IReadOnlyList<IBlockRenderer> renderers = await s.RenderersAsync(ct).ConfigureAwait(false);
        ControlService control = s.NewControl(parsed, renderers);
        BuildReport report = await s.Engine.BuildAsync(parsed, new BuildOptions("bake", null, null, 0, renderers, null, control, null, null), ct).ConfigureAwait(false);
        List<string> failed = [];
        foreach (string name in graph.Active.Order(StringComparer.Ordinal))
        {
            BlockResult? result = report.Blocks.FirstOrDefault(b => b.Block == name);
            if (result is null || result.TakeId is null || result.Status is not ("rendered" or "cached"))
            {
                BlockNode? b = graph.Block(name);
                failed.Add($"{name} ({result?.Status ?? "not built"}{(string.IsNullOrEmpty(result?.Message) ? string.Empty : ": " + result!.Message)}; line {(b?.Line ?? 0) + 1})");
            }
        }

        if (failed.Count > 0)
        {
            throw new AmpleException("AMP5432", "A block of the final result does not build, so the song cannot be exported: " + string.Join("; ", failed));
        }

        string fallback = string.Empty;
        string songText = d.Saved;
        if (request.Mode == "derived")
        {
            // module instances (use ... as ...) are cut like ordinary blocks: the Derived song is made from the text in which every use is expanded into its blocks
            ExpandedSong expanded = new Ample.Engine.Modules.ModuleService(s.Root, () => s.Library, s.Manifest.Id.ToString("D")).Expand(d.Saved, s.Spec);
            if (expanded.Used.Count > 0 && expanded.Diagnostics.Count == 0)
            {
                songText = expanded.Text;
                parsed = Parser.Parse(songText, s.Spec);
                graph = new TxGraph(parsed, s.Spec);
                fallback = "The module instances (use ... as ...) are written out as ordinary blocks in this Derived package, so the package needs no module files.";
            }
        }

        TxPrepared p = new() { Renderers = renderers, Control = control, Request = request, Doc = d, Parsed = parsed, Graph = graph, Marks = TxMarks.Parse(songText), Report = report, SongText = songText, MasterTakeId = report.MasterTakeId };
        if (report.MasterTakeId is not null)
        {
            p.MasterDataId = AudioData(s, report.MasterTakeId, null);
        }

        if (fallback.Length > 0)
        {
            p.Warnings.Add(fallback);
        }

        p.TempFolder = Path.Combine(s.Root, ProjectLayout.AmpleFolder, "tmp", "tx-" + Guid.NewGuid().ToString("N")[..12]);
        if (request.Mode == "derived")
        {
            await PrepareDerivedAsync(s, p, ct).ConfigureAwait(false);
        }

        CollectAssets(s, p);
        foreach (TxAsset a in p.Assets)
        {
            FileInfo info = new(a.FullPath);
            a.Size = info.Length;
            a.OriginalSize = info.Length;
            a.OriginalFileName = info.Name;
            await using FileStream stream = new(a.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            a.Sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        }

        if (!request.WholeSets && p.Assets.Any(a => a.IsSetFile || a.EntryPath.EndsWith(".sf2", StringComparison.OrdinalIgnoreCase)))
        {
            UsageTrace? trace = null;
            try
            {
                trace = await TraceAsync(s, p, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AmpleException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                p.Warnings.Add("What the song needs could not be found out (" + ex.Message + "), so the whole sets are included.");
            }

            if (trace is not null)
            {
                await SelectNeededAsync(p, trace, ct).ConfigureAwait(false);
            }
        }

        foreach (BlockNode b in graph.All.Where(b => !graph.Active.Contains(b.Name)))
        {
            p.Warnings.Add($"{b.Name} is not part of the final result: it is {(request.Mode == "derived" ? "left out" : "kept in the package, but not built")}.");
        }

        return p;
    }

    private static string? AudioData(ProjectSession s, string takeId, string? role)
    {
        TakeRead take = s.Bank.ReadTake(s.Bank.ResolveTakeId(takeId));
        string[] preferred = ["master", "mix", "mixed", "audio", "out"];
        IEnumerable<TakeDataRef> data = role is not null ? take.Take.Data.Where(x => x.Role == role) : take.Take.Data.OrderBy(x => { int i = Array.IndexOf(preferred, x.Role); return i < 0 ? preferred.Length : i; });
        foreach (TakeDataRef d in data)
        {
            using Stream st = s.Bank.OpenData(d.DataId);
            byte[] head = new byte[4];
            if (st.Read(head, 0, 4) == 4 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F')
            {
                return d.DataId;
            }
        }

        return null;
    }

    private static async Task PrepareDerivedAsync(ProjectSession s, TxPrepared p, CancellationToken ct)
    {
        TxDerivedPlan plan = p.Graph.PlanDerived(p.Marks, b => BlockFiles.Of(s.Root, s.AmpleRoot, p.Parsed, b).Count > 0);
        p.Derived = plan;
        if (plan.Conflicts.Count > 0)
        {
            throw new AmpleException("AMP5434", "The Derived export has conflicts that must be resolved first: " + string.Join("; ", plan.Conflicts));
        }

        Directory.CreateDirectory(p.TempFolder);
        double barSeconds = 240.0 / (p.Parsed.Project?.Tempo is > 0 ? p.Parsed.Project.Tempo : 120);
        Dictionary<string, TxDerivedAsset> byData = new(StringComparer.Ordinal);
        Dictionary<string, string> inputName = new(StringComparer.Ordinal);
        Dictionary<string, (string File, int Bars)> files = new(StringComparer.Ordinal);
        foreach (TxCut cut in plan.Cuts)
        {
            BlockResult result = p.Report.Blocks.First(b => b.Block == cut.Block);
            string dataId = AudioData(s, result.TakeId!, cut.Port) ?? throw new AmpleException("AMP5433", $"The output {cut.Key} has no audio that can be rendered, so it cannot be a Derived boundary.");
            if (!byData.TryGetValue(dataId, out TxDerivedAsset? asset))
            {
                string name = Safe("derived_" + cut.Block + (cut.Port is "out" or "audio" ? string.Empty : "_" + cut.Port));
                string tempFile = Path.Combine(p.TempFolder, name + ".wav");
                await using (Stream source = s.Bank.OpenData(dataId))
                await using (FileStream target = new(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    await source.CopyToAsync(target, ct).ConfigureAwait(false);
                }

                (int rate, int channels, int bits, double seconds) = WavInfo(tempFile);
                _ = (rate, channels, bits);
                asset = new TxDerivedAsset { Cut = cut, InputName = name, File = "derived/" + name + ".wav", TempFile = tempFile, DataId = dataId, Seconds = seconds, Bars = Math.Max(1, (int)Math.Ceiling((seconds / barSeconds) - 1e-6)) };
                FileInfo info = new(tempFile);
                asset.Size = info.Length;
                await using FileStream hashed = new(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                asset.Sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(hashed, ct).ConfigureAwait(false));
                asset.Baked = [.. p.Graph.BakedBy(cut, plan.Live)];
                byData[dataId] = asset;
                p.DerivedAssets.Add(asset);
            }

            asset.Keys.Add(cut.Key);
            inputName[cut.Key] = asset.InputName;
            files[cut.Key] = (asset.File, asset.Bars);
        }

        foreach (TxDerivedAsset a in p.DerivedAssets)
        {
            TxCut cut = a.Cut;
            BlockNode? cutBlock = p.Graph.Block(cut.Block);
            if (cut.RawSource)
            {
                a.Identity = true;
                p.Warnings.Add($"{cut.Key}: this source has no transformed Derived boundary. The exported audio is effectively identical to the output of the source ({cut.Key}); the path goes straight into {string.Join(", ", p.Graph.Consumers(cut.Block, cut.Port).Select(e => e.ToBlock + "." + e.ToInput))}. Mark a block further down as a boundary, or choose Always include as-is.");
            }
            else if (cutBlock is not null && p.Graph.MainIn(cutBlock) is { } mainIn)
            {
                TxEdge? feeder = p.Graph.Edges.FirstOrDefault(e => e.ToBlock == cut.Block && e.ToInput == mainIn && !e.Disabled);
                if (feeder is not null && p.Report.Blocks.FirstOrDefault(b => b.Block == feeder.FromBlock)?.TakeId is { } feederTake && AudioData(s, feederTake, feeder.FromPort) == a.DataId)
                {
                    a.Identity = true;
                    p.Warnings.Add($"{cut.Key}: the boundary changed nothing audible; the exported audio is identical to the audio that went in ({feeder.FromBlock}.{feeder.FromPort}).");
                }
            }
        }

        p.SongText = p.Graph.DerivedText(plan, files, inputName);
        AmpleDocument reparsed = Parser.Parse(p.SongText, s.Spec, new ParseOptions { RequireProjectLine = true });
        string[] errors = [.. reparsed.Diagnostics.Where(x => x.Severity == Severity.Error).Select(x => $"line {x.Line}: {x.Message}")];
        if (errors.Length > 0)
        {
            throw new AmpleException("AMP5433", "The Derived song could not be made correctly: " + string.Join("; ", errors));
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, UsageTrace> Traces = new(StringComparer.Ordinal);

    /// <summary>Runs the build once more with a recorder, so it is known which samples were played and which SoundFont presets were used (the result is kept for this saved state).</summary>
    private static async Task<UsageTrace> TraceAsync(ProjectSession s, TxPrepared p, CancellationToken ct)
    {
        string key = (p.MasterTakeId ?? string.Empty) + "|" + Sha(p.Doc.Saved);
        if (Traces.TryGetValue(key, out UsageTrace? known))
        {
            return known;
        }

        UsageTrace trace = new();
        // the traced build goes into a temporary takebank, so the project's own takes stay untouched
        string scratch = Path.Combine(p.TempFolder, "trace-project");
        using (OpenProject created = await ProjectStore.CreateAsync(s.Fs, s.Clock, new GuidV7Generator(), scratch, "trace", null, ct).ConfigureAwait(false))
        {
            _ = created;
        }

        BuildEngine engine = new(Takebank.Open(s.Fs, s.Clock, scratch), s.Spec, s.Manifest.Id, s.Root, s.AmpleRoot);
        BuildReport again = await engine.BuildAsync(p.Parsed, new BuildOptions("bake", null, null, 0, p.Renderers, null, p.Control, null, null) { Trace = trace }, ct).ConfigureAwait(false);
        string[] failed = [.. p.Graph.Active.Where(n => again.Blocks.FirstOrDefault(b => b.Block == n) is not { Status: "rendered" or "cached" })];
        if (failed.Length > 0)
        {
            throw new AmpleException("AMP5432", "The traced build did not finish, so what the song needs could not be found out: " + string.Join(", ", failed) + ". Export with the whole sets to skip this step.");
        }

        Traces[key] = trace;
        return trace;
    }

    /// <summary>Keeps in the package only what the build used: the presets of a SoundFont (as a smaller SoundFont) and the samples of a set that were played (with all their variants).</summary>
    private static Task SelectNeededAsync(TxPrepared p, UsageTrace trace, CancellationToken ct)
    {
        Directory.CreateDirectory(p.TempFolder);
        bool Traced(TxAsset a) => a.UsedBy.All(b => p.Graph.Active.Contains(b));
        List<TxAsset> drop = [];
        foreach (TxAsset a in p.Assets.Where(x => x.EntryPath.EndsWith(".sf2", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            if (!Traced(a))
            {
                a.Reason = "a block that is not part of the final result also uses it";
                continue;
            }

            List<(int Bank, int Program)> presets = [.. a.UsedBy.SelectMany(b => trace.PresetsOf(b)).Where(x => string.Equals(Path.GetFullPath(x.Path), a.FullPath, StringComparison.OrdinalIgnoreCase)).Select(x => (x.Bank, x.Program)).Distinct()];
            if (presets.Count == 0)
            {
                a.Reason = "the build did not report which presets it used";
                continue;
            }

            string subset = System.IO.Path.Combine(p.TempFolder, "subset-" + a.Sha256[..12] + ".sf2");
            Ample.Audio.Instruments.Sf2Subset.Result made = Ample.Audio.Instruments.Sf2Subset.Write(a.FullPath, presets, subset, $"A subset of {System.IO.Path.GetFileName(a.FullPath)} (SHA-256 {a.Sha256}) with the presets this song uses; made by AMPLE for an .ampletx package.");
            a.SubsetFile = subset;
            a.SubsetOf = a.Sha256;
            a.Selection = "needed";
            a.Presets.AddRange(made.Presets);
            a.SubsetInstruments = made.Instruments;
            a.SubsetSamples = made.Samples;
            a.Size = made.SubsetBytes;
            using FileStream stream = new(subset, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            a.Sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        foreach (IGrouping<string, TxAsset> folder in p.Assets.Where(x => x.IsSetFile && x.EntryPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)).GroupBy(x => System.IO.Path.GetDirectoryName(x.FullPath)!, StringComparer.OrdinalIgnoreCase))
        {
            if (!folder.All(Traced))
            {
                foreach (TxAsset a in folder)
                {
                    a.Reason = "a block that is not part of the final result also uses this set";
                }

                continue;
            }

            HashSet<string> played = new(StringComparer.Ordinal);
            foreach (string block in folder.SelectMany(x => x.UsedBy).Distinct())
            {
                played.UnionWith(trace.SamplesOf(block));
            }

            HashSet<string> keptNames = new(StringComparer.OrdinalIgnoreCase);
            foreach (TxAsset a in folder.Where(x => played.Contains("sha256:" + x.Sha256)))
            {
                keptNames.Add(Ample.Audio.Engine.Instrument.ParseName(System.IO.Path.GetFileNameWithoutExtension(a.FullPath).ToLowerInvariant()).Name);
            }

            foreach (TxAsset a in folder)
            {
                string name = Ample.Audio.Engine.Instrument.ParseName(System.IO.Path.GetFileNameWithoutExtension(a.FullPath).ToLowerInvariant()).Name;
                if (keptNames.Contains(name))
                {
                    a.Selection = "needed";
                }
                else
                {
                    drop.Add(a);
                }
            }
        }

        foreach (TxAsset a in drop)
        {
            p.Assets.Remove(a);
            p.Left.Add(a);
        }

        return Task.CompletedTask;
    }

    /// <summary>Collects the files the package must hold: every file a block reads (Full: all blocks; Derived: the blocks that stay), the settings that influence the sound, and the derived audio.</summary>
    private static void CollectAssets(ProjectSession s, TxPrepared p)
    {
        IEnumerable<BlockNode> blocks = p.Request.Mode == "derived" ? p.Graph.All.Where(b => p.Derived!.Live.Contains(b.Name)) : p.Graph.All;
        Dictionary<string, TxAsset> found = new(StringComparer.OrdinalIgnoreCase);
        string root = Path.GetFullPath(s.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        IReadOnlyList<string> packs = AmpleHome.PackFolders(AmpleHome.DefaultPath(), s.AmpleRoot, s.Root);
        foreach (BlockNode b in blocks)
        {
            HashSet<string> setFiles = new(StringComparer.OrdinalIgnoreCase);
            foreach (string full in BlockFiles.Of(s.Root, s.AmpleRoot, p.Parsed, b, setFiles))
            {
                string path = Path.GetFullPath(full);
                if (!found.TryGetValue(path, out TxAsset? asset))
                {
                    asset = Classify(path, root, packs);
                    found[path] = asset;
                    p.Assets.Add(asset);
                }

                asset.UsedBy.Add(b.Name);
                if (setFiles.Contains(full) || setFiles.Contains(path))
                {
                    asset.IsSetFile = true;
                }
            }
        }

        foreach (UseNode use in p.Parsed.Uses)
        {
            string module = Path.Combine(s.Root, "modules", use.Module + ".module");
            if (File.Exists(module) && !found.ContainsKey(Path.GetFullPath(module)))
            {
                TxAsset asset = Classify(Path.GetFullPath(module), root, packs);
                found[asset.FullPath] = asset;
                p.Assets.Add(asset);
            }
        }

        // the component files the song imports (and the files they import): the package must build without the project folder
        string? ReadComponentFile(string target)
        {
            string full = Path.GetFullPath(Path.Combine(s.Root, target));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? File.ReadAllText(full) : null;
        }

        foreach (ComponentSource source in ComponentRegistry.Resolve(p.SongText, p.Request.Path, ReadComponentFile, s.Spec).Sources)
        {
            string component = Path.GetFullPath(Path.Combine(s.Root, source.Origin));
            if (!File.Exists(component) || found.ContainsKey(component)) continue;
            TxAsset asset = Classify(component, root, packs);
            found[component] = asset;
            p.Assets.Add(asset);
        }

        string settings = Path.Combine(s.Root, ProjectLayout.AmpleFolder, "plugin-settings.json");
        if (File.Exists(settings))
        {
            p.Assets.Add(new TxAsset { EntryPath = "settings/plugin-settings.json", FullPath = settings, OriginalPath = ".ample/plugin-settings.json", Role = AmpleTx.Roles.Settings, Kind = "settings" });
        }

        p.Assets.Sort((a, b) => string.CompareOrdinal(a.EntryPath, b.EntryPath));
    }

    private static TxAsset Classify(string full, string root, IReadOnlyList<string> packs)
    {
        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            string rel = full[root.Length..].Replace('\\', '/');
            return new TxAsset { EntryPath = rel, FullPath = full, OriginalPath = rel, Kind = "project" };
        }

        foreach (string pack in packs)
        {
            string prefix = Path.GetFullPath(pack).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string rel = full[prefix.Length..].Replace('\\', '/');
                string reference = rel == "licensed/musescore-general/MuseScore_General.sf2" ? "pack:musescore-general" : "pack:" + rel;
                return new TxAsset { EntryPath = "packs/" + rel, FullPath = full, OriginalPath = reference, Kind = "pack" };
            }
        }

        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..8];
        return new TxAsset { EntryPath = "external/" + hash + "/" + Path.GetFileName(full), FullPath = full, OriginalPath = Path.GetFileName(full), Kind = "external" };
    }

    private static string Safe(string name) => new([.. name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_')]);

    /// <summary>Sample rate, channels, bits and duration of a WAV file.</summary>
    internal static (int Rate, int Channels, int Bits, double Seconds) WavInfo(string path)
    {
        using FileStream f = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return WavInfo(f);
    }

    internal static (int Rate, int Channels, int Bits, double Seconds) WavInfo(Stream f)
    {
        using BinaryReader r = new(f, Encoding.ASCII, true);
        if (new string(r.ReadChars(4)) != "RIFF")
        {
            throw new AmpleException("AMP5433", "The rendered audio is not a WAV file.");
        }

        r.ReadInt32();
        r.ReadChars(4);
        int rate = 0, channels = 0, bits = 0;
        while (f.Position + 8 <= f.Length)
        {
            string id = new(r.ReadChars(4));
            int size = r.ReadInt32();
            if (id == "fmt ")
            {
                r.ReadInt16();
                channels = r.ReadInt16();
                rate = r.ReadInt32();
                r.ReadInt32();
                r.ReadInt16();
                bits = r.ReadInt16();
                f.Seek(size - 16, SeekOrigin.Current);
            }
            else if (id == "data")
            {
                long length = Math.Min(size, f.Length - f.Position);
                return (rate, channels, bits, rate == 0 || channels == 0 || bits == 0 ? 0 : length / (double)(channels * (bits / 8)) / rate);
            }
            else
            {
                f.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }

        return (rate, channels, bits, 0);
    }

    /// <summary>The preview of the impact of an export, in the words of the user interface.</summary>
    public static JsonObject Impact(ProjectSession s, TxPrepared p)
    {
        JsonObject o = new()
        {
            ["mode"] = p.Request.Mode,
            ["song"] = p.Request.Path,
            ["includeMaster"] = p.Request.IncludeMaster,
            ["compress"] = p.Request.Compress,
            ["saved"] = true,
            ["build"] = new JsonObject { ["ok"] = true, ["blocksInFinalResult"] = p.Graph.Active.Count, ["blocksInSong"] = p.Graph.All.Count(), ["masterTake"] = p.MasterTakeId },
            ["marks"] = new JsonObject { ["bake"] = new JsonArray([.. p.Marks.Bake.Select(x => (JsonNode)x)]), ["asIs"] = new JsonArray([.. p.Marks.AsIs.Select(x => (JsonNode)x)]) },
            ["assets"] = new JsonArray([.. p.Assets.Select(a => (JsonNode)new JsonObject { ["path"] = a.EntryPath, ["kind"] = a.Kind, ["sha256"] = a.Sha256, ["size"] = a.Size, ["usedBy"] = new JsonArray([.. a.UsedBy.Select(x => (JsonNode)x)]), ["originalPath"] = a.OriginalPath, ["selection"] = a.Selection, ["originalSize"] = a.OriginalSize, ["presets"] = a.SubsetOf is null ? null : new JsonArray([.. a.Presets.Select(x => (JsonNode)x)]), ["whole"] = a.Reason.Length > 0 ? a.Reason : null })]),
            ["leftOut"] = new JsonObject { ["files"] = p.Left.Count, ["bytes"] = p.Left.Sum(x => x.OriginalSize), ["note"] = "Files of sample sets that this song never plays. Export with the whole sets to include them." },
            ["wholeSets"] = p.Request.WholeSets,
            ["warnings"] = new JsonArray([.. p.Warnings.Select(x => (JsonNode)x)]),
        };
        long total = p.Assets.Sum(a => a.Size) + p.DerivedAssets.Sum(a => a.Size) + (p.Request.IncludeMaster && p.MasterDataId is not null ? MasterSize(s, p) : 0);
        o["approximateBytes"] = (total + 2) / 3 * 4 + (total / 57 * 1);
        o["master"] = new JsonObject { ["included"] = p.Request.IncludeMaster && p.MasterDataId is not null, ["state"] = p.MasterDataId is null ? "none" : "current", ["takeId"] = p.MasterTakeId };
        if (p.Request.Mode == "derived" && p.Derived is { } plan)
        {
            JsonArray paths = [];
            foreach (TxDerivedAsset a in p.DerivedAssets)
            {
                paths.Add(new JsonObject
                {
                    ["generatedAsset"] = a.File,
                    ["inputBlock"] = a.InputName,
                    ["sha256"] = a.Sha256,
                    ["size"] = a.Size,
                    ["seconds"] = Math.Round(a.Seconds, 2),
                    ["boundary"] = a.Cut.Explicit ? "explicit" : a.Cut.RawSource ? "none (straight from the source)" : "automatic",
                    ["boundaryBlock"] = a.Cut.Block,
                    ["output"] = a.Cut.Key,
                    ["origin"] = a.Cut.Origin,
                    ["bakedBlocks"] = new JsonArray([.. a.Baked.Select(x => (JsonNode)x)]),
                    ["usedBy"] = new JsonArray([.. a.Keys.Select(x => (JsonNode)x)]),
                    ["identity"] = a.Identity,
                    ["originalSourceEmbedded"] = false,
                    ["fullUpstreamReconstructionAfterImport"] = false,
                    ["editableDownstream"] = new JsonArray([.. plan.Live.Where(n => p.Graph.Reaches(a.Cut.Block, a.Cut.Port, n) || p.Graph.Consumers(a.Cut.Block, a.Cut.Port).Any(e => e.ToBlock == n)).Select(x => (JsonNode)x)]),
                    ["includeAsIsOverride"] = false,
                });
            }

            JsonArray kept = [];
            foreach (string name in plan.KeptSources)
            {
                kept.Add(new JsonObject { ["block"] = name, ["originalSourceEmbedded"] = true, ["includeAsIsOverride"] = p.Marks.AsIs.Any(a => a.Split('.')[0] == name), ["files"] = new JsonArray([.. p.Assets.Where(a => a.UsedBy.Contains(name)).Select(a => (JsonNode)a.EntryPath)]) });
            }

            o["derived"] = new JsonObject
            {
                ["paths"] = paths,
                ["keptSources"] = kept,
                ["editableBlocks"] = new JsonArray([.. plan.Live.Select(x => (JsonNode)x)]),
                ["droppedBlocks"] = new JsonArray([.. p.Graph.All.Where(b => !plan.Live.Contains(b.Name)).Select(b => (JsonNode)b.Name)]),
            };
        }

        return o;
    }

    private static long MasterSize(ProjectSession s, TxPrepared p)
    {
        using Stream st = s.Bank.OpenData(p.MasterDataId!);
        return st.Length;
    }

    /// <summary>Writes the package.</summary>
    public static async Task<JsonObject> WriteAsync(ProjectSession s, TxPrepared p, CancellationToken ct)
    {
        TxExportRequest r = p.Request;
        string folder = (r.Folder ?? "exports").Replace('\\', '/').Trim('/');
        string songName = System.IO.Path.GetFileNameWithoutExtension(r.Path);
        string name = Safe(string.IsNullOrWhiteSpace(r.Name) ? $"{songName}-{r.Mode}" : r.Name!);
        string relative = (folder.Length == 0 ? name : folder + "/" + name) + ".ampletx";
        string target = ProjectPath.Resolve(s.Root, relative);
        if (File.Exists(target) || File.Exists(target + ".zip"))
        {
            throw new AmpleException("AMP5435", $"There is already a package called {relative}. Choose another name: an existing package is never overwritten.");
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        List<TxSource> sources = [];
        sources.Add(new TxSource(r.Path, AmpleTx.Roles.Song, "text/plain", System.IO.Path.GetFileName(r.Path), r.Path, Text: p.SongText, Info: r.Mode == "derived" ? new JsonObject { ["derivedFrom"] = Sha(p.Doc.Saved), ["note"] = "The song of a Derived export: the graph before the audio inputs below is not in this package." } : null));
        foreach (TxAsset a in p.Assets)
        {
            sources.Add(new TxSource(a.EntryPath, a.Role, MediaOf(a.EntryPath), System.IO.Path.GetFileName(a.FullPath), a.OriginalPath, File: a.Source, Info: AssetInfo(a)));
        }

        FlacPreview? flac = r.Flac && r.Mode == "derived" ? TxFlac.Approved(r.FlacApproval, p) : null;
        foreach (TxDerivedAsset a in p.DerivedAssets)
        {
            FlacStem? stem = flac?.Stems.FirstOrDefault(x => x.File == a.File && x.Supported);
            string entryPath = stem is null ? a.File : System.IO.Path.ChangeExtension(a.File, ".flac").Replace('\\', '/');
            sources.Add(new TxSource(entryPath, AmpleTx.Roles.Derived, stem is null ? "audio/wav" : "audio/flac", System.IO.Path.GetFileName(entryPath), a.Cut.Key, File: stem?.FlacFile ?? a.TempFile, Info: new JsonObject
            {
                ["codec"] = stem is null ? "wav" : "flac",
                ["bitDepth"] = stem is null ? null : TxFlac.BitsOf(a.TempFile),
                ["decodedPath"] = stem is null ? null : a.File,
                ["wavSha256"] = stem is null ? null : a.Sha256,
                ["wavSize"] = stem is null ? null : a.Size,
                ["samplesSha256"] = stem is null ? null : TxFlac.DataSha(a.TempFile),
                ["decodedIdentical"] = stem?.IdenticalFile,
                ["samplesIdentical"] = stem?.IdenticalSamples,
                ["flacNote"] = stem is null ? null : "Stored as FLAC at the owner's request after listening to the result before and after; there is no guarantee that the decoded audio equals the original file.",
                ["renderedFrom"] = a.Cut.Key,
                ["boundary"] = a.Cut.Explicit ? "explicit" : a.Cut.RawSource ? "source" : "automatic",
                ["bakedBlocks"] = new JsonArray([.. a.Baked.Select(x => (JsonNode)x)]),
                ["inputBlock"] = a.InputName,
                ["identity"] = a.Identity,
                ["seconds"] = Math.Round(a.Seconds, 3),
                ["bars"] = a.Bars,
            }));
        }

        JsonObject? masterInfo = null;
        string? masterTemp = null;
        if (r.IncludeMaster && p.MasterDataId is not null)
        {
            masterTemp = System.IO.Path.Combine(p.TempFolder, "master.wav");
            Directory.CreateDirectory(p.TempFolder);
            await using (Stream source = s.Bank.OpenData(p.MasterDataId))
            await using (FileStream copy = new(masterTemp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                await source.CopyToAsync(copy, ct).ConfigureAwait(false);
            }

            (int rate, int channels, int bits, double seconds) = WavInfo(masterTemp);
            TakeRead take = s.Bank.ReadTake(s.Bank.ResolveTakeId(p.MasterTakeId!));
            masterInfo = new JsonObject
            {
                ["entry"] = "master/" + songName + ".wav",
                ["format"] = "wav",
                ["codec"] = bits == 16 ? "pcm_s16le" : "pcm",
                ["sampleRate"] = rate,
                ["bitDepth"] = bits,
                ["channels"] = channels,
                ["channelLayout"] = channels == 2 ? "stereo" : channels == 1 ? "mono" : channels + " channels",
                ["durationSeconds"] = Math.Round(seconds, 3),
                ["renderedAt"] = take.Take.Created.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["takeId"] = p.MasterTakeId,
                ["belongsToState"] = StateHash(p),
                ["stateKind"] = "the saved song of this package, built from exactly the files in it",
            };
            sources.Add(new TxSource("master/" + songName + ".wav", AmpleTx.Roles.Master, "audio/wav", songName + ".wav", "master", File: masterTemp, Info: masterInfo));
        }

        JsonObject manifest = Manifest(s, p, masterInfo);
        if (flac is not null)
        {
            manifest["flac"] = true;
        }
        string sha;
        try
        {
            sha = TxWriter.Write(target, r.Mode, manifest, sources);
        }
        finally
        {
            try { Directory.Delete(p.TempFolder, true); } catch (IOException) { /* a temporary folder that cannot be removed now is removed with .ample/tmp later */ }
        }

        JsonObject result = new() { ["path"] = relative, ["sha256"] = sha, ["size"] = new FileInfo(target).Length, ["mode"] = r.Mode, ["masterIncluded"] = masterInfo is not null, ["entries"] = sources.Count };
        if (r.Compress)
        {
            string zip = TxPackage.Compress(target);
            result["zip"] = relative + ".zip";
            result["zipSize"] = new FileInfo(zip).Length;
            result["zipSha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(zip)));
        }

        return result;
    }

    private static JsonObject AssetInfo(TxAsset a)
    {
        JsonObject o = new() { ["usedBy"] = new JsonArray([.. a.UsedBy.Select(x => (JsonNode)x)]), ["kind"] = a.Kind, ["selection"] = a.Selection };
        if (a.SubsetOf is not null)
        {
            o["subsetOf"] = a.SubsetOf;
            o["originalFileName"] = a.OriginalFileName;
            o["originalSize"] = a.OriginalSize;
            o["presets"] = new JsonArray([.. a.Presets.Select(x => (JsonNode)x)]);
            o["instruments"] = a.SubsetInstruments;
            o["samples"] = a.SubsetSamples;
        }

        if (a.Reason.Length > 0)
        {
            o["whole"] = a.Reason;
        }

        return o;
    }

    private static JsonObject? executable;

    /// <summary>The file name and SHA-256 of the program that made the package (the entry assembly), kept for later comparison; nothing reads it back yet.</summary>
    private static JsonObject ExecutableInfo()
    {
        if (executable is not null)
        {
            return (JsonObject)executable.DeepClone();
        }

        // Assembly.Location is empty in a single-file program; the entry assembly name next to the program, else the program itself.
        string? path = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name is { } entryName ? Path.Combine(AppContext.BaseDirectory, entryName + ".dll") : null;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            path = Environment.ProcessPath ?? throw new InvalidOperationException("The program file is unknown.");
        }

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        executable = new JsonObject { ["file"] = System.IO.Path.GetFileName(path), ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(stream)) };
        return (JsonObject)executable.DeepClone();
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string MediaOf(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".wav" => "audio/wav",
        ".flac" => "audio/flac",
        ".mp3" => "audio/mpeg",
        ".ogg" => "audio/ogg",
        ".mid" or ".midi" => "audio/midi",
        ".sf2" or ".sf3" => "audio/x-soundfont",
        var e when TextExtensions.Contains(e) => "text/plain",
        _ => "application/octet-stream",
    };

    /// <summary>The state the package is made from: the song of the package and the hash of every file in it.</summary>
    internal static string StateHash(TxPrepared p)
    {
        StringBuilder text = new();
        text.Append(Sha(p.SongText)).Append('\n');
        foreach (TxAsset a in p.Assets.OrderBy(x => x.EntryPath, StringComparer.Ordinal))
        {
            text.Append(a.EntryPath).Append(':').Append(a.Sha256).Append('\n');
        }

        foreach (TxDerivedAsset a in p.DerivedAssets.OrderBy(x => x.File, StringComparer.Ordinal))
        {
            text.Append(a.File).Append(':').Append(a.Sha256).Append('\n');
        }

        return Sha(text.ToString());
    }

    private static JsonObject Manifest(ProjectSession s, TxPrepared p, JsonObject? master)
    {
        AmpleDocument doc = p.Parsed;
        ProjectManifest pm = ProjectFile.Parse(File.ReadAllText(System.IO.Path.Combine(s.Root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile)));
        string version = typeof(TxExport).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown";
        JsonObject song = new()
        {
            ["name"] = doc.Project?.Name,
            ["tempo"] = doc.Project?.Tempo,
            ["masterSeed"] = doc.Project?.MasterSeed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["features"] = new JsonObject([.. doc.Features.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value))]),
            ["lint"] = new JsonObject([.. doc.Lint.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value))]),
            ["assist"] = doc.Assist,
            ["pluginSettings"] = new JsonObject([.. doc.Settings.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value))]),
            ["timeSignature"] = "4/4 (every AMPLE project counts four beats to a bar)",
            ["tempoMap"] = "one tempo for the whole song",
        };
        JsonObject projectSettings = new()
        {
            ["masterSeed"] = pm.MasterSeed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["defaultEngine"] = pm.Settings.DefaultEngine,
        };
        (int rate, int channels, int bits) = (44100, 2, 16);
        if (master is not null)
        {
            rate = master["sampleRate"]!.GetValue<int>();
            channels = master["channels"]!.GetValue<int>();
            bits = master["bitDepth"]!.GetValue<int>();
        }

        JsonObject render = new() { ["sampleRate"] = rate, ["channels"] = channels, ["bitDepth"] = bits, ["dsp"] = "integer", ["container"] = "wav", ["resampler"] = "built-in (AMPLE)", ["outputCodec"] = "pcm_s" + bits + "le" };
        JsonObject settings = new() { ["song"] = song, ["project"] = projectSettings, ["render"] = render };
        string settingsFingerprint = Sha(settings.ToJsonString());

        JsonArray blocks = [];
        SortedDictionary<string, JsonObject> renderers = new(StringComparer.Ordinal);
        SortedSet<string> toolprints = new(StringComparer.Ordinal);
        foreach (BlockNode b in doc.Blocks.Where(b => !b.Skipped).OrderBy(b => b.Name, StringComparer.Ordinal))
        {
            JsonObject entry = new() { ["name"] = b.Name, ["type"] = b.Type, ["class"] = b.Class, ["version"] = b.Version, ["inFinalResult"] = p.Graph.Active.Contains(b.Name) };
            if (s.Spec.Templates.TryGetValue(b.Type, out TemplateSpec? template))
            {
                entry["templateVersion"] = template.Class + "@" + template.Version;
                if (template.Plugin is not null)
                {
                    entry["plugin"] = template.Plugin;
                }
            }

            BlockResult? result = p.Report.Blocks.FirstOrDefault(x => x.Block == b.Name);
            if (result?.TakeId is not null)
            {
                JsonNode? plan = JsonNode.Parse(s.Bank.ReadTake(s.Bank.ResolveTakeId(result.TakeId)).Plan.CanonicalJson);
                if (plan?["plugin"] is JsonObject pl)
                {
                    entry["renderer"] = pl.DeepClone();
                    renderers[pl["id"]!.GetValue<string>()] = (JsonObject)pl.DeepClone();
                }

                if (plan?["toolprint"] is { } tp)
                {
                    entry["toolprint"] = tp.DeepClone();
                    toolprints.Add(tp.ToJsonString());
                }
            }

            blocks.Add(entry);
        }

        JsonArray plugins = [];
        IEnumerable<string> usedPlugins = p.Graph.All.Where(b => p.Graph.Active.Contains(b.Name) && s.Spec.Templates.TryGetValue(b.Type, out TemplateSpec? t) && t.Plugin is not null).Select(b => s.Spec.Templates[b.Type].Plugin!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        (IReadOnlyList<Ample.Plugins.DiscoveredPlugin> found, _) = Ample.Plugins.PluginHost.Discover([System.IO.Path.Combine(s.AmpleRoot, "plugins"), System.IO.Path.Combine(s.Root, "plugins"), System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ample", "plugins")]);
        foreach (string id in usedPlugins)
        {
            Ample.Plugins.DiscoveredPlugin? plugin = found.FirstOrDefault(x => x.Manifest.Id == id);
            plugins.Add(new JsonObject { ["id"] = id, ["version"] = plugin?.Manifest.Version, ["installed"] = plugin is not null, ["fingerprint"] = plugin is null ? null : Sha(plugin.Manifest.Id + "@" + plugin.Manifest.Version + "/" + plugin.Manifest.Determinism + "/" + string.Join(",", plugin.Manifest.Blocks)) });
        }

        JsonArray models = [];
        foreach (TxAsset a in p.Assets.Where(a => a.EntryPath.EndsWith(".sf2", StringComparison.OrdinalIgnoreCase)))
        {
            models.Add(new JsonObject { ["kind"] = "soundfont", ["reference"] = a.OriginalPath, ["file"] = a.EntryPath, ["sha256"] = a.Sha256, ["size"] = a.Size, ["selection"] = a.Selection, ["subsetOf"] = a.SubsetOf, ["originalFileName"] = a.SubsetOf is null ? null : a.OriginalFileName, ["presets"] = a.SubsetOf is null ? null : new JsonArray([.. a.Presets.Select(x => (JsonNode)x)]), ["instruments"] = a.SubsetOf is null ? null : a.SubsetInstruments, ["samples"] = a.SubsetOf is null ? null : a.SubsetSamples });
        }

        JsonObject m = new()
        {
            ["format"] = new JsonObject { ["ampletx"] = AmpleTx.FormatVersion, ["amplet"] = ProjectLayout.CurrentFormatVersion, ["spec"] = s.Spec.Version },
            ["application"] = new JsonObject { ["name"] = "AMPLE", ["version"] = version, ["executable"] = ExecutableInfo() },
            ["created"] = s.Clock.UtcNow.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["mode"] = p.Request.Mode,
            ["project"] = new JsonObject { ["id"] = pm.Id.ToString("D"), ["name"] = pm.Name, ["song"] = p.Request.Path, ["savedSongSha256"] = Sha(p.Doc.Saved), ["packageSongSha256"] = Sha(p.SongText), ["stateSha256"] = StateHash(p) },
            ["settings"] = settings,
            ["settingsFingerprint"] = settingsFingerprint,
            ["dependencies"] = new JsonObject
            {
                ["blocks"] = blocks,
                ["renderers"] = new JsonArray([.. renderers.Values.Select(x => (JsonNode)x)]),
                ["toolprints"] = new JsonArray([.. toolprints.Select(x => JsonNode.Parse(x)!)]),
                ["plugins"] = plugins,
                ["models"] = models,
                ["assets"] = new JsonArray([.. p.Assets.Select(a => (JsonNode)new JsonObject { ["path"] = a.EntryPath, ["sha256"] = a.Sha256, ["size"] = a.Size })]),
            },
            ["master"] = master?.DeepClone(),
            ["warnings"] = new JsonArray([.. p.Warnings.Select(x => (JsonNode)x)]),
        };
        if (p.Request.Mode == "derived" && p.Derived is { } plan2)
        {
            m["derived"] = new JsonObject
            {
                ["note"] = "A Derived package starts at its audio inputs: what was baked into them is not in this package and is not rebuilt by an import.",
                ["originalSongSha256"] = Sha(p.Doc.Saved),
                ["marks"] = new JsonObject { ["bake"] = new JsonArray([.. p.Marks.Bake.Select(x => (JsonNode)x)]), ["asIs"] = new JsonArray([.. p.Marks.AsIs.Select(x => (JsonNode)x)]) },
                ["inputs"] = new JsonArray([.. p.DerivedAssets.Select(a => (JsonNode)new JsonObject { ["inputBlock"] = a.InputName, ["file"] = a.File, ["sha256"] = a.Sha256, ["renderedFrom"] = a.Cut.Key, ["boundary"] = a.Cut.Explicit ? "explicit" : a.Cut.RawSource ? "source" : "automatic", ["bakedBlocks"] = new JsonArray([.. a.Baked.Select(x => (JsonNode)x)]), ["identity"] = a.Identity })]),
                ["keptSources"] = new JsonArray([.. plan2.KeptSources.Select(x => (JsonNode)x)]),
                ["editableBlocks"] = new JsonArray([.. plan2.Live.Select(x => (JsonNode)x)]),
                ["droppedBlocks"] = new JsonArray([.. p.Graph.All.Where(b => !plan2.Live.Contains(b.Name)).Select(b => (JsonNode)b.Name)]),
            };
        }

        return m;
    }
}
