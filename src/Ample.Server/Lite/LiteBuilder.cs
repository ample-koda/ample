using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Ample.Core.Abstractions;
using Ample.Audio.IO;
using Ample.Engine.Engine;
using Ample.Storage.Projects;
using Ample.Storage.Takes;
using Ample.Language;

namespace Ample.Server.Lite;

/// <summary>One problem of a Lite build as the page shows it.</summary>
/// <param name="Code">The diagnostic code.</param>
/// <param name="Severity">error, warning or info.</param>
/// <param name="Line">The 1-based line in the song (0 when it is not about a line).</param>
/// <param name="Col">The 1-based column.</param>
/// <param name="Message">The message, without a path of this machine.</param>
public sealed record LiteDiagnostic(string Code, string Severity, int Line, int Col, string Message);

/// <summary>The outcome of a Lite build.</summary>
/// <param name="Ok">Whether there is a result to hear.</param>
/// <param name="Diagnostics">The problems found.</param>
/// <param name="OutputId">The id to fetch the audio with, or null.</param>
/// <param name="Seconds">How long the build took.</param>
/// <param name="AudioSeconds">The length of the audio.</param>
/// <param name="Bytes">The size of the audio.</param>
public sealed record LiteBuildResult(bool Ok, IReadOnlyList<LiteDiagnostic> Diagnostics, string? OutputId, double Seconds, double AudioSeconds, long Bytes);

/// <summary>
/// Builds a Lite project: a private workspace with the files of the page, no user library, no plugin host, a takebank inside the workspace, a time limit, and the workspace gone
/// afterwards whether the build worked, failed, was cancelled or ran out of time (plan/lite/PLAN.md, D7 to D10).
/// </summary>
public static partial class LiteBuilder
{
    [GeneratedRegex(@"[A-Za-z]:[\\/][^\s""'<>|]*|\\\\[^\s""']+|/(?:home|usr|var|tmp|etc|mnt|root|opt)/[^\s""']*", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AbsolutePath();

    /// <summary>Builds the song.</summary>
    /// <param name="spec">The language data.</param>
    /// <param name="libraryHome">The AMPLE library home (the work root lives in it).</param>
    /// <param name="entry">The song to build, a path in <paramref name="files"/>.</param>
    /// <param name="files">The files of the project.</param>
    /// <param name="limits">The limits.</param>
    /// <param name="outputs">Where the audio is kept.</param>
    /// <param name="cancellationToken">Cancelled when the page goes away.</param>
    /// <param name="log">Told, step by step, what the build is doing: a line of text and how far the whole build is (0 to 1, or -1 when unchanged). Called from build threads.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="LiteRejectedException">The project is not acceptable.</exception>
    public static async Task<LiteBuildResult> BuildAsync(SpecData spec, string libraryHome, string entry, IReadOnlyList<LiteFile> files, LiteLimits limits, LiteOutputs outputs, CancellationToken cancellationToken, Action<string, double>? log = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(outputs);
        LitePolicy.CheckFiles(entry, files, limits);
        log?.Invoke($"Checked {files.Count} file{(files.Count == 1 ? string.Empty : "s")}: all are allowed.", 0.02);
        Stopwatch clock = Stopwatch.StartNew();
        using LiteWorkspace workspace = LiteWorkspace.Create(libraryHome, files);
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // a limit of no seconds stops the build at once; a timer of zero seconds only fires a moment later, and a fast build could be done before it
        if (limits.BuildSeconds <= 0) limit.Cancel();
        else limit.CancelAfter(TimeSpan.FromSeconds(limits.BuildSeconds));
        List<LiteDiagnostic> found = [];
        try
        {
            Dictionary<string, string> texts = files.Where(f => LitePolicy.IsText(f.Path)).ToDictionary(f => f.Path, f => Encoding.UTF8.GetString(f.Data), StringComparer.Ordinal);
            PhysicalFileSystem fs = new();
            SystemClock time = new();
            using OpenProject open = await ProjectStore.CreateAsync(fs, time, new GuidV7Generator(), workspace.Root, "lite", 1UL, limit.Token).ConfigureAwait(false);
            using Ample.Engine.Modules.ModuleService modules = new(workspace.Root, (Ample.Storage.Library.Library?)null);
            log?.Invoke($"Reading {entry} and the components it uses.", 0.04);
            AmpleDocument doc = modules.ParseExpanded(texts[entry], spec);
            log?.Invoke($"Read the song: {doc.Blocks.Count} blocks.", 0.08);
            found.AddRange(doc.Diagnostics.Where(d => d.Severity != Severity.Info || d.Code.StartsWith("AMP", StringComparison.Ordinal)).Take(200).Select(d => Map(d, workspace.Root)));
            if (doc.Diagnostics.Any(d => d.Severity == Severity.Error)) return Result(false, found, null, clock, 0, 0);
            LitePolicy.CheckSong(texts, doc, spec, limits);
            Takebank bank = Takebank.Open(fs, time, workspace.Root);
            log?.Invoke("The song passes the Remix rules. Rendering the blocks (2 at a time).", 0.1);
            BuildOptions options = new("bake", null, null, Parallelism: 2);
            if (log is not null) options = options with { Progress = ProgressLog(log, workspace.Root, doc.Blocks.Count), Detail = DetailLog(log, workspace.Root, doc.Blocks.Count) };
            BuildReport report = await new BuildEngine(bank, spec, open.Manifest.Id, workspace.Root).BuildAsync(doc, options, limit.Token).ConfigureAwait(false);
            // the engine reports a block it had to stop as cancelled, without throwing: when the time limit is what stopped it, that is the answer (AMPL923), not a failed block
            if (limit.IsCancellationRequested && !cancellationToken.IsCancellationRequested) throw new OperationCanceledException(limit.Token);
            log?.Invoke("All blocks are done. Reading the master.", 0.92);
            foreach (BlockResult block in report.Blocks.Where(b => b.Status is "failed" or "cancelled"))
                found.Add(new LiteDiagnostic("AMPL920", "error", 0, 0, Clean($"The block \"{block.Block}\" {block.Status}{(block.Message is null ? string.Empty : ": " + block.Message)}", workspace.Root)));
            if (report.MasterTakeId is null || found.Any(d => d.Severity == "error" && d.Code == "AMPL920"))
            {
                if (report.MasterTakeId is null && found.All(d => d.Severity != "error")) found.Add(new LiteDiagnostic("AMPL921", "error", 0, 0, "The song makes no sound: add a master block that listens to something."));
                return Result(false, found, null, clock, 0, 0);
            }

            TakeRecord take = bank.GetTake(report.MasterTakeId);
            await using Stream data = bank.OpenData(take.Data[0].DataId);
            using MemoryStream copy = new();
            await data.CopyToAsync(copy, limit.Token).ConfigureAwait(false);
            byte[] wav = copy.ToArray();
            double seconds = SecondsOf(wav);
            if (seconds > limits.MaxOutputSeconds)
            {
                found.Add(new LiteDiagnostic("AMPL922", "error", 0, 0, $"The song is {seconds:0} seconds long; Remix builds at most {limits.MaxOutputSeconds} seconds."));
                return Result(false, found, null, clock, seconds, 0);
            }

            log?.Invoke($"Master is {seconds:0.0} seconds long ({wav.LongLength / 1024} KB). Keeping it for playback.", 0.98);
            string id = outputs.Put(wav, limits);
            return Result(true, found, id, clock, seconds, wav.LongLength);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            found.Add(new LiteDiagnostic("AMPL923", "error", 0, 0, $"The build took longer than {limits.BuildSeconds} seconds and was stopped."));
            return Result(false, found, null, clock, 0, 0);
        }
        catch (AmpleException ex)
        {
            found.Add(new LiteDiagnostic(ex.DiagnosticCode, "error", 0, 0, Clean(ex.Message, workspace.Root)));
            return Result(false, found, null, clock, 0, 0);
        }
    }

    // the build tells the page what happens: a block that is queued or running (each 10 percent step, and each sub-step once), and a block that is done
    private static Action<BlockProgress> DetailLog(Action<string, double> log, string root, int total)
    {
        Dictionary<string, double> shown = new(StringComparer.Ordinal);
        Dictionary<string, string> state = new(StringComparer.Ordinal);
        HashSet<string> steps = new(StringComparer.Ordinal);
        object gate = new();
        return p =>
        {
            lock (gate)
            {
                shown.TryGetValue(p.Block, out double last);
                if (p.State == "queued") { shown[p.Block] = 0; return; }
                string text = Clean(p.Block, root);
                if (!state.TryGetValue(p.Block, out string? was) || was != p.State)
                {
                    state[p.Block] = p.State;
                    log($"{text}: started.", Overall(shown, total));
                }

                if (p.Steps.Count > 0 && steps.Add(p.Block + "|" + p.Steps[^1].Name)) log($"{text}: {Clean(p.Steps[^1].Name, root)}.", Overall(shown, total));
                if (p.Fraction >= last + 0.25 && p.Fraction < 1)
                {
                    shown[p.Block] = p.Fraction;
                    log($"{text}: {Math.Round(p.Fraction * 100):0}%.", Overall(shown, total));
                }
            }
        };
    }

    private static Action<string, string> ProgressLog(Action<string, double> log, string root, int total)
    {
        int done = 0;
        return (block, status) =>
        {
            int n = Interlocked.Increment(ref done);
            log($"{Clean(block, root)}: {status}.", 0.1 + (0.8 * Math.Min(1.0, (double)n / Math.Max(1, total))));
        };
    }

    private static double Overall(Dictionary<string, double> shown, int total) => 0.1 + (0.8 * Math.Min(1.0, shown.Values.Sum() / Math.Max(1, total)));

    private static LiteBuildResult Result(bool ok, List<LiteDiagnostic> found, string? id, Stopwatch clock, double audio, long bytes) => new(ok, found, id, Math.Round(clock.Elapsed.TotalSeconds, 2), Math.Round(audio, 2), bytes);

    private static LiteDiagnostic Map(Diagnostic d, string root) => new(d.Code, d.Severity.ToString().ToLowerInvariant(), d.Line, d.Col, Clean(d.Message, root));

    /// <summary>Takes the workspace path and any other absolute path out of a message.</summary>
    /// <param name="text">The message.</param>
    /// <param name="root">The workspace folder.</param>
    /// <returns>The message without paths of this machine.</returns>
    public static string Clean(string text, string root)
    {
        ArgumentNullException.ThrowIfNull(text);
        string cleaned = text;
        if (!string.IsNullOrEmpty(root))
        {
            cleaned = cleaned.Replace(root + Path.DirectorySeparatorChar, string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace(root.Replace('\\', '/') + "/", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace(root, "the project", StringComparison.OrdinalIgnoreCase);
        }

        return AbsolutePath().Replace(cleaned, "<path>");
    }

    /// <summary>The length of PCM WAV bytes in seconds (from the header).</summary>
    /// <param name="wav">The WAV bytes.</param>
    /// <returns>The seconds, 0 when the header is not PCM WAV.</returns>
    public static double SecondsOf(byte[] wav)
    {
        ArgumentNullException.ThrowIfNull(wav);
        if (wav.Length < 44) return 0;
        int byteRate = BitConverter.ToInt32(wav, 28);
        return byteRate <= 0 ? 0 : (wav.Length - 44d) / byteRate;
    }
}
