using System.CommandLine;
using System.Globalization;
using Ample.Core.Abstractions;
using Ample.Audio.Engine;
using Ample.Audio.IO;
using Ample.Engine.Engine;
using Ample.Storage.BlackBox;
using Ample.Storage.Projects;
using Ample.Storage.Takes;
using Ample.Docs;
using Ample.Language;

namespace Ample.Cli;

/// <summary>The <c>ample build</c>, <c>bake</c> and <c>play</c> commands.</summary>
internal static class BuildCommands
{
    /// <summary>The settings of a plugin from the project's .ample/plugin-settings.json (the same file the web UI writes); {} when there are none.</summary>
    private static string ProjectPluginSettings(string projectRoot, string pluginId)
    {
        string file = Path.Combine(projectRoot, ".ample", "plugin-settings.json");
        try
        {
            if (File.Exists(file))
            {
                using System.Text.Json.JsonDocument d = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                if (d.RootElement.TryGetProperty(pluginId, out System.Text.Json.JsonElement v))
                {
                    return v.GetRawText();
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // A damaged file means no settings.
        }

        return "{}";
    }

    private static readonly PhysicalFileSystem Fs = new();
    private static readonly SystemClock Clock = new();

    internal static IReadOnlyList<Command> Create(TextWriter output)
    {
        Argument<string> file = new("song") { Description = "The .amplet song file." };
        Option<string?> project = new("--project") { Description = "The project folder (default: the folder above the song that has .ample)." };
        Option<string?> root = new("--root") { Description = "The AMPLE folder with spec/ (default: found from the current folder upwards)." };
        Option<string?> only = new("--block") { Description = "Build only this block and what it needs." };
        Option<string?> preview = new("--preview") { Description = "Preview: low quality, optionally a range, for example 5:4 (four bars from bar 5), or just 'all'." };
        Option<string?> outFile = new("--out") { Description = "Write the master here: a WAV file, or mp3 / flac when the name ends so (needs ffmpeg on the PATH; default: out/<song>.wav in the project). With --block: write that block's sound here." };

        Command build = new("build", "Renders the song; blocks whose plan is unchanged come from the cache.") { file, project, root, only, preview, outFile };
        build.SetAction(async (p, t) => await Run(output, p.GetValue(file)!, p.GetValue(project), p.GetValue(root), p.GetValue(only), p.GetValue(preview), p.GetValue(outFile), false, t));

        Command play = new("play", "Builds a preview and writes the WAV (no player is started).") { file, project, root, outFile };
        play.SetAction(async (p, t) => await Run(output, p.GetValue(file)!, p.GetValue(project), p.GetValue(root), null, "all", p.GetValue(outFile), false, t));

        Argument<string> block = new("block") { Description = "The block to bake." };
        Command bake = new("bake", "Renders a block at full quality and marks it baked in the song source.") { file, block, project, root };
        bake.SetAction(async (p, t) => await Run(output, p.GetValue(file)!, p.GetValue(project), p.GetValue(root), p.GetValue(block), null, null, true, t));

        return [build, play, bake];
    }

    private static async Task<int> Run(TextWriter output, string songPath, string? projectFolder, string? rootFolder, string? only, string? preview, string? outFile, bool bakeFlag, CancellationToken token)
    {
        string? scratch = null;
        try
        {
            string song = Path.GetFullPath(songPath);
            string projectRoot;
            try
            {
                projectRoot = projectFolder is not null ? Path.GetFullPath(projectFolder) : FindProject(Path.GetDirectoryName(song)!);
            }
            catch (AmpleException ex) when (ex.DiagnosticCode == "AMP5210" && projectFolder is null && outFile is not null)
            {
                // a quick render of a song that lives outside any project: it is built in a scratch project that is removed afterwards (files the song names in its folder are not found there)
                scratch = Path.Combine(Path.GetTempPath(), "ample-scratch-" + Guid.NewGuid().ToString("N"));
                using (await ProjectStore.CreateAsync(Fs, Clock, new GuidV7Generator(), scratch, "scratch", null, token)) { }
                projectRoot = scratch;
                output.WriteLine("No project found: building in a scratch project (a song that reads project files needs a real one).");
            }
            SpecData spec = (rootFolder is null ? SpecRoot.Find(Directory.GetCurrentDirectory()) : SpecRoot.At(rootFolder)).LoadSpecData();
            using Ample.Engine.Modules.ModuleService modules = Ample.Engine.Modules.ModuleService.WithUserLibrary(projectRoot, Clock);
            AmpleDocument doc = modules.ParseExpanded(await File.ReadAllTextAsync(song, token), spec);
            foreach (Diagnostic d in doc.Diagnostics.Where(d => d.Severity == Severity.Error))
            {
                output.WriteLine($"{song}:{d.Line}:{d.Col}: error {d.Code} {d.Message}");
            }

            if (doc.Diagnostics.Any(d => d.Severity == Severity.Error))
            {
                output.WriteLine("The song has errors; fix them first (ample check).");
                return 1;
            }

            // the first time a song is built with a component, that component is pinned in ample.lock (a later change of its code is AMP2203)
            if (modules.LockWithNewComponents(modules.Expand(await File.ReadAllTextAsync(song, token), spec)) is { } pinned)
            {
                await File.WriteAllTextAsync(Path.Combine(projectRoot, Ample.Engine.Library.LockFile.FileName), pinned, token);
            }

            using OpenProject open = await ProjectStore.OpenAsync(Fs, Clock, projectRoot, readOnly: false, token);
            Takebank bank = Takebank.Open(Fs, Clock, projectRoot);
            BarRange? range = null;
            string intent = preview is null ? "bake" : "preview";
            if (preview is not null && preview != "all")
            {
                string[] parts = preview.Split(':');
                range = new BarRange(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
            }

            using Ample.Plugins.PluginHost host = new(Ample.Plugins.VaultSecretProvider.FromEnvironment(Ample.Storage.Library.Library.DefaultHome()));
            (IReadOnlyList<Ample.Plugins.DiscoveredPlugin> plugins, _) = PluginCommands.Find(rootFolder, null);
            IReadOnlyList<IBlockRenderer> renderers = [];
            try
            {
                string jobsFolder = Path.Combine(projectRoot, ".ample", "tmp", "jobs");
                IReadOnlyList<IBlockRenderer> local = await Ample.Plugins.PluginRenderer.CreateAsync(host, plugins.Where(p => p.Manifest.Id is not ("flaky" or "echo" or "analysis-fake")), jobsFolder, settingsFor: id => ProjectPluginSettings(projectRoot, id), cancellationToken: token);
                renderers = Ample.Plugins.FaerRouting.Apply(local, open.Manifest.Settings.Faer, jobsFolder);
            }
            catch (AmpleException ex)
            {
                output.WriteLine($"Note: the plugins could not be started ({ex.DiagnosticCode}: {ex.Message}); blocks that need one are skipped.");
            }

            BuildReport report = await new BuildEngine(bank, spec, open.Manifest.Id, projectRoot).BuildAsync(doc, new BuildOptions(intent, range, only, ExtraRenderers: renderers), token);
            foreach (BlockResult r in report.Blocks)
            {
                output.WriteLine($"{r.Status,-9} {r.Block}{(r.Message is null ? string.Empty : "  " + r.Message)}");
            }

            output.WriteLine($"build: {report.Hits.ToString(CultureInfo.InvariantCulture)} from cache, {report.Misses.ToString(CultureInfo.InvariantCulture)} rendered, {report.Blocks.Count(b => b.Status == "skipped").ToString(CultureInfo.InvariantCulture)} skipped");
            if (report.Blocks.Any(b => b.Status is "failed" or "cancelled"))
            {
                return 1;
            }

            if (report.MasterTakeId is not null && only is null)
            {
                TakeRecord take = bank.GetTake(report.MasterTakeId);
                await using Stream data = bank.OpenData(take.Data[0].DataId);
                using MemoryStream ms = new();
                await data.CopyToAsync(ms, token);
                (short[] samples, int rate) = Wav.Decode(ms.ToArray());
                string target = outFile ?? Path.Combine(projectRoot, "out", Path.GetFileNameWithoutExtension(song) + ".wav");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
                AudioFile.WriteAs(ms.ToArray(), target);
                output.WriteLine($"master: {samples.Length.ToString(CultureInfo.InvariantCulture)} samples at {rate.ToString(CultureInfo.InvariantCulture)} Hz, peak {AudioMeter.Format(AudioMeter.PeakDbfs(samples))}, rms {AudioMeter.Format(AudioMeter.RmsDbfs(samples))}, loudness {(Loudness.IntegratedLufs(samples, rate) is { } lufs ? lufs.ToString("0.0", CultureInfo.InvariantCulture) + " LUFS" : "n/a")}; written to {target}");
            }

            if (only is not null && outFile is not null && !bakeFlag && report.Blocks.FirstOrDefault(b => b.Block == only)?.TakeId is { } blockTake)
            {
                TakeRecord blockRecord = bank.GetTake(blockTake);
                TakeDataRef picked = blockRecord.Data.FirstOrDefault(d => d.Role is "audio" or "out" or "mixed" or "mix") ?? blockRecord.Data[0];
                await using Stream blockData = bank.OpenData(picked.DataId);
                using MemoryStream blockBytes = new();
                await blockData.CopyToAsync(blockBytes, token);
                string blockTarget = Path.GetFullPath(outFile);
                AudioFile.WriteAs(blockBytes.ToArray(), blockTarget);
                output.WriteLine($"block {only}: {blockBytes.Length.ToString(CultureInfo.InvariantCulture)} bytes written to {blockTarget}");
            }

            if (bakeFlag && only is not null)
            {
                EditResult edit = Edits.AddFlag(doc, only, "bake");
                if (!edit.Ok)
                {
                    output.WriteLine($"AMP5252: {edit.Error}");
                    return 1;
                }

                using Journal journal = await Journal.OpenAsync(Fs, Clock, projectRoot, open.Manifest.Id.ToString("D"), cancellationToken: token);
                string relative = Path.GetRelativePath(projectRoot, song).Replace('\\', '/');
                await new CommandBus(journal, new CommandContext(Fs, projectRoot, Clock), "cli").ExecuteAsync(new WriteFileCommand(relative, Edits.Apply(doc.Text, edit.Edits)), token);
                output.WriteLine($"Baked '{only}': the song source now says bake.");
            }

            return 0;
        }
        catch (AmpleException ex)
        {
            output.WriteLine($"{ex.DiagnosticCode}: {ex.Message}");
            return 1;
        }
        finally
        {
            if (scratch is not null)
            {
                try { Directory.Delete(scratch, true); } catch (IOException) { /* a temp folder; the system cleans it */ } catch (UnauthorizedAccessException) { /* the same */ }
            }
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
