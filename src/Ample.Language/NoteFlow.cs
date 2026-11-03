using System.Globalization;

namespace Ample.Language;

/// <summary>One note expression of a document, with its result.</summary>
/// <param name="Block">The midi-clip block.</param>
/// <param name="Parameter">Its <c>notes</c> parameter.</param>
/// <param name="IsExpression">Whether the value is an expression (and not a plain literal).</param>
/// <param name="Result">What evaluating it gave (the notes, or the problems).</param>
/// <param name="StageIssues">The problems found at its bake stages (stale, dirty, no notes yet).</param>
public sealed record FlowEntry(BlockNode Block, Parameter Parameter, bool IsExpression, PipeResult Result, IReadOnlyList<PipeIssue> StageIssues)
{
    /// <summary>Gets the value up to the stage the evaluation was asked to stop at, or null.</summary>
    public PipeValue? Stopped { get; init; }

    /// <summary>Gets an upper bound of the number of notes the expression can make (worked out without running it).</summary>
    public System.Numerics.BigInteger Estimate { get; init; }

    /// <summary>Gets the recipe hash of that stage, or null.</summary>
    public string? StoppedInputHash { get; init; }
}

/// <summary>
/// The note pipelines of a whole document: every midi-clip whose notes are an expression is evaluated, in dependency order, with the notes of the other clips as the things a
/// <c>block.notes</c> reference reads. The result is used by the parser (diagnostics, <c>for auto</c>), by the build (the expression is replaced by the notes it makes) and by the bake.
/// </summary>
public static class NoteFlow
{
    /// <summary>Evaluates every midi-clip notes parameter of the blocks.</summary>
    /// <param name="blocks">The blocks of the document.</param>
    /// <param name="tempo">The song tempo as a decimal text.</param>
    /// <param name="limit">The largest number of notes a value may hold.</param>
    /// <param name="readFile">How a file of the project is read (for a bake that keeps its notes in a file), or null.</param>
    /// <param name="stopBlock">The block whose evaluation stops at a bake stage (to bake it), or null.</param>
    /// <param name="stopAt">The name of that stage, or null.</param>
    /// <param name="midiSource">How the notes of a <c>midi</c> block (a MIDI file) are read, or null (the editors: a placeholder is used so that the rest of the pipeline can still be checked).</param>
    /// <param name="masterSeed">The master-seed of the song (the root of every stable seed).</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <param name="helpers">The helpers, type aliases and constants of the file, or null.</param>
    /// <param name="limits">The limits of helper code, or null for the defaults.</param>
    /// <param name="clock">The clock of the volatile built-ins, or null for the system clock.</param>
    /// <param name="capabilities">The capability policy of the song, or null to allow everything.</param>
    /// <param name="environment">How environment(name) reads a value, or null.</param>
    /// <param name="isComponent">Whether a block is an instance of a component, or null. In the editors (no <paramref name="midiSource"/>) the notes of an instance are a placeholder so that the rest of the pipeline can be checked; the build and the bake read the expanded song, where an instance is a clip.</param>
    /// <param name="beatsPerBar">The beats of a bar in quarter notes (4 in 4/4; the meter of the song).</param>
    /// <returns>The entries by block name.</returns>
#pragma warning disable CA1068 // the cancellation token stays where the callers put it
    public static IReadOnlyDictionary<string, FlowEntry> Analyze(IReadOnlyList<BlockNode> blocks, string tempo, int limit, Func<string, string?>? readFile, string? stopBlock = null, string? stopAt = null, Func<BlockNode, PipeValue?>? midiSource = null, ulong masterSeed = 0, CancellationToken cancellation = default, NotePipeline.HelperTable? helpers = null, LangLimits? limits = null, Func<DateTime>? clock = null, CapabilityPolicy? capabilities = null, Func<string, string?>? environment = null, Func<BlockNode, bool>? isComponent = null, double beatsPerBar = 4.0)
#pragma warning restore CA1068
    {
        using IDisposable meterScope = NotePipeline.UseMeter((decimal)beatsPerBar);
        Dictionary<string, FlowEntry> done = new(StringComparer.Ordinal);
        HashSet<string> active = new(StringComparer.Ordinal);
        BlockNode? Clip(string name) => blocks.FirstOrDefault(b => !b.Skipped && b.Type == "midi-clip" && b.Name == name && b.Parameters.Any(p => p.Name == "notes"));

        NotePipeline.SourceInfo Source(string name)
        {
            BlockNode? block = Clip(name);
            BlockNode? midi = block is null ? blocks.FirstOrDefault(b => !b.Skipped && b.Type == "midi" && b.Name == name) : null;
            if (midi is not null)
            {
                if (midiSource is null) return new NotePipeline.SourceInfo(new PipeValue { Span = NotePipeline.Bar }, "midi:" + name, null);
                PipeValue? read = midiSource(midi);
                return read is null ? new NotePipeline.SourceInfo(null, null, $"The MIDI file of \"{name}\" cannot be read") : new NotePipeline.SourceInfo(read, "midi:" + NotePipeline.NotesHash(read), null);
            }

            if (block is null && blocks.FirstOrDefault(b => !b.Skipped && b.Name == name && isComponent?.Invoke(b) == true) is { } instance)
            {
                return midiSource is null
                    ? new NotePipeline.SourceInfo(new PipeValue { Span = NotePipeline.Bar }, "component:" + name, null)
                    : new NotePipeline.SourceInfo(null, null, $"\"{name}\" is an instance of the component {instance.Type}: the notes of an instance are only known in the expanded song");
            }

            if (block is null)
            {
                bool exists = blocks.Any(b => !b.Skipped && b.Name == name);
                return new NotePipeline.SourceInfo(null, null, exists ? $"\"{name}\" has no notes to read: only a midi-clip with notes can be read" : $"There is no block \"{name}\"");
            }

            if (active.Contains(name)) return new NotePipeline.SourceInfo(null, null, "cycle");
            FlowEntry entry = Resolve(block);
            return entry.Result.Value is null
                ? new NotePipeline.SourceInfo(null, null, $"The notes of \"{name}\" have problems")
                : new NotePipeline.SourceInfo(entry.Result.Value, entry.Result.Recipe, null);
        }

        FlowEntry Resolve(BlockNode block)
        {
            if (done.TryGetValue(block.Name, out FlowEntry? known)) return known;
            Parameter notes = block.Parameters.First(p => p.Name == "notes");
            string value = notes.Value;
            FlowEntry entry;
            if (!NotePipeline.IsExpression(value))
            {
                Parameter? ppq = block.Parameters.FirstOrDefault(p => p.Name == "ppq");
                if (ppq is not null && ppq.Value.Trim() != "1000000")
                {
                    entry = new FlowEntry(block, notes, false, new PipeResult { Errors = [new PipeIssue("unknown-reference", $"\"{block.Name}\" counts in ticks (ppq {ppq.Value.Trim()}); only clips with the default ppq can be read by a pipeline", 0, 1)] }, []);
                }
                else
                {
                    List<PipeIssue> bad = [];
                    List<PipeNote> events = NotePipeline.ReadNotes(value.Trim().Trim('"'), bad);
                    PipeValue v = new() { Events = events, Span = SpanOf(events) };
                    entry = new FlowEntry(block, notes, false, bad.Count > 0 ? new PipeResult { Errors = bad } : new PipeResult { Notes = NotePipeline.WriteNotes(events), Span = v.Span, Value = v, Recipe = "sha256:" + Hex("lit:" + NotePipeline.WriteNotes(events)) }, []);
                }
            }
            else
            {
                active.Add(block.Name);
                try
                {
                    Dictionary<string, string> streams = block.Parameters.Where(p => p.Name.StartsWith("random-stream:", StringComparison.Ordinal)).ToDictionary(p => p.Name["random-stream:".Length..], p => p.Value, StringComparer.Ordinal);
                    NotePipeline.Context context = new() { Tempo = tempo, Limit = limit, Source = Source, ReadFile = readFile, Cancellation = cancellation, StopAt = block.Name == stopBlock ? stopAt : null, MasterSeed = masterSeed, Streams = streams.Count > 0 ? streams : null, Helpers = helpers, Limits = limits ?? new LangLimits(), Clock = clock, Capabilities = capabilities, Environment = environment };
                    System.Numerics.BigInteger estimate = NotePipeline.Estimate(value, context);
                    PipeResult result = NotePipeline.Evaluate(value, context);
                    entry = new FlowEntry(block, notes, true, result, [.. context.StageIssues]) { Stopped = context.StoppedValue, StoppedInputHash = context.StoppedInputHash, Estimate = estimate };
                }
                finally
                {
                    active.Remove(block.Name);
                }
            }

            done[block.Name] = entry;
            return entry;
        }

        foreach (BlockNode block in blocks.Where(b => !b.Skipped && b.Type == "midi-clip" && b.Parameters.Any(p => p.Name == "notes"))) Resolve(block);
        return done;
    }

    /// <summary>Warnings about the stable names of the song: a name used once that is within two edits of a name used elsewhere is probably a typo (a new namespace would silently change the music).</summary>
    /// <param name="entries">The entries of the document.</param>
    /// <returns>The problems, each with the entry it belongs to.</returns>
    public static IReadOnlyList<(FlowEntry Entry, PipeIssue Issue)> SeedLint(IReadOnlyCollection<FlowEntry> entries)
    {
        List<(FlowEntry Entry, NotePipeline.SeedSite Site, string Name)> uses = [];
        Dictionary<string, int> count = new(StringComparer.Ordinal);
        foreach (FlowEntry e in entries)
        {
            foreach (Parameter p in e.Block.Parameters.Where(p => p.Name.StartsWith("random-stream:", StringComparison.Ordinal)))
            {
                if (NotePipeline.StableNameOf(p.Value) is { } streamName) count[streamName] = count.GetValueOrDefault(streamName) + 1;
            }

            if (!e.IsExpression) continue;
            foreach (NotePipeline.SeedSite site in NotePipeline.SeedSites(e.Parameter.Value))
            {
                if (site.Stable is not { } name) continue;
                uses.Add((e, site, name));
                count[name] = count.GetValueOrDefault(name) + 1;
            }
        }

        List<(FlowEntry, PipeIssue)> issues = [];
        foreach ((FlowEntry entry, NotePipeline.SeedSite site, string name) in uses.Where(u => count[u.Name] == 1))
        {
            string? best = null;
            int bestDistance = 3;
            foreach ((string other, int n) in count.Where(c => c.Value >= 2 && c.Key != name))
            {
                int d = Distance(name, other);
                if (d < bestDistance || (d == bestDistance && best is not null && n > count[best])) { best = other; bestDistance = d; }
            }

            if (best is not null) issues.Add((entry, new PipeIssue("seed-typo", $"The stable name \"{name}\" is used only once, and \"{best}\" is used elsewhere: a new name starts a new random family and changes the music", site.Start, site.End, best)));
        }

        return issues;
    }

    private static int Distance(string a, string b)
    {
        int[] row = [.. Enumerable.Range(0, b.Length + 1)];
        for (int i = 1; i <= a.Length; i++)
        {
            int prev = row[0];
            row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int keep = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = keep;
            }
        }

        return row[b.Length];
    }

    private static long SpanOf(List<PipeNote> events) => Math.Max(1, events.Count == 0 ? 0 : (events.Max(e => e.At + e.Len) + NotePipeline.Bar - 1) / NotePipeline.Bar) * NotePipeline.Bar;

    private static string Hex(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    /// <summary>The number of whole bars a span covers (at least one).</summary>
    /// <param name="span">The span in micro-beats.</param>
    /// <returns>The bars.</returns>
    public static int BarsOf(long span) => (int)Math.Max(1, (span + NotePipeline.Bar - 1) / NotePipeline.Bar);

    /// <summary>Formats a span for a bake stage (for example 8bars, or beats when it is not whole bars).</summary>
    /// <param name="span">The span in micro-beats.</param>
    /// <returns>The text.</returns>
    public static string SpanText(long span) => span % NotePipeline.Bar == 0 ? (span / NotePipeline.Bar).ToString(CultureInfo.InvariantCulture) + "bars" : (span / (double)NotePipeline.Beat).ToString("0.######", CultureInfo.InvariantCulture) + "beats";
}
