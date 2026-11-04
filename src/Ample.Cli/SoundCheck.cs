using System.Globalization;
using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Audio.IO;
using Ample.Engine.Engine;
using Ample.Storage.BlackBox;
using Ample.Storage.Projects;
using Ample.Storage.Takes;
using Ample.Docs;
using Ample.Language;

namespace Ample.Cli;

/// <summary>
/// <c>ample doctor &lt;folder&gt; --sound &lt;song&gt;</c>: builds the song (what is cached is reused) and looks at the sound of every block (see <see cref="SoundAnalysis"/>).
/// With <c>--json</c> the findings are written as JSON; with <c>--fix</c> the suggested effect blocks (normalize, volume) are put into the song, the journal keeping the old text.
/// Only names and numbers are printed.
/// </summary>
internal static class SoundCheck
{
    private static readonly PhysicalFileSystem Fs = new();
    private static readonly SystemClock Clock = new();
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    internal static async Task<int> RunAsync(TextWriter output, string songPath, string projectRoot, string? rootFolder, bool json, bool fix, CancellationToken token)
    {
        try
        {
            string song = Path.IsPathRooted(songPath) ? songPath : Path.GetFullPath(Path.Combine(projectRoot, songPath));
            if (!File.Exists(song))
            {
                output.WriteLine($"AMP5000: the song '{songPath}' does not exist.");
                return 1;
            }

            SpecData spec = (rootFolder is null ? SpecRoot.Find(Directory.GetCurrentDirectory()) : SpecRoot.At(rootFolder)).LoadSpecData();
            using Ample.Engine.Modules.ModuleService modules = Ample.Engine.Modules.ModuleService.WithUserLibrary(projectRoot, Clock);
            string text = await File.ReadAllTextAsync(song, token);
            AmpleDocument doc = modules.ParseExpanded(text, spec);
            if (doc.Diagnostics.Any(d => d.Severity == Severity.Error))
            {
                output.WriteLine("The song has errors; fix them first (ample check).");
                return 1;
            }

            using OpenProject open = await ProjectStore.OpenAsync(Fs, Clock, projectRoot, readOnly: false, token);
            Takebank bank = Takebank.Open(Fs, Clock, projectRoot);
            BuildReport report = await new BuildEngine(bank, spec, open.Manifest.Id, projectRoot).BuildAsync(doc, new BuildOptions("bake", null, null), token);
            (IReadOnlyList<SoundFinding> findings, MasterLevels? master) = await SoundAnalysis.AnalyseAsync(doc, spec, bank, report, token);
            if (json)
            {
                output.WriteLine(JsonSerializer.Serialize(new { findings = findings.Select(f => new { f.Severity, f.Code, f.Block, f.Message, f.Fix }), master = master is null ? null : new { master.PeakDbfs, master.RmsDbfs, master.Lufs } }, Indented));
            }
            else
            {
                foreach (SoundFinding f in findings)
                {
                    output.WriteLine($"{f.Severity} {f.Code} {f.Block}: {f.Message}{(f.Fix is null ? string.Empty : "  [fix: " + f.Fix + "]")}");
                }

                if (master is not null)
                {
                    output.WriteLine($"master: peak {master.PeakDbfs.ToString("0.0", CultureInfo.InvariantCulture)} dBFS, rms {master.RmsDbfs.ToString("0.0", CultureInfo.InvariantCulture)} dBFS, loudness {(master.Lufs is { } lufs ? lufs.ToString("0.0", CultureInfo.InvariantCulture) + " LUFS" : "n/a")}");
                }
            }

            if (fix)
            {
                int applied = await ApplyFixesAsync(output, song, projectRoot, open.Manifest.Id.ToString("D"), spec, text, findings, token);
                if (!json) output.WriteLine($"sound: {applied.ToString(CultureInfo.InvariantCulture)} fix(es) put into the song" + (applied > 0 ? " (the journal keeps the old text; build again)" : string.Empty));
            }

            int errors = findings.Count(f => f.Severity == "error"), warnings = findings.Count(f => f.Severity == "warning");
            if (!json) output.WriteLine(findings.Count == 0 ? "sound: nothing to report" : $"sound: {errors.ToString(CultureInfo.InvariantCulture)} error(s), {warnings.ToString(CultureInfo.InvariantCulture)} warning(s)");
            return errors > 0 ? 1 : 0;
        }
        catch (AmpleException ex)
        {
            output.WriteLine($"{ex.DiagnosticCode}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Puts the suggested effect after each block that has a fix (once per block): normalize for a very quiet one, a volume block with a lower gain for a clipping one.</summary>
    private static async Task<int> ApplyFixesAsync(TextWriter output, string song, string projectRoot, string projectId, SpecData spec, string text, IReadOnlyList<SoundFinding> findings, CancellationToken token)
    {
        int applied = 0;
        string current = text;
        foreach (SoundFinding f in findings.Where(f => f.Fix is not null).DistinctBy(f => f.Block))
        {
            string? error;
            string? next = f.Fix == "normalize"
                ? Edits.InsertEffectAfter(current, spec, f.Block, "normalize", f.Block + "-normalize", [("peak", "-3")], out error)
                : Edits.InsertEffectAfter(current, spec, f.Block, "volume", f.Block + "-lower", [("gain", f.Fix!["volume:".Length..])], out error);
            if (next is null)
            {
                output.WriteLine($"fix {f.Block}: not done ({error})");
                continue;
            }

            current = next;
            applied++;
        }

        if (applied > 0)
        {
            using Journal journal = await Journal.OpenAsync(Fs, Clock, projectRoot, projectId, cancellationToken: token);
            string relative = Path.GetRelativePath(projectRoot, song).Replace('\\', '/');
            await new CommandBus(journal, new CommandContext(Fs, projectRoot, Clock), "cli").ExecuteAsync(new WriteFileCommand(relative, current), token);
        }

        return applied;
    }
}
