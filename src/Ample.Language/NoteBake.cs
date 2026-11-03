using System.Globalization;
using System.Text;

namespace Ample.Language;

/// <summary>A bake or detach that cannot be done; the code is the diagnostic code the caller reports.</summary>
/// <param name="code">The diagnostic code.</param>
/// <param name="message">The message.</param>
public sealed class NoteBakeException(string code, string message) : Exception(message)
{
    /// <summary>Gets the diagnostic code.</summary>
    public string Code { get; } = code;
}

/// <summary>The result of a bake or a detach: the new text of the song and, when the notes were put in a file, that file.</summary>
/// <param name="Text">The new song text.</param>
/// <param name="FilePath">The project-relative file the notes were written for, or null.</param>
/// <param name="FileContent">The content of that file, or null.</param>
/// <param name="Notes">The number of notes stored.</param>
/// <param name="Stage">The name of the stage (empty for a detach).</param>
/// <param name="NotesHash">The signature of the stored notes (empty for a detach).</param>
/// <param name="InputHash">The recipe hash of the stage (empty for a detach).</param>
public sealed record BakeOutcome(string Text, string? FilePath, string? FileContent, int Notes, string Stage, string NotesHash, string InputHash);

/// <summary>
/// The bake of a note pipeline (docs/language/12-note-pipelines.md section 6): the notes of a pipeline up to a <c>bake</c> stage are stored in that stage with the hash of what they were made from and
/// their own signature, inline or in a file. Pure text work: the caller writes the text (and the file) in one journaled step, so a failure leaves the song as it was.
/// </summary>
public static class NoteBake
{
    private const int LineWidth = 96;

    /// <summary>Bakes the pipeline of a clip up to a stage.</summary>
    /// <param name="songText">The text of the song.</param>
    /// <param name="spec">The language spec.</param>
    /// <param name="block">The midi-clip block.</param>
    /// <param name="stage">The stage to bake, or null for the last one (a stage called <c>baked</c> is added when there is none).</param>
    /// <param name="filePath">A project-relative file to keep the notes in, or null to keep them in the stage.</param>
    /// <param name="readFile">How a project file is read (stages that keep their notes in a file), or null.</param>
    /// <param name="limit">The largest number of notes.</param>
    /// <param name="midiSource">How the notes of a midi block (a MIDI file) are read, or null.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public static BakeOutcome Bake(string songText, SpecData spec, string block, string? stage, string? filePath, Func<string, string?>? readFile, int limit, Func<BlockNode, PipeValue?>? midiSource = null, CancellationToken cancellation = default)
    {
        bool crlf = songText.Contains("\r\n", StringComparison.Ordinal);
        string text = songText.Replace("\r\n", "\n", StringComparison.Ordinal);
        (BlockNode b, Parameter p, ParseOptions options) = Find(text, spec, block, readFile, limit, midiSource, out AmpleDocument doc);
        List<PipeIssue> issues = [];
        IReadOnlyList<BakeStage> stages = NotePipeline.Stages(p.Value, issues);
        if (issues.Count > 0) throw new NoteBakeException("AMP1111", issues[0].Message + ".");
        string name = stage ?? (stages.Count > 0 ? stages[^1].Name : "baked");
        if (stages.All(s => s.Name != name))
        {
            string indent = Indent(doc, p, p.Value.Length);
            string added = p.Value.TrimEnd() + "\n" + indent + "  |> bake " + name;
            text = Replace(text, doc, p, added);
            (b, p, options) = Find(text, spec, block, readFile, limit, midiSource, out doc);
            stages = NotePipeline.Stages(p.Value, issues);
        }

        string tempo = (doc.Project?.Tempo ?? 120).ToString(CultureInfo.InvariantCulture);
        IReadOnlyDictionary<string, FlowEntry> flow = NoteFlow.Analyze(doc.Blocks, tempo, limit, readFile, block, name, midiSource, doc.Project?.MasterSeed ?? 0, cancellation, doc.HelperTable, LangLimits.From(doc.Settings), null, doc.CapabilityPolicy, null, null, doc.Project?.BeatsPerBar ?? 4.0);
        FlowEntry entry = flow[block];
        if (entry.Result.Errors.Count > 0) throw new NoteBakeException("AMP2024", entry.Result.Errors[0].Message + ".");
        PipeValue value = entry.Stopped ?? throw new NoteBakeException("AMP2034", $"The stage \"{name}\" was not reached.");
        BakeStage target = stages.First(s => s.Name == name);
        string notes = NotePipeline.WriteNotes(value.Events);
        string notesHash = NotePipeline.NotesHash(value);
        string input = entry.StoppedInputHash ?? string.Empty;
        string body = StageText(name, target.Strict, input, notesHash, NoteFlow.SpanText(value.Span), filePath is null ? notes : null, filePath, Indent(doc, p, target.Start));
        string newValue = p.Value[..target.Start] + body + p.Value[target.End..];
        string result = Replace(text, doc, p, newValue);
        _ = (b, options);
        return new BakeOutcome(crlf ? result.Replace("\n", "\r\n", StringComparison.Ordinal) : result, filePath, filePath is null ? null : notes + "\n", value.Events.Count, name, notesHash, input);
    }

    /// <summary>Turns the pipeline of a clip into plain notes (what it makes now), the way to go back to an ordinary clip.</summary>
    /// <param name="songText">The text of the song.</param>
    /// <param name="spec">The language spec.</param>
    /// <param name="block">The midi-clip block.</param>
    /// <param name="readFile">How a project file is read, or null.</param>
    /// <param name="limit">The largest number of notes.</param>
    /// <param name="midiSource">How the notes of a midi block (a MIDI file) are read, or null.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public static BakeOutcome Detach(string songText, SpecData spec, string block, Func<string, string?>? readFile, int limit, Func<BlockNode, PipeValue?>? midiSource = null, CancellationToken cancellation = default)
    {
        bool crlf = songText.Contains("\r\n", StringComparison.Ordinal);
        string text = songText.Replace("\r\n", "\n", StringComparison.Ordinal);
        (_, Parameter p, _) = Find(text, spec, block, readFile, limit, midiSource, out AmpleDocument doc);
        string tempo = (doc.Project?.Tempo ?? 120).ToString(CultureInfo.InvariantCulture);
        FlowEntry entry = NoteFlow.Analyze(doc.Blocks, tempo, limit, readFile, null, null, midiSource, doc.Project?.MasterSeed ?? 0, cancellation, doc.HelperTable, LangLimits.From(doc.Settings), null, doc.CapabilityPolicy, null, null, doc.Project?.BeatsPerBar ?? 4.0)[block];
        if (entry.Result.Notes is null) throw new NoteBakeException("AMP2024", (entry.Result.Errors.FirstOrDefault()?.Message ?? "The notes could not be worked out") + ".");
        string result = Replace(text, doc, p, entry.Result.Notes);
        return new BakeOutcome(crlf ? result.Replace("\n", "\r\n", StringComparison.Ordinal) : result, null, null, entry.Result.Value!.Events.Count, string.Empty, string.Empty, string.Empty);
    }

    /// <summary>How a copy of a clip relates to the stable random namespaces of the original (docs/language/13-randomness.md section 5).</summary>
    public enum SeedCopyMode
    {
        /// <summary>Keep the names: the copy makes exactly the same random choices.</summary>
        Reuse,

        /// <summary>Append a fork label to every stable seed: its own deterministic branch of the same parents.</summary>
        Fork,

        /// <summary>New song-global names (pulse_2, pulse_3, ...): a separate family.</summary>
        Independent,
    }

    /// <summary>Rewrites the stable seeds of a clip that was just copied, so that the copy reuses, forks or no longer shares the random namespaces of the original.</summary>
    /// <param name="songText">The text of the song (the copy is already in it).</param>
    /// <param name="spec">The language spec.</param>
    /// <param name="block">The copied clip.</param>
    /// <param name="mode">What the copy should do.</param>
    /// <returns>The new text of the song.</returns>
    public static string CopySeeds(string songText, SpecData spec, string block, SeedCopyMode mode)
    {
        if (mode == SeedCopyMode.Reuse) return songText;
        bool crlf = songText.Contains("\r\n", StringComparison.Ordinal);
        string text = songText.Replace("\r\n", "\n", StringComparison.Ordinal);
        AmpleDocument doc = Parser.Parse(text, spec);
        BlockNode b = doc.Blocks.FirstOrDefault(x => x.Type == "midi-clip" && x.Name == block) ?? throw new NoteBakeException("AMP5501", $"The block \"{block}\" is not a midi-clip.");
        HashSet<string> taken = new(StringComparer.Ordinal);
        foreach (BlockNode other in doc.Blocks.Where(x => x.Type == "midi-clip"))
        {
            foreach (Parameter p in other.Parameters)
            {
                if (p.Name == "notes") foreach (NotePipeline.SeedSite s in NotePipeline.SeedSites(p.Value)) { if (s.Stable is { } n) taken.Add(n); }
                else if (p.Name.StartsWith("random-stream:", StringComparison.Ordinal) && NotePipeline.StableNameOf(p.Value) is { } sn) taken.Add(sn);
            }
        }

        Dictionary<string, string> renamed = new(StringComparer.Ordinal);
        string Fresh(string name)
        {
            if (renamed.TryGetValue(name, out string? known)) return known;
            string baseName = System.Text.RegularExpressions.Regex.Replace(name, "_[0-9]+$", string.Empty, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
            for (int n = 2; ; n++)
            {
                string candidate = baseName + "_" + n.ToString(CultureInfo.InvariantCulture);
                if (taken.Add(candidate)) { renamed[name] = candidate; return candidate; }
            }
        }

        string label = "copy-" + block;
        string Rewrite(string value)
        {
            IReadOnlyList<NotePipeline.SeedSite> sites = NotePipeline.SeedSites(value);
            StringBuilder sb = new(value);
            foreach (NotePipeline.SeedSite site in sites.Where(s => s.Stable is not null).OrderByDescending(s => s.Start))
            {
                string replaced = mode == SeedCopyMode.Fork
                    ? site.Text + ".fork(\"" + label + "\")"
                    : site.Text.Replace("stable(\"" + site.Stable + "\")", "stable(\"" + Fresh(site.Stable!) + "\")", StringComparison.Ordinal);
                sb.Remove(site.Start, site.End - site.Start).Insert(site.Start, replaced);
            }

            return sb.ToString();
        }

        string RewriteStream(string value)
        {
            string? name = NotePipeline.StableNameOf(value);
            if (name is null) return value;
            return mode == SeedCopyMode.Fork ? value.TrimEnd() + ".fork(\"" + label + "\")" : value.Replace("stable(\"" + name + "\")", "stable(\"" + Fresh(name) + "\")", StringComparison.Ordinal);
        }

        List<string> lines = [.. text.Split('\n')];
        // later lines first, so that the line numbers of the earlier ones stay right
        foreach (Parameter p in b.Parameters.OrderByDescending(p => p.Line))
        {
            if (p.Name == "notes" && NotePipeline.IsExpression(p.Value))
            {
                string rewritten = Rewrite(p.Value);
                string prefix = lines[p.Line][..Math.Min(p.ValueColumn, lines[p.Line].Length)];
                lines.RemoveRange(p.Line, p.EndLine - p.Line + 1);
                lines.InsertRange(p.Line, (prefix + rewritten).Split('\n'));
            }
            else if (p.Name.StartsWith("random-stream:", StringComparison.Ordinal))
            {
                string name = p.Name["random-stream:".Length..];
                int at = lines[p.Line].IndexOf(name, lines[p.Line].IndexOf("random-stream", StringComparison.Ordinal) + 13, StringComparison.Ordinal) + name.Length;
                lines[p.Line] = lines[p.Line][..at] + " " + RewriteStream(p.Value);
            }
        }

        string result = string.Join('\n', lines);
        return crlf ? result.Replace("\n", "\r\n", StringComparison.Ordinal) : result;
    }

    /// <summary>Replaces every <c>random-seed()</c> of the song by a whole number chosen from OS randomness, once: from then on it is an ordinary explicit seed and the song is reproducible.</summary>
    /// <param name="songText">The text of the song.</param>
    /// <param name="pick">Gives a fresh random number (the OS random generator in the server).</param>
    /// <returns>The new text and how many seeds were picked.</returns>
    public static (string Text, int Count) PickSeeds(string songText, Func<long> pick)
    {
        int count = 0;
        List<string> lines = [.. songText.Split('\n')];
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            int hash = line.IndexOf('#', StringComparison.Ordinal);
            string code = hash < 0 ? line : line[..hash], comment = hash < 0 ? string.Empty : line[hash..];
            System.Text.RegularExpressions.Regex spelled = new(@"(?:random-seed|randomSeed)\(\s*\)", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
            while (spelled.Match(code) is { Success: true } found)
            {
                int at = found.Index;
                code = code[..at] + pick().ToString(CultureInfo.InvariantCulture) + code[(at + found.Length)..];
                count++;
            }

            lines[i] = code + comment;
        }

        return (string.Join('\n', lines), count);
    }

    /// <summary>Copies a clip (its whole block) under a new name, right after the original, and applies the seed mode to the copy.</summary>
    /// <param name="songText">The text of the song.</param>
    /// <param name="spec">The language spec.</param>
    /// <param name="block">The clip to copy.</param>
    /// <param name="newName">The name of the copy.</param>
    /// <param name="mode">What the copy does with the stable random namespaces of the original.</param>
    /// <returns>The new text of the song.</returns>
    public static string DuplicateClip(string songText, SpecData spec, string block, string newName, SeedCopyMode mode)
    {
        bool crlf = songText.Contains("\r\n", StringComparison.Ordinal);
        string text = songText.Replace("\r\n", "\n", StringComparison.Ordinal);
        AmpleDocument doc = Parser.Parse(text, spec);
        BlockNode b = doc.Blocks.FirstOrDefault(x => x.Type == "midi-clip" && x.Name == block) ?? throw new NoteBakeException("AMP5501", $"The block \"{block}\" is not a midi-clip.");
        if (doc.Blocks.Any(x => x.Name == newName)) throw new NoteBakeException("AMP5285", $"There is already a block called \"{newName}\".");
        if (b.CloseLine < b.Line) throw new NoteBakeException("AMP5501", $"The block \"{block}\" is not closed.");
        List<string> lines = [.. text.Split('\n')];
        List<string> copy = lines.GetRange(b.Line, b.CloseLine - b.Line + 1);
        copy[0] = System.Text.RegularExpressions.Regex.Replace(copy[0], "^(\\s*\\S+\\s+)(\"?)" + System.Text.RegularExpressions.Regex.Escape(b.RawName.Trim('"')) + "(\"?)", "${1}${2}" + newName + "${3}", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        lines.InsertRange(b.CloseLine + 1, ["", .. copy]);
        string result = CopySeeds(string.Join('\n', lines), spec, newName, mode);
        return crlf ? result.Replace("\n", "\r\n", StringComparison.Ordinal) : result;
    }

    /// <summary>Where the randomness of a clip comes from: every use of a seed (operator, seed, derived key, values drawn), its random-streams, and the stable names of the whole song with how often each is used.</summary>
    /// <param name="songText">The text of the song.</param>
    /// <param name="spec">The language spec.</param>
    /// <param name="block">The clip.</param>
    /// <param name="readFile">How a project file is read, or null.</param>
    /// <param name="midiSource">How a midi block is read, or null.</param>
    /// <returns>The provenance.</returns>
    public static (IReadOnlyList<SeedUseInfo> Seeds, IReadOnlyList<StreamUseInfo> Streams, IReadOnlyList<(string Name, int Count)> Names) Provenance(string songText, SpecData spec, string block, Func<string, string?>? readFile, Func<BlockNode, PipeValue?>? midiSource)
    {
        AmpleDocument doc = Parser.Parse(songText.Replace("\r\n", "\n", StringComparison.Ordinal), spec, new ParseOptions { NoteLimit = NotePipeline.DefaultBuildLimit, ReadFile = readFile, MidiSource = midiSource });
        string tempo = (doc.Project?.Tempo ?? 120).ToString(CultureInfo.InvariantCulture);
        IReadOnlyDictionary<string, FlowEntry> flow = NoteFlow.Analyze(doc.Blocks, tempo, NotePipeline.DefaultBuildLimit, readFile, null, null, midiSource, doc.Project?.MasterSeed ?? 0, default, doc.HelperTable, LangLimits.From(doc.Settings), null, doc.CapabilityPolicy, null, null, doc.Project?.BeatsPerBar ?? 4.0);
        flow.TryGetValue(block, out FlowEntry? mine);
        Dictionary<string, int> names = new(StringComparer.Ordinal);
        foreach (FlowEntry e in flow.Values)
        {
            if (e.IsExpression) foreach (NotePipeline.SeedSite s in NotePipeline.SeedSites(e.Parameter.Value)) { if (s.Stable is { } n) names[n] = names.GetValueOrDefault(n) + 1; }
            foreach (Parameter p in e.Block.Parameters.Where(p => p.Name.StartsWith("random-stream:", StringComparison.Ordinal))) { if (NotePipeline.StableNameOf(p.Value) is { } sn) names[sn] = names.GetValueOrDefault(sn) + 1; }
        }

        return (mine?.Result.Seeds ?? [], mine?.Result.Streams ?? [], [.. names.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => (x.Key, x.Value))]);
    }

    private static (BlockNode, Parameter, ParseOptions) Find(string text, SpecData spec, string block, Func<string, string?>? readFile, int limit, Func<BlockNode, PipeValue?>? midiSource, out AmpleDocument doc)
    {
        ParseOptions options = new() { NoteLimit = limit, ReadFile = readFile, MidiSource = midiSource };
        doc = Parser.Parse(text, spec, options);
        BlockNode b = doc.Blocks.FirstOrDefault(x => x.Type == "midi-clip" && x.Name == block) ?? throw new NoteBakeException("AMP5501", $"The block \"{block}\" is not a midi-clip.");
        Parameter p = b.Parameters.FirstOrDefault(x => x.Name == "notes") ?? throw new NoteBakeException("AMP5702", $"The clip \"{block}\" has no notes.");
        if (!NotePipeline.IsExpression(p.Value)) throw new NoteBakeException("AMP5702", $"The notes of \"{block}\" are not a pipeline: write one first (for example notes {block}.notes |> bake name).");
        return (b, p, options);
    }

    private static string Indent(AmpleDocument doc, Parameter p, int offset)
    {
        string value = p.Value;
        int s = Math.Clamp(offset, 0, value.Length), nl = 0, lineStart = 0;
        for (int k = 0; k < s; k++) if (value[k] == '\n') { nl++; lineStart = k + 1; }
        string line = nl == 0 ? doc.Lines[p.Line] : value[lineStart..];
        return line[..(line.Length - line.TrimStart().Length)];
    }

    private static string Replace(string text, AmpleDocument doc, Parameter p, string newValue)
    {
        List<string> lines = [.. text.Split('\n')];
        string prefix = lines[p.Line][..Math.Min(p.ValueColumn, lines[p.Line].Length)];
        lines.RemoveRange(p.Line, p.EndLine - p.Line + 1);
        lines.InsertRange(p.Line, (prefix + newValue).Split('\n'));
        _ = doc;
        return string.Join('\n', lines);
    }

    /// <summary>Rewrites one hand-written <c>editable { }</c> piece of a clip's pipeline (the roll writes here), keeping everything else of the song as it is.</summary>
    /// <param name="songText">The text of the song.</param>
    /// <param name="spec">The language spec.</param>
    /// <param name="block">The midi-clip block.</param>
    /// <param name="index">Which editable piece (0 is the first one in the text).</param>
    /// <param name="notes">The new notes of the piece, as compact notes text.</param>
    /// <returns>The new song text.</returns>
    public static string SetEditable(string songText, SpecData spec, string block, int index, string notes)
    {
        bool crlf = songText.Contains("\r\n", StringComparison.Ordinal);
        string text = songText.Replace("\r\n", "\n", StringComparison.Ordinal);
        (_, Parameter p, _) = Find(text, spec, block, null, NotePipeline.MaxEvents, null, out AmpleDocument doc);
        IReadOnlyList<(int Start, int End, string Entries)> pieces = NotePipeline.Editables(p.Value);
        if (index < 0 || index >= pieces.Count) throw new NoteBakeException("AMP5702", $"The clip \"{block}\" has no editable piece number {index + 1}.");
        List<PipeIssue> issues = [];
        _ = NotePipeline.ReadNotes(notes, issues);
        if (issues.Count > 0) throw new NoteBakeException("AMP2012", issues[0].Message + ".");
        (int start, int end, _) = pieces[index];
        string indent = Indent(doc, p, start), pad = indent + "  ";
        string body = "editable {\n" + NotesLines(pad, notes) + "\n" + indent + "}";
        string result = Replace(text, doc, p, p.Value[..start] + body + p.Value[end..]);
        return crlf ? result.Replace("\n", "\r\n", StringComparison.Ordinal) : result;
    }

    private static string NotesLines(string pad, string notes)
    {
        StringBuilder sb = new();
        StringBuilder line = new(pad + "notes");
        foreach (string entry in notes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length + entry.Length + 1 > LineWidth && line.Length > pad.Length + 6) { sb.Append(line).Append('\n'); line = new StringBuilder(pad + "      "); }
            line.Append(' ').Append(entry);
        }

        sb.Append(line);
        return sb.ToString();
    }

    private static string StageText(string name, bool strict, string inputHash, string notesHash, string span, string? notes, string? file, string indent)
    {
        StringBuilder sb = new();
        sb.Append("bake ").Append(name).Append(" {\n");
        if (strict) sb.Append(indent).Append("  strict\n");
        sb.Append(indent).Append("  input-hash \"").Append(inputHash).Append("\"\n");
        sb.Append(indent).Append("  notes-hash \"").Append(notesHash).Append("\"\n");
        sb.Append(indent).Append("  span ").Append(span).Append('\n');
        if (file is not null) sb.Append(indent).Append("  file \"").Append(file).Append("\"\n");
        else
        {
            string pad = indent + "  ";
            StringBuilder line = new(pad + "notes");
            foreach (string entry in (notes ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length + entry.Length + 1 > LineWidth && line.Length > pad.Length + 6) { sb.Append(line).Append('\n'); line = new StringBuilder(pad + "      "); }
                line.Append(' ').Append(entry);
            }

            sb.Append(line).Append('\n');
        }

        sb.Append(indent).Append('}');
        return sb.ToString();
    }
}
