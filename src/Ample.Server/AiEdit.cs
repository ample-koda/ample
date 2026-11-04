using System.Text.Json.Nodes;
using Ample.Core.Abstractions;
using Ample.Language;

namespace Ample.Server;

/// <summary>
/// Structured edits of an AMPLE text file for the AI assistant (the tool <c>ample_edit</c>), so that it never has to patch a song with a regular expression in a shell command.
/// The edits of one call are applied in order to the text in memory and are all or nothing: the first one that does not fit stops the call and nothing is written.
/// </summary>
internal static class AiEdit
{
    /// <summary>What an edit call did: the new text and one line per edit.</summary>
    public sealed record Result(string Text, List<string> Summary);

    private static readonly string[] Ops = ["replace", "insert", "delete_lines", "set_param", "note_transform", "note_add", "note_remove", "add_block", "remove_block", "set_flag", "sequence_row", "sequence_channel", "connect", "disconnect", "create"];

    public static Result Apply(string original, JsonArray edits, SpecData spec)
    {
        bool crlf = original.Contains("\r\n", StringComparison.Ordinal);
        bool cr = !original.Contains('\n', StringComparison.Ordinal) && original.Contains('\r', StringComparison.Ordinal);
        string text = original.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (cr)
        {
            text = text.Replace('\r', '\n');
        }
        List<string> summary = [];
        int number = 0;
        foreach (JsonNode? node in edits)
        {
            number++;
            JsonObject e = node as JsonObject ?? throw Fail(number, "an edit must be an object like {\"op\":\"replace\",...}");
            string op = Str(e, "op");
            if (!Ops.Contains(op))
            {
                throw Fail(number, $"unknown op '{op}'. The ops are: {string.Join(", ", Ops)}.");
            }

            try
            {
                text = Run(op, text, e, spec, summary);
            }
            catch (AmpleException ex)
            {
                // a model that escaped its text twice sends "-\u003E" for "->" and "\n" for a line break: when the text did not fit and looks like that, it is read once more as the characters it stands for
                if (Unescaped(e) is not { } again)
                {
                    throw Fail(number, ex.Message);
                }

                try
                {
                    List<string> second = [];
                    text = Run(op, text, again, spec, second);
                    summary.AddRange(second);
                    summary.Add("note: the text had escape sequences (\\u003E, \\n); they were read as the characters they stand for");
                }
                catch (AmpleException)
                {
                    throw Fail(number, ex.Message);
                }
            }
        }

        return new Result(crlf ? text.Replace("\n", "\r\n", StringComparison.Ordinal) : cr ? text.Replace('\n', '\r') : text, summary);
    }

    private static string Run(string op, string text, JsonObject e, SpecData spec, List<string> summary) => op switch
    {
        "replace" => Replace(text, e, summary),
        "insert" => Insert(text, e, summary),
        "delete_lines" => DeleteLines(text, e, summary),
        "set_param" => SetParam(text, e, spec, summary),
        "note_transform" => TransformNotes(text, e, spec, summary),
        "note_add" => AddNote(text, e, spec, summary),
        "note_remove" => RemoveNote(text, e, spec, summary),
        "add_block" => AddBlock(text, e, spec, summary),
        "remove_block" => RemoveBlock(text, e, spec, summary),
        "set_flag" => SetFlag(text, e, spec, summary),
        "sequence_row" => SequenceRow(text, e, spec, summary),
        "sequence_channel" => SequenceChannel(text, e, spec, summary),
        "connect" => Connect(text, e, spec, summary, true),
        "disconnect" => Connect(text, e, spec, summary, false),
        _ => Create(text, e, summary),
    };

    private static readonly string[] TextKeys = ["old", "new", "after", "before", "text", "value", "note"];

    private static readonly System.Text.RegularExpressions.Regex EscapeArtifact = new(@"\\u[0-9a-fA-F]{4}|\\[nrt""]", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>The same edit with the escape sequences of its text read as the characters they stand for (\u003E is >, backslash-n is a line break), or null when nothing in it looks escaped.</summary>
    private static JsonObject? Unescaped(JsonObject e)
    {
        if (!TextKeys.Any(k => e[k] is JsonValue v && v.TryGetValue(out string? s) && s is not null && EscapeArtifact.IsMatch(s)))
        {
            return null;
        }

        JsonObject copy = (JsonObject)e.DeepClone();
        foreach (string key in TextKeys)
        {
            if (copy[key] is JsonValue v && v.TryGetValue(out string? s) && s is not null)
            {
                copy[key] = Unescape(s);
            }
        }

        return copy;
    }

    internal static string Unescape(string s) =>
        EscapeArtifact.Replace(s, m => m.Value[1] switch
        {
            'u' => ((char)Convert.ToInt32(m.Value[2..], 16)).ToString(),
            'n' => "\n",
            'r' => string.Empty,
            't' => "\t",
            _ => "\"",
        });

    private static AmpleException Fail(int number, string message) => new("AMP5460", $"Edit {number} did not fit, so nothing was changed: {message}");

    private static string Str(JsonObject o, string key) => o[key] is JsonValue v ? v.ToString() : string.Empty;

    /// <summary>Text for a block or a new file. A model that wrote the line breaks as the two characters backslash and n (one line, no real break) gets real line breaks, so a block does not land in the song as one line with a literal \n in it.</summary>
    internal static string LineBreaks(string s)
    {
        string t = Norm(s);
        return !t.Contains('\n', StringComparison.Ordinal) && t.Contains("\\n", StringComparison.Ordinal)
            ? t.Replace("\\r\\n", "\n", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\t", "\t", StringComparison.Ordinal)
            : t;
    }

    private static string Norm(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>The lines of the file that are most like the first line of the text that was not found (with their numbers), so the model sees what differs.</summary>
    private static string ClosestLines(string text, string old)
    {
        string first = old.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? string.Empty;
        if (first.Length == 0 || first.Length > 300)
        {
            return string.Empty;
        }

        string[] lines = text.Split('\n');
        List<(int Number, string Line, double Score)> best = [.. lines.Select((l, i) => (Number: i + 1, Line: l, Score: Similarity(l.Trim(), first))).Where(x => x.Score >= 0.6).OrderByDescending(x => x.Score).Take(3)];
        if (best.Count == 0)
        {
            return $" No line of the file looks like \"{(first.Length > 60 ? first[..60] + "..." : first)}\".";
        }

        return " The closest lines are: " + string.Join(" | ", best.Select(b => $"line {b.Number}: \"{(b.Line.Length > 100 ? b.Line[..100] + "..." : b.Line)}\"")) + ".";
    }

    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0 || Math.Abs(a.Length - b.Length) > Math.Max(a.Length, b.Length) / 2 + 8 || a.Length > 300)
        {
            return 0;
        }

        int[] previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int[] current = new int[b.Length + 1];
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            previous = current;
        }

        return 1.0 - ((double)previous[b.Length] / Math.Max(a.Length, b.Length));
    }

    private static string Replace(string text, JsonObject e, List<string> summary)
    {
        string old = Norm(Str(e, "old")), now = Norm(Str(e, "new"));
        if (old.Length == 0)
        {
            throw new AmpleException("AMP5460", "'old' is the exact text to replace and must not be empty.");
        }

        if (string.Equals(old, now, StringComparison.Ordinal))
        {
            throw new AmpleException("AMP5460", "'old' and 'new' are the same text, so this edit changes nothing. Give the text as it should become (to disable a block use op set_flag with flag disabled).");
        }

        int count = 0;
        for (int i = text.IndexOf(old, StringComparison.Ordinal); i >= 0; i = text.IndexOf(old, i + old.Length, StringComparison.Ordinal))
        {
            count++;
        }

        if (count == 0)
        {
            throw new AmpleException("AMP5460", "the text to replace was not found (it must match exactly, including spaces and line breaks)." + ClosestLines(text, old) + " Read the file again with ample_file.");
        }

        bool all = e["all"] is JsonValue a && a.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
        if (e["occurrence"] is JsonValue o && int.TryParse(o.ToString(), out int nth) && nth >= 1 && !all)
        {
            if (nth > count)
            {
                throw new AmpleException("AMP5460", $"'occurrence' is {nth} but the text was found only {count} time(s).");
            }

            int at = -1;
            for (int n = 0; n < nth; n++)
            {
                at = text.IndexOf(old, at + 1, StringComparison.Ordinal);
            }

            summary.Add($"replace: occurrence {nth} of {count}");
            return string.Concat(text.AsSpan(0, at), now, text.AsSpan(at + old.Length));
        }

        if (count > 1 && !all)
        {
            List<int> places = [];
            for (int i = text.IndexOf(old, StringComparison.Ordinal); i >= 0 && places.Count < 6; i = text.IndexOf(old, i + old.Length, StringComparison.Ordinal))
            {
                places.Add(text.AsSpan(0, i).Count('\n') + 1);
            }

            throw new AmpleException("AMP5460", $"the text to replace was found {count} times (at lines {string.Join(", ", places)}); add more of the surrounding text to make it unique, give \"occurrence\": 1..{count} for one of them, or set \"all\": true.");
        }

        summary.Add($"replace: {(all ? count : 1)} place(s)");
        if (all)
        {
            return text.Replace(old, now, StringComparison.Ordinal);
        }

        int index = text.IndexOf(old, StringComparison.Ordinal);
        return string.Concat(text.AsSpan(0, index), now, text.AsSpan(index + old.Length));
    }

    private static string Insert(string text, JsonObject e, List<string> summary)
    {
        List<string> lines = [.. text.Split('\n')];
        string add = LineBreaks(Str(e, "text")).TrimEnd('\n');
        if (add.Length == 0)
        {
            throw new AmpleException("AMP5460", "'text' is the text to insert.");
        }

        int at;
        if (e["at_line"] is JsonValue al && int.TryParse(al.ToString(), out int line))
        {
            if (line < 1 || line > lines.Count + 1)
            {
                throw new AmpleException("AMP5460", $"at_line {line} is outside the file (1 to {lines.Count + 1}).");
            }

            at = line - 1;
        }
        else
        {
            bool after = e["after"] is not null;
            string anchor = Norm(Str(e, after ? "after" : "before"));
            if (anchor.Trim().Length == 0)
            {
                throw new AmpleException("AMP5460", "give 'after', 'before' (text of the line to insert next to) or 'at_line'.");
            }

            if (anchor.Trim('\n').Contains('\n', StringComparison.Ordinal))
            {
                // several lines as the anchor: found as a whole, and it has to be there once
                string block = anchor.Trim('\n');
                int first = text.IndexOf(block, StringComparison.Ordinal);
                if (first < 0 || text.IndexOf(block, first + 1, StringComparison.Ordinal) >= 0)
                {
                    throw new AmpleException("AMP5460", first < 0 ? "the lines of the anchor were not found together (they must match exactly)." + ClosestLines(text, block) : "the lines of the anchor are in the file more than once; use at_line or a longer anchor.");
                }

                int startLine = text.AsSpan(0, first).Count('\n');
                int endLine = startLine + block.Count(c => c == '\n');
                at = after ? endLine + 1 : startLine;
                string multiple = LineBreaks(Str(e, "text")).TrimEnd('\n');
                if (multiple.Length == 0)
                {
                    throw new AmpleException("AMP5460", "'text' is the text to insert.");
                }

                lines.InsertRange(at, multiple.Split('\n'));
                summary.Add($"insert: {multiple.Split('\n').Length} line(s) at line {at + 1}");
                return string.Join('\n', lines);
            }

            int[] hits = [.. lines.Select((l, i) => (l, i)).Where(x => x.l.Contains(anchor, StringComparison.Ordinal)).Select(x => x.i)];
            if (hits.Length != 1)
            {
                throw new AmpleException("AMP5460", hits.Length == 0 ? $"no line contains '{anchor}' (the anchor is the text of one line, or several whole lines)." + ClosestLines(string.Join('\n', lines), anchor) : $"{hits.Length} lines contain '{anchor}' (lines {string.Join(", ", hits.Take(6).Select(i => i + 1))}); use a more specific anchor or at_line.");
            }

            at = after ? hits[0] + 1 : hits[0];
        }

        lines.InsertRange(at, add.Split('\n'));
        summary.Add($"insert: {add.Split('\n').Length} line(s) at line {at + 1}");
        return string.Join('\n', lines);
    }

    private static string DeleteLines(string text, JsonObject e, List<string> summary)
    {
        List<string> lines = [.. text.Split('\n')];
        int to = 0;
        if (!int.TryParse(Str(e, "from"), out int from))
        {
            throw new AmpleException("AMP5460", "delete_lines needs 'from': the number of the first line to delete (1-based); 'to' is the last line and defaults to 'from'.");
        }

        if (!int.TryParse(Str(e, "to"), out to))
        {
            to = from;
        }

        if (from < 1 || to < from || to > lines.Count)
        {
            throw new AmpleException("AMP5460", $"delete_lines {from}-{to} is outside the file: it has {lines.Count} line(s) (1-based). Read the file again with ample_file to see the numbers.");
        }

        lines.RemoveRange(from - 1, to - from + 1);
        summary.Add($"delete_lines: {to - from + 1} line(s) from line {from}");
        return string.Join('\n', lines);
    }

    private static BlockNode FindBlock(string text, string name, SpecData spec)
    {
        AmpleDocument doc = Parser.Parse(text, spec);
        return doc.Blocks.FirstOrDefault(b => !b.Skipped && string.Equals(b.Name, name, StringComparison.Ordinal))
            ?? throw new AmpleException("AMP5460", $"there is no block called '{name}'. Blocks: {string.Join(", ", doc.Blocks.Where(b => !b.Skipped).Select(b => b.Name).Take(40))}.");
    }

    private static string SetParam(string text, JsonObject e, SpecData spec, List<string> summary)
    {
        string block = Str(e, "block"), name = Str(e, "name"), value = Str(e, "value");
        if (name.Length == 0)
        {
            throw new AmpleException("AMP5460", "set_param needs 'block', 'name' and 'value'.");
        }

        BlockNode b = FindBlock(text, block, spec);
        List<string> lines = [.. text.Split('\n')];
        int close = b.CloseLine < 0 ? b.Line : b.CloseLine;
        for (int i = b.Line + 1; i < close && i < lines.Count; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("from ", StringComparison.Ordinal) || trimmed.StartsWith('#'))
            {
                continue;
            }

            string[] parts = trimmed.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && string.Equals(parts[0], name, StringComparison.Ordinal))
            {
                string indent = new([.. lines[i].TakeWhile(char.IsWhiteSpace)]);
                string was = parts.Length > 1 ? parts[1] : string.Empty;
                lines[i] = value.Length == 0 ? $"{indent}{name}" : $"{indent}{name} {value}";
                summary.Add($"set_param: {block}.{name}: '{Cut(was)}' -> '{Cut(value)}'");
                return string.Join('\n', lines);
            }
        }

        string pad = close > b.Line + 1 ? new string([.. lines[b.Line + 1].TakeWhile(char.IsWhiteSpace)]) : "  ";
        if (b.CloseLine < 0)
        {
            throw new AmpleException("AMP5460", $"the block '{block}' has no body in braces to add the parameter to.");
        }

        lines.Insert(close, value.Length == 0 ? $"{pad}{name}" : $"{pad}{name} {value}");
        summary.Add($"set_param: {block}.{name} added: '{Cut(value)}'");
        return string.Join('\n', lines);
    }

    private static string AddBlock(string text, JsonObject e, SpecData spec, List<string> summary)
    {
        string block = LineBreaks(Str(e, "text")).Trim('\n');
        if (block.Length == 0)
        {
            throw new AmpleException("AMP5460", "'text' is the whole block, for example \"reverb room {\\n  from drums.audio -> audio\\n}\".");
        }

        List<string> lines = [.. text.TrimEnd('\n').Split('\n')];
        string after = Str(e, "after_block");
        int at = lines.Count;
        if (after.Length > 0)
        {
            BlockNode b = FindBlock(text, after, spec);
            at = (b.CloseLine < 0 ? b.Line : b.CloseLine) + 1;
        }

        lines.InsertRange(at, new[] { string.Empty }.Concat(block.Split('\n')));
        summary.Add($"add_block: {block.Split('\n')[0].Trim()}{(after.Length > 0 ? " after " + after : string.Empty)}");
        return string.Join('\n', lines) + "\n";
    }

    private static string TransformNotes(string text, JsonObject e, SpecData spec, List<string> summary)
    {
        string name = Str(e, "block"), action = Str(e, "action");
        BlockNode block = FindBlock(text, name, spec);
        if (block.Type != "midi-clip") throw new AmpleException("AMP5460", $"'{name}' is not a midi-clip block.");
        Parameter? parameter = block.Parameters.FirstOrDefault(p => p.Name == "notes");
        if (parameter is null)
            throw new AmpleException("AMP5460", $"'{name}' needs valid compact structured notes; expanded notes bodies are not supported by note_transform yet.");
        int ppq = int.TryParse(block.Parameters.FirstOrDefault(p => p.Name == "ppq")?.Value, out int parsedPpq) ? parsedPpq : StructuredMidiNotes.MicrobeatsPerBeat;
        if (!StructuredMidiNotes.TryParse(parameter.Value, ppq, out IReadOnlyList<StructuredMidiNote> parsed))
            throw new AmpleException("AMP5460", $"'{name}' needs valid compact structured notes; expanded notes bodies are not supported by note_transform yet.");
        int amount = int.TryParse(Str(e, "amount"), out int parsedAmount) ? parsedAmount : 0;
        int grid = double.TryParse(Str(e, "grid"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double beats) ? (int)Math.Round(beats * ppq) : 0;
        if (action == "quantize" ? grid < 1 || grid > 4 * ppq : amount == 0)
            throw new AmpleException("AMP5460", "note_transform needs a nonzero integer 'amount' for transpose (semitones) or velocity (delta), or a positive 'grid' in beats for quantize.");
        int? bar = int.TryParse(Str(e, "bar"), out int selectedBar) ? selectedBar : null;
        int? pitch = int.TryParse(Str(e, "pitch"), out int selectedPitch) ? selectedPitch : null;
        if (!parsed.Any(n => (!bar.HasValue || n.Bar == bar) && (!pitch.HasValue || n.Pitch == pitch)))
            throw new AmpleException("AMP5460", "the bar/pitch filter did not match any notes in this clip.");
        List<string> entries = parameter.Value.Trim().Trim('"').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        IReadOnlyList<StructuredMidiNote> updated = parsed.Select(n =>
        {
            if ((bar.HasValue && n.Bar != bar) || (pitch.HasValue && n.Pitch != pitch)) return n;
            return action switch
            {
                "transpose" => n with { Pitch = Math.Clamp(n.Pitch + amount, 0, 127) },
                "velocity" => n with { Velocity = Math.Clamp(n.Velocity + amount, 1, 127) },
                "quantize" => n with { BeatTicks = Math.Clamp(((n.BeatTicks + grid / 2L) / grid) * grid, 0, 4L * n.TicksPerQuarter - n.LengthTicks) },
                _ => throw new AmpleException("AMP5460", "action must be transpose, velocity or quantize.")
            };
        }).ToArray();
        entries = updated.Select(n =>
        {
            string beat = ppq == StructuredMidiNotes.MicrobeatsPerBeat ? DecimalBeat(n.BeatTicks) : n.BeatTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string length = ppq == StructuredMidiNotes.MicrobeatsPerBeat ? DecimalBeat(n.LengthTicks) : n.LengthTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string entry = $"{n.Bar}:{beat}:{length}:{n.Pitch}:{n.Velocity}";
            return n.Channel < 0 ? entry : $"{entry}:{n.Channel}:{n.Bank}:{n.Program}{(n.TieId < 0 ? string.Empty : $":{n.TieId}")}";
        }).ToList();
        List<string> lines = [.. text.Split('\n')];
        string indent = new([.. lines[parameter.Line].TakeWhile(char.IsWhiteSpace)]);
        lines[parameter.Line] = $"{indent}notes {string.Join(' ', entries)}";
        summary.Add($"note_transform: {action} {(action == "quantize" ? $"{beats} beat grid" : amount)} on {updated.Count(n => (!bar.HasValue || n.Bar == bar) && (!pitch.HasValue || n.Pitch == pitch))} note(s) in {name}");
        return string.Join('\n', lines);
    }

    private static string DecimalBeat(long ticks) => (ticks / 1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture) + (ticks % 1_000_000 == 0 ? string.Empty : "." + (ticks % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture).TrimEnd('0'));

    private static (List<string> Lines, Parameter Parameter, int Ppq, List<string> Entries) CompactNotes(string text, JsonObject e, SpecData spec)
    {
        string name = Str(e, "block");
        BlockNode block = FindBlock(text, name, spec);
        Parameter? parameter = block.Type == "midi-clip" ? block.Parameters.FirstOrDefault(p => p.Name == "notes") : null;
        int ppq = int.TryParse(block.Parameters.FirstOrDefault(p => p.Name == "ppq")?.Value, out int value) ? value : StructuredMidiNotes.MicrobeatsPerBeat;
        if (parameter is null || !StructuredMidiNotes.TryParse(parameter.Value, ppq, out _))
            throw new AmpleException("AMP5460", $"'{name}' needs valid compact structured notes; expanded notes bodies are not supported by note edits yet.");
        return ([.. text.Split('\n')], parameter, ppq, parameter.Value.Trim().Trim('"').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList());
    }

    private static string WriteNotes(List<string> lines, Parameter parameter, List<string> entries) =>
        string.Join('\n', lines.Select((line, index) => index == parameter.Line ? $"{new string([.. line.TakeWhile(char.IsWhiteSpace)])}notes {string.Join(' ', entries)}" : line));

    private static string AddNote(string text, JsonObject e, SpecData spec, List<string> summary)
    {
        (List<string> lines, Parameter parameter, int ppq, List<string> entries) = CompactNotes(text, e, spec);
        string note = Str(e, "note").Trim().Trim('"');
        if (!StructuredMidiNotes.TryParse(note, ppq, out IReadOnlyList<StructuredMidiNote> parsed) || parsed.Count != 1)
            throw new AmpleException("AMP5460", "note_add needs one valid note entry in bar:beat:length:pitch:velocity form (optional MIDI metadata may follow).");
        entries.Add(note);
        summary.Add($"note_add: added one note to {Str(e, "block")}");
        return WriteNotes(lines, parameter, entries);
    }

    private static string RemoveNote(string text, JsonObject e, SpecData spec, List<string> summary)
    {
        (List<string> lines, Parameter parameter, int ppq, List<string> entries) = CompactNotes(text, e, spec);
        if (!int.TryParse(Str(e, "bar"), out int bar) && !int.TryParse(Str(e, "pitch"), out _))
            throw new AmpleException("AMP5460", "note_remove needs at least a 'bar' or 'pitch' selector; optionally narrow it with 'beat' (in beats) and use 'all': true to remove every match.");
        int? pitch = int.TryParse(Str(e, "pitch"), out int p) ? p : null;
        int? beat = double.TryParse(Str(e, "beat"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double b) ? (int)Math.Round(b * ppq) : null;
        IReadOnlyList<StructuredMidiNote> parsed = StructuredMidiNotes.TryParse(string.Join(' ', entries), ppq, out IReadOnlyList<StructuredMidiNote> notes) ? notes : [];
        int[] matches = [.. parsed.Select((n, i) => (n, i)).Where(x => (!int.TryParse(Str(e, "bar"), out int selectedBar) || x.n.Bar == selectedBar) && (!pitch.HasValue || x.n.Pitch == pitch) && (!beat.HasValue || x.n.BeatTicks == beat)).Select(x => x.i)];
        bool all = e["all"] is JsonValue allValue && allValue.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
        if (matches.Length == 0 || matches.Length > 1 && !all)
            throw new AmpleException("AMP5460", matches.Length == 0 ? "no note matched those selectors." : $"{matches.Length} notes matched; add a more specific selector or set 'all': true.");
        foreach (int index in matches.OrderDescending()) entries.RemoveAt(index);
        summary.Add($"note_remove: removed {matches.Length} note(s) from {Str(e, "block")}");
        return WriteNotes(lines, parameter, entries);
    }

    /// <summary>Puts a header flag on a block or takes it off: <c>disabled</c> (the block stays visible but returns no take), <c>lock</c>, <c>bake</c> or <c>refresh</c>.</summary>
    /// <summary>The lines of the sequence kept in a block: the index of the "sequence {" line and of its closing brace (both inside the block), or an error.</summary>
    private static (int Open, int Close) SequenceLines(List<string> lines, BlockNode b, string block)
    {
        int end = b.CloseLine < 0 ? b.Line : b.CloseLine;
        for (int i = b.Line + 1; i < end && i < lines.Count; i++)
        {
            if (lines[i].Trim() == "sequence {")
            {
                int indent = lines[i].Length - lines[i].TrimStart().Length;
                for (int j = i + 1; j < end && j < lines.Count; j++)
                {
                    if (lines[j].Trim() == "}" && lines[j].Length - lines[j].TrimStart().Length == indent)
                    {
                        return (i, j);
                    }
                }
            }
        }

        throw new AmpleException("AMP5460", $"the block '{block}' keeps no sequence in the song (it has no 'sequence {{ ... }}' part).");
    }

    /// <summary>Sets one row of one pattern of a sequence kept in the song, and nothing else: {"op":"sequence_row","block":"beat-bed","pattern":"groove","channel":"bell","text":"1:0:1:80:90"} (empty text removes the row). The channel must exist in the sequence.</summary>
    private static string SequenceRow(string text, JsonObject e, SpecData spec, List<string> summary)
    {
        string block = Str(e, "block"), pattern = Str(e, "pattern"), channel = Str(e, "channel"), row = Norm(Str(e, "text")).Trim();
        if (block.Length == 0 || pattern.Length == 0 || channel.Length == 0)
        {
            throw new AmpleException("AMP5460", "sequence_row needs 'block', 'pattern', 'channel' and 'text'.");
        }

        List<string> lines = [.. text.Split('\n')];
        (int open, int close) = SequenceLines(lines, FindBlock(text, block, spec), block);
        if (!lines.Skip(open + 1).Take(close - open - 1).Any(l => l.Trim().StartsWith("channel " + channel + " ", StringComparison.Ordinal) || l.Trim() == "channel " + channel))
        {
            throw new AmpleException("AMP5460", $"the sequence of '{block}' has no channel '{channel}'; add it first with sequence_channel.");
        }

        int p = -1;
        for (int i = open + 1; i < close; i++)
        {
            string[] words = lines[i].Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2 && words[0] == "pattern" && words[1].Split(' ')[0] == pattern)
            {
                p = i;
                break;
            }
        }

        if (p < 0)
        {
            throw new AmpleException("AMP5460", $"the sequence of '{block}' has no pattern '{pattern}'.");
        }

        int patternIndent = lines[p].Length - lines[p].TrimStart().Length;
        int last = p;
        for (int i = p + 1; i < close; i++)
        {
            if (lines[i].Trim().Length == 0)
            {
                continue;
            }

            if (lines[i].Length - lines[i].TrimStart().Length <= patternIndent)
            {
                break;
            }

            last = i;
            string[] words = lines[i].Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (words[0] == channel)
            {
                string pad = new(' ', lines[i].Length - lines[i].TrimStart().Length);
                string was = words.Length > 1 ? words[1] : string.Empty;
                if (row.Length == 0)
                {
                    lines.RemoveAt(i);
                }
                else
                {
                    lines[i] = $"{pad}{channel} {row}";
                }

                summary.Add($"sequence_row: {block} {pattern} {channel}: '{Cut(was)}' -> '{Cut(row)}'");
                return string.Join('\n', lines);
            }
        }

        if (row.Length == 0)
        {
            summary.Add($"sequence_row: {block} {pattern} {channel}: already empty");
            return text;
        }

        int rowIndent = last > p ? lines[last].Length - lines[last].TrimStart().Length : patternIndent + 2;
        lines.Insert(last + 1, $"{new string(' ', rowIndent)}{channel} {row}");
        summary.Add($"sequence_row: {block} {pattern} {channel}: new row '{Cut(row)}'");
        return string.Join('\n', lines);
    }

    /// <summary>Adds one channel to a sequence kept in the song, after the last channel: {"op":"sequence_channel","block":"beat-bed","channel":"bell","text":"gm program=10 vol=-24"} (the text is the rest of the channel line: its type and settings).</summary>
    private static string SequenceChannel(string text, JsonObject e, SpecData spec, List<string> summary)
    {
        string block = Str(e, "block"), channel = Str(e, "channel"), rest = Norm(Str(e, "text")).Trim();
        if (block.Length == 0 || channel.Length == 0 || rest.Length == 0)
        {
            throw new AmpleException("AMP5460", "sequence_channel needs 'block', 'channel' and 'text' (the type and settings, like \"gm program=10 vol=-24\").");
        }

        List<string> lines = [.. text.Split('\n')];
        (int open, int close) = SequenceLines(lines, FindBlock(text, block, spec), block);
        int last = -1;
        for (int i = open + 1; i < close; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed == "channel " + channel || trimmed.StartsWith("channel " + channel + " ", StringComparison.Ordinal))
            {
                throw new AmpleException("AMP5460", $"the sequence of '{block}' already has a channel '{channel}'.");
            }

            if (trimmed.StartsWith("channel ", StringComparison.Ordinal))
            {
                last = i;
            }
        }

        if (last < 0)
        {
            throw new AmpleException("AMP5460", $"the sequence of '{block}' has no channel line to put the new one after.");
        }

        string pad = new(' ', lines[last].Length - lines[last].TrimStart().Length);
        lines.Insert(last + 1, $"{pad}channel {channel} {rest}");
        summary.Add($"sequence_channel: {block}: channel {channel} {Cut(rest)}");
        return string.Join('\n', lines);
    }

    private static string SetFlag(string text, JsonObject e, SpecData spec, List<string> summary)
    {
        string name = Str(e, "block"), flag = Str(e, "flag");
        if (flag is not ("disabled" or "lock" or "bake" or "refresh"))
        {
            throw new AmpleException("AMP5460", "'flag' is one of: disabled, lock, bake, refresh (to disable a block use flag \"disabled\"; that is not the 'enabled' parameter of effects).");
        }

        bool on = e["on"] is not JsonValue v || !v.TryGetValue(out bool value) || value;
        BlockNode block = FindBlock(text, name, spec);
        AmpleDocument doc = Parser.Parse(text, spec);
        EditResult result = on ? Edits.AddFlag(doc, name, flag) : Edits.RemoveFlag(doc, name, flag);
        if (result.Error is not null)
        {
            throw new AmpleException("AMP5460", result.Error);
        }

        summary.Add($"set_flag: {name} {(on ? "+" : "-")}{flag}");
        if (on && flag == "disabled")
        {
            // done as asked; what it also does is said in the answer, so the user is told
            string[] needs = [.. doc.Blocks.Where(x => !x.Skipped && x.Froms.Any(f => f.Block == name)).Select(x => x.Name).Take(12)];
            List<string> notes = [];
            if (block.Type is "sequencer" or "mixer" or "master")
            {
                notes.Add($"{name} is a {block.Type}: with it disabled the song may make little or no sound");
            }

            if (needs.Length > 0)
            {
                notes.Add($"blocks that read from {name} become implicitly disabled too (warning AMP5253): {string.Join(", ", needs)}");
            }

            if (notes.Count > 0)
            {
                summary.Add($"WARNING: {string.Join("; ", notes)}.");
            }
        }

        return Edits.Apply(text, result.Edits);
    }

    private static string RemoveBlock(string text, JsonObject e, SpecData spec, List<string> summary)
    {
        string name = Str(e, "block");
        BlockNode b = FindBlock(text, name, spec);
        List<string> lines = [.. text.Split('\n')];
        int from = b.Line, to = b.CloseLine < 0 ? b.Line : b.CloseLine;
        lines.RemoveRange(from, to - from + 1);
        if (from > 0 && from < lines.Count && lines[from - 1].Trim().Length == 0 && lines[from].Trim().Length == 0)
        {
            lines.RemoveAt(from);
        }

        string rest = string.Join('\n', lines);
        string[] users = [.. Parser.Parse(rest, spec).Blocks.Where(x => x.Froms.Any(f => f.Block == name)).Select(x => x.Name)];
        summary.Add($"remove_block: {name}" + (users.Length > 0 ? $" (still referenced by: {string.Join(", ", users)}; connect or remove those)" : string.Empty));
        return rest;
    }

    private static string Connect(string text, JsonObject e, SpecData spec, List<string> summary, bool add)
    {
        string to = Str(e, "to_block"), from = Str(e, "from_block"), output = Str(e, "from_output"), input = Str(e, "to_input");
        if (to.Length == 0 || from.Length == 0 || output.Length == 0 || input.Length == 0)
        {
            throw new AmpleException("AMP5460", "needs 'from_block', 'from_output', 'to_block' and 'to_input' (the line is 'from <from_block>.<from_output> -> <to_input>' inside the block 'to_block').");
        }

        BlockNode b = FindBlock(text, to, spec);
        List<string> lines = [.. text.Split('\n')];
        string wanted = $"from {from}.{output} -> {input}";
        int close = b.CloseLine < 0 ? b.Line : b.CloseLine;
        int found = -1;
        for (int i = b.Line + 1; i < close && i < lines.Count; i++)
        {
            string t = lines[i].Trim();
            if (t == wanted || t.StartsWith(wanted + " ", StringComparison.Ordinal))
            {
                found = i;
            }
        }

        if (add)
        {
            if (b.CloseLine < 0)
            {
                throw new AmpleException("AMP5460", $"the block '{to}' has no body in braces.");
            }

            if (found >= 0)
            {
                summary.Add($"connect: {wanted} was already there in {to}");
                return text;
            }

            string pad = close > b.Line + 1 ? new string([.. lines[b.Line + 1].TakeWhile(char.IsWhiteSpace)]) : "  ";
            lines.Insert(close, pad + wanted);
            summary.Add($"connect: {wanted} in {to}");
        }
        else
        {
            if (found < 0)
            {
                throw new AmpleException("AMP5460", $"'{wanted}' is not in the block '{to}'.");
            }

            lines.RemoveAt(found);
            summary.Add($"disconnect: {wanted} from {to}");
        }

        return string.Join('\n', lines);
    }

    private static string Create(string text, JsonObject e, List<string> summary)
    {
        if (text.Length > 0)
        {
            throw new AmpleException("AMP5460", "the file already exists; 'create' only makes a new file. Use the other ops to change it.");
        }

        string body = LineBreaks(Str(e, "text"));
        summary.Add($"create: {body.Split('\n').Length} line(s)");
        return body.EndsWith('\n') ? body : body + "\n";
    }

    private static string Cut(string s) => s.Length > 60 ? s[..60] + "…" : s;
}
