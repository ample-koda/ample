using System.Text;
using System.Text.Json;

namespace Ample.Language;

/// <summary>A text change: replace the range (1-based, end exclusive) with new text.</summary>
/// <param name="StartLine">First line.</param>
/// <param name="StartCol">First column (UTF-16 index + 1).</param>
/// <param name="EndLine">Last line.</param>
/// <param name="EndCol">Exclusive end column.</param>
/// <param name="NewText">Replacement text.</param>
public sealed record TextEdit(int StartLine, int StartCol, int EndLine, int EndCol, string NewText);

/// <summary>The result of an editing operation: edits to apply, or the reason it was refused.</summary>
/// <param name="Edits">The edits (empty when refused).</param>
/// <param name="Error">Why the operation was refused, or null.</param>
public sealed record EditResult(IReadOnlyList<TextEdit> Edits, string? Error)
{
    /// <summary>Gets a value indicating whether the operation was accepted.</summary>
    public bool Ok => Error is null;

    /// <summary>Gets a short account of what the edit did, for the person (for example the calls that stay dynamic), or null.</summary>
    public string? Summary { get; init; }

    internal static EditResult Refuse(string error) => new([], error);

    internal static EditResult Of(params TextEdit[] edits) => new(edits, null);
}

/// <summary>Editing operations that return text edits, so graph edits and text edits are the same thing.</summary>
public static class Edits
{
    /// <summary>Applies edits to a text. Edits must not overlap; they are applied from the end.</summary>
    public static string Apply(string text, IEnumerable<TextEdit> edits)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int[] starts = new int[lines.Length + 1];
        string joined = string.Join('\n', lines);
        for (int i = 0; i < lines.Length; i++)
        {
            starts[i + 1] = starts[i] + lines[i].Length + 1;
        }

        StringBuilder sb = new(joined);
        foreach (TextEdit e in edits.OrderByDescending(x => x.StartLine).ThenByDescending(x => x.StartCol))
        {
            int from = Offset(starts, lines, e.StartLine, e.StartCol);
            int to = Offset(starts, lines, e.EndLine, e.EndCol);
            sb.Remove(from, to - from).Insert(from, e.NewText);
        }

        return sb.ToString();
    }

    /// <summary>Sets a parameter of a block, adding the line when it does not exist.</summary>
    public static EditResult SetParameter(AmpleDocument doc, string block, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            // an empty value is not valid source ("name" alone breaks the block): empty means the parameter is not set
            return RemoveParameter(doc, block, name);
        }

        BlockNode? b = Find(doc, block);
        if (b is null)
        {
            return EditResult.Refuse($"There is no block named \"{block}\".");
        }

        Parameter? existing = b.Parameters.FirstOrDefault(p => p.Name == name);
        // the events of a rewind are kept as its scratch block
        if (name == "events" && b.Type == "rewind" && RewindBlock.Expand(value, "  ") is { } scratch)
        {
            string text = string.Join('\n', scratch);
            if (existing is not null) return EditResult.Of(new TextEdit(existing.Line + 1, 1, existing.EndLine + 1, doc.Lines[existing.EndLine].Length + 1, text));
            if (b.CloseLine < 0) return EditResult.Refuse($"Block \"{block}\" is not closed.");
            return EditResult.Of(InsertLineAfter(doc, b.Parameters.Count > 0 ? b.Parameters.Max(p => p.EndLine) : b.Line, text));
        }

        if (existing is not null && NotePipeline.IsPropertyExpression(existing.Value) && !NotePipeline.IsPropertyExpression(value))
        {
            return EditResult.Refuse($"AMP2081: {block}.{name} is driven by an expression ({existing.Value.Trim()}). Choose what to do with the value {value}: override (keep the expression, use this value), bake (freeze what the expression gives), literal (drop the expression) or expression (edit the expression): use setPropertyValue with that mode.");
        }

        if (existing is not null)
        {
            string raw = doc.Lines[existing.Line];
            if (raw.Trim() == $"{name} {{") return EditResult.Refuse($"Edit the expanded {name} body in the source or dedicated editor.");
            int start = raw.IndexOf(existing.Value, raw.IndexOf(name, StringComparison.Ordinal) + name.Length, StringComparison.Ordinal);
            if (start < 0) return EditResult.Refuse($"The source value of {name} could not be found.");
            return EditResult.Of(new TextEdit(existing.Line + 1, start + 1, existing.Line + 1, start + 1 + existing.Value.Length, value));
        }

        if (b.CloseLine < 0)
        {
            return EditResult.Refuse($"Block \"{block}\" is not closed.");
        }

        int after = b.Parameters.Count > 0 ? b.Parameters.Max(p => p.EndLine) : b.Line;
        return EditResult.Of(InsertLineAfter(doc, after, $"  {name} {value}"));
    }

    /// <summary>Removes one simple parameter line so clearing a file field does not leave an empty value.</summary>
    public static EditResult RemoveParameter(AmpleDocument doc, string block, string name)
    {
        BlockNode? b = Find(doc, block);
        if (b is null) return EditResult.Refuse($"There is no block named \"{block}\".");
        Parameter? existing = b.Parameters.FirstOrDefault(p => p.Name == name);
        if (existing is null) return EditResult.Of();
        if (existing.EndLine > existing.Line && doc.Lines[existing.Line].Trim() == "scratch {") return EditResult.Of(new TextEdit(existing.Line + 1, 1, existing.EndLine + 1, doc.Lines[existing.EndLine].Length + 1, string.Empty));
        string raw = doc.Lines[existing.Line];
        if (raw.Trim() == $"{name} {{") return EditResult.Refuse($"Remove the expanded {name} body in the source or dedicated editor.");
        return EditResult.Of(new TextEdit(existing.Line + 1, 1, existing.Line + 1, raw.Length + 1, string.Empty));
    }

    /// <summary>The last line that holds a block or a setting: not blank, not a comment, not a layout line and not inside the view block (-1 when there is none).</summary>
    private static int LastContentLine(AmpleDocument doc)
    {
        int last = -1, view = 0;
        for (int i = 0; i < doc.Lines.Count; i++)
        {
            string t = doc.Lines[i].Trim();
            if (view > 0 || (t == "view {" && doc.Blocks.All(b => i < b.Line || i > b.CloseLine)))
            {
                view = Math.Max(0, view + (t.EndsWith('{') ? 1 : 0) - (t == "}" ? 1 : 0));
                continue;
            }

            if (t.Length > 0 && !t.StartsWith("layout ", StringComparison.Ordinal) && t[0] != '#')
            {
                last = i;
            }
        }

        return last;
    }

    /// <summary>Adds a block at the end of the song (before layout lines).</summary>
    /// <param name="doc">The song.</param>
    /// <param name="spec">The language data (the song's own derived data, with its components, is used when it has some).</param>
    /// <param name="type">The block type (a component path for a component).</param>
    /// <param name="name">The block name.</param>
    /// <param name="version">The version to write in the header (for a component: the version the person chose), or null for the version of the template.</param>
    /// <returns>The edit.</returns>
    public static EditResult AddBlock(AmpleDocument doc, SpecData spec, string type, string name, string? version = null)
    {
        if (!(doc.Spec ?? spec).Templates.TryGetValue(type, out TemplateSpec? t))
        {
            return EditResult.Refuse($"Unknown block type \"{type}\".");
        }

        if (Find(doc, name) is not null)
        {
            return EditResult.Refuse($"Two blocks would be called \"{name}\".");
        }

        List<string> text = [$"{type} {Quote(name)}{(string.IsNullOrWhiteSpace(version) ? string.Empty : " pinned@" + version.Trim())} {{"];
        text.AddRange(t.Parameters.Where(p => p.Value.Length > 0).Select(p => $"  {p.Key} {p.Value}"));
        text.Add("}");
        int lastContent = LastContentLine(doc);
        return EditResult.Of(lastContent >= 0 ? InsertLineAfter(doc, lastContent, string.Join('\n', text)) : new TextEdit(1, 1, 1, 1, string.Join('\n', text) + "\n"));
    }

    /// <summary>Adds the text of whole blocks (for example a placed library bundle) after the last block of the song.</summary>
    public static EditResult InsertBlocks(AmpleDocument doc, string text)
    {
        ArgumentNullException.ThrowIfNull(doc);
        string body = text.TrimEnd();
        if (body.Length == 0) return EditResult.Refuse("There is nothing to add.");
        int lastContent = LastContentLine(doc);
        return EditResult.Of(lastContent >= 0 ? InsertLineAfter(doc, lastContent, "\n" + body) : new TextEdit(1, 1, 1, 1, body + "\n"));
    }

    /// <summary>Sets the canvas position of a block: changes its layout line, or adds one at the end of the song.</summary>
    public static EditResult SetLayout(AmpleDocument doc, string block, int x, int y)
    {
        if (Find(doc, block) is null)
        {
            return EditResult.Refuse($"There is no block named \"{block}\".");
        }

        string line = $"layout {Quote(block)} {x.ToString(System.Globalization.CultureInfo.InvariantCulture)} {y.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        for (int i = 0; i < doc.Lines.Count; i++)
        {
            string t = doc.Lines[i].Trim();
            if (t.StartsWith("layout ", StringComparison.Ordinal) && Parser.Unquote(LayoutRaw(t)) == block)
            {
                return EditResult.Of(new TextEdit(i + 1, 1, i + 1, doc.Lines[i].Length + 1, line));
            }
        }

        int last = Enumerable.Range(0, doc.Lines.Count).Where(i => doc.Lines[i].Trim().Length > 0).DefaultIfEmpty(-1).Max();
        return EditResult.Of(last >= 0 ? InsertLineAfter(doc, last, line) : new TextEdit(1, 1, 1, 1, line + "\n"));
    }

    /// <summary>Sets the place of many blocks at once (Auto format, align, distribute): one pass over the text, existing layout lines are replaced and new ones are added together after the last line.</summary>
    public static EditResult SetLayouts(AmpleDocument doc, IReadOnlyList<(string Block, int X, int Y)> places)
    {
        foreach ((string block, _, _) in places)
        {
            if (Find(doc, block) is null)
            {
                return EditResult.Refuse($"There is no block named \"{block}\".");
            }
        }

        Dictionary<string, int> lineOf = [];
        for (int i = 0; i < doc.Lines.Count; i++)
        {
            string t = doc.Lines[i].Trim();
            if (t.StartsWith("layout ", StringComparison.Ordinal))
            {
                lineOf[Parser.Unquote(LayoutRaw(t))] = i;
            }
        }

        string Line((string Block, int X, int Y) p) => $"layout {Quote(p.Block)} {p.X.ToString(System.Globalization.CultureInfo.InvariantCulture)} {p.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        List<TextEdit> edits = [];
        List<string> added = [];
        foreach ((string Block, int X, int Y) p in places)
        {
            if (lineOf.TryGetValue(p.Block, out int i))
            {
                edits.Add(new TextEdit(i + 1, 1, i + 1, doc.Lines[i].Length + 1, Line(p)));
            }
            else
            {
                added.Add(Line(p));
            }
        }

        if (added.Count > 0)
        {
            int last = Enumerable.Range(0, doc.Lines.Count).Where(i => doc.Lines[i].Trim().Length > 0).DefaultIfEmpty(-1).Max();
            string joined = string.Join("\n", added);
            edits.Add(last >= 0 ? InsertLineAfter(doc, last, joined) : new TextEdit(1, 1, 1, 1, joined + "\n"));
        }

        return EditResult.Of([.. edits]);
    }

    /// <summary>Changes a block's arrangement parts by editing its source header.</summary>
    public static EditResult SetParts(AmpleDocument doc, string block, IReadOnlyList<Part> parts)
    {
        BlockNode? b = Find(doc, block);
        if (b is null) return EditResult.Refuse($"There is no block named \"{block}\".");
        if (parts.Count == 0 || parts.Any(p => p.At < 1 || p.Length < 1 || (long)p.At + p.Length - 1 > 256))
            return EditResult.Refuse("Arrangement parts must start at bar 1 or later, last at least one bar, and end by bar 256.");

        string raw = doc.Lines[b.Line];
        int open = raw.LastIndexOf('{');
        if (open < 0) return EditResult.Refuse($"Block \"{block}\" has no open header.");
        int placement = FindPlacement(raw, open);
        string value = string.Join(", ", parts.Select(p => $"{p.At.ToString(System.Globalization.CultureInfo.InvariantCulture)} for {p.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)}" + (p.Name.Length > 0 ? $" as {Quote(p.Name)}" : string.Empty)));
        string next = placement >= 0
            ? raw[..placement] + $"at {value} " + raw[open..]
            : raw[..open].TrimEnd() + $" at {value} " + raw[open..];
        return next == raw ? EditResult.Of() : EditResult.Of(new TextEdit(b.Line + 1, 1, b.Line + 1, raw.Length + 1, next));
    }

    private static int FindPlacement(string header, int open)
    {
        bool quoted = false;
        bool escaped = false;
        for (int i = 0; i + 3 < open; i++)
        {
            char c = header[i];
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') quoted = false;
                continue;
            }
            if (c == '"') { quoted = true; continue; }
            if (c == ' ' && header.AsSpan(i + 1, 2).SequenceEqual("at") && char.IsWhiteSpace(header[i + 3])) return i + 1;
        }
        return -1;
    }

    /// <summary>Adds a header flag (<c>bake</c>, <c>lock</c>, <c>refresh</c> or <c>disabled</c>) to a block when it is not there yet.</summary>
    public static EditResult AddFlag(AmpleDocument doc, string block, string flag)
    {
        BlockNode? b = Find(doc, block);
        if (b is null)
        {
            return EditResult.Refuse($"There is no block named \"{block}\".");
        }

        if (b.Flags.Contains(flag))
        {
            return EditResult.Of();
        }

        string header = doc.Lines[b.Line];
        int at = header.TrimEnd().EndsWith(" freeze seeds {", StringComparison.Ordinal) ? header.LastIndexOf(" freeze", StringComparison.Ordinal) : header.TrimEnd().EndsWith(" freeze plan {", StringComparison.Ordinal) ? header.LastIndexOf(" freeze", StringComparison.Ordinal) : header.LastIndexOf('{');
        if (at < 0)
        {
            return EditResult.Refuse("The block header has no opening brace.");
        }

        string insert = header[..at].EndsWith(' ') ? flag + " " : " " + flag + (header[at] == '{' ? " " : string.Empty);
        return EditResult.Of(new TextEdit(b.Line + 1, at + 1, b.Line + 1, at + 1, insert));
    }

    /// <summary>Sets the freeze level written in the block header: <c>free</c> (nothing written), <c>seeds</c> or <c>plan</c>.</summary>
    public static EditResult SetFreeze(AmpleDocument doc, string block, string level)
    {
        BlockNode? b = Find(doc, block);
        if (b is null)
        {
            return EditResult.Refuse($"There is no block named \"{block}\".");
        }

        if (level is not ("free" or "seeds" or "plan"))
        {
            return EditResult.Refuse("The freeze level must be free, seeds or plan.");
        }

        string header = doc.Lines[b.Line];
        string cleaned = System.Text.RegularExpressions.Regex.Replace(header, @"\s+freeze\s+(seeds|plan)(?=\s*\{)", string.Empty, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        int brace = cleaned.LastIndexOf('{');
        if (brace < 0)
        {
            return EditResult.Refuse("The block header has no opening brace.");
        }

        string result = level == "free" ? cleaned : cleaned[..brace].TrimEnd() + " freeze " + level + " " + cleaned[brace..];
        return result == header ? EditResult.Of() : EditResult.Of(new TextEdit(b.Line + 1, 1, b.Line + 1, header.Length + 1, result));
    }

    /// <summary>Removes a header flag from a block when it is there.</summary>
    public static EditResult RemoveFlag(AmpleDocument doc, string block, string flag)
    {
        BlockNode? b = Find(doc, block);
        if (b is null)
        {
            return EditResult.Refuse($"There is no block named \"{block}\".");
        }

        if (!b.Flags.Contains(flag))
        {
            return EditResult.Of();
        }

        string header = doc.Lines[b.Line];
        int brace = header.LastIndexOf('{');
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(header[..(brace < 0 ? header.Length : brace)], $@"\s{System.Text.RegularExpressions.Regex.Escape(flag)}(?=\s|$)", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        return m.Success ? EditResult.Of(new TextEdit(b.Line + 1, m.Index + 1, b.Line + 1, m.Index + m.Length + 1, string.Empty)) : EditResult.Refuse($"Flag \"{flag}\" was not found in the header of \"{block}\".");
    }

    /// <summary>Removes a block and its lines; refuses when other blocks read from it.</summary>
    public static EditResult RemoveBlock(AmpleDocument doc, string block)
    {
        BlockNode? b = Find(doc, block);
        if (b is null)
        {
            return EditResult.Refuse($"There is no block named \"{block}\".");
        }

        if (b.CloseLine < 0)
        {
            return EditResult.Refuse($"Block \"{block}\" is not closed.");
        }

        BlockNode? reader = doc.Blocks.FirstOrDefault(x => !ReferenceEquals(x, b) && x.Froms.Any(f => f.Block == block));
        if (reader is not null)
        {
            return EditResult.Refuse($"Block \"{reader.Name}\" still reads from \"{block}\".");
        }

        return EditResult.Of(new TextEdit(b.Line + 1, 1, b.CloseLine + 2, 1, string.Empty));
    }

    /// <summary>Connects an output of one block to an input of another, checking that both ports exist and the types fit.</summary>
    public static EditResult Connect(AmpleDocument doc, SpecData spec, string fromBlock, string output, string toBlock, string input)
    {
        BlockNode? src = Find(doc, fromBlock);
        BlockNode? dst = Find(doc, toBlock);
        if (src is null || dst is null)
        {
            return EditResult.Refuse($"There is no block named \"{(src is null ? fromBlock : toBlock)}\".");
        }

        SpecData effective = doc.Spec ?? spec;   // a component instance has its template in the spec of the song, not in the built-in one
        if (!effective.Templates.TryGetValue(src.Type, out TemplateSpec? ts) || !effective.Templates.TryGetValue(dst.Type, out TemplateSpec? td))
        {
            return EditResult.Refuse("Unknown block type.");
        }

        PortSpec? o = PortNames.Find(ts.Outputs, output, true);
        PortSpec? i = PortNames.Find(td.Inputs, input, false) ?? td.ControlInputs.FirstOrDefault(c => c.Name == input);   // a control also connects to a parameter that can be automated
        if (o is null)
        {
            return EditResult.Refuse($"Block \"{fromBlock}\" has no output \"{output}\".");
        }

        if (i is null)
        {
            return EditResult.Refuse($"Block \"{toBlock}\" has no input \"{input}\".");
        }

        if (!(ControlTypes.Is(o.Type) && ControlTypes.Is(i.Type)) && o.Type != i.Type && (o.Type == "render" || i.Type == "render" || o.Type == "region" || i.Type == "region" || o.Type != "any" && i.Type != "any"))
        {
            return EditResult.Refuse($"Type mismatch: {fromBlock}.{output} is {o.Type} but {toBlock}.{input} wants {i.Type}.");
        }

        if (ReferenceEquals(src, dst))
        {
            return EditResult.Refuse("A block cannot read from itself.");
        }

        if (dst.Froms.Any(f => f.Input == i.Name))
        {
            return EditResult.Refuse($"Input \"{input}\" of \"{toBlock}\" already has a source.");
        }

        if (DependsOn(doc, src.Name, dst.Name))
        {
            return EditResult.Refuse("This connection would make a cycle.");
        }

        if (dst.CloseLine < 0)
        {
            return EditResult.Refuse($"Block \"{toBlock}\" is not closed.");
        }

        int after = new[] { dst.Line }.Concat(dst.Parameters.Select(p => p.Line)).Concat(dst.Froms.Select(f => f.Line)).Max();
        return EditResult.Of(InsertLineAfter(doc, after, $"  from {Quote(fromBlock)}.{Quote(PortNames.Public(o, ts.Outputs, true))} -> {Quote(PortNames.Public(i, td.Inputs, false))}"));
    }

    /// <summary>Rewrites the source of an existing <c>from</c> line in place: the line keeps its place in the block, its input and what follows the arrow.</summary>
    private static EditResult RewriteSource(AmpleDocument doc, string toBlock, string input, string newBlock, string newOutputPublic)
    {
        FromLine? f = Find(doc, toBlock)?.Froms.FirstOrDefault(x => x.Input == input);
        if (f is null)
        {
            return EditResult.Refuse($"Input \"{input}\" of \"{toBlock}\" has no connection.");
        }

        string raw = doc.Lines[f.Line].TrimEnd('\r');
        int from = raw.IndexOf("from", StringComparison.Ordinal);
        int arrow = from < 0 ? -1 : raw.IndexOf("->", from, StringComparison.Ordinal);
        if (arrow < 0)
        {
            return EditResult.Refuse("The connection line could not be read.");
        }

        string rewritten = raw[..(from + 4)] + " " + Quote(newBlock) + "." + Quote(newOutputPublic) + " " + raw[arrow..];
        return EditResult.Of(new TextEdit(f.Line + 1, 1, f.Line + 1, raw.Length + 1, rewritten));
    }

    /// <summary>
    /// Puts a new effect block after a block: the effect reads the block, and everything that read the block now reads the effect. Returns the new text,
    /// or null with the reason in <paramref name="error"/>.
    /// </summary>
    public static string? InsertEffectAfter(string text, SpecData spec, string block, string type, string newName, IReadOnlyList<(string Name, string Value)> parameters, out string? error)
    {
        error = null;
        AmpleDocument doc = Parser.Parse(text, spec);
        BlockNode? source = Find(doc, block);
        if (source is null || !(doc.Spec ?? spec).Templates.TryGetValue(source.Type, out TemplateSpec? sourceSpec) || !(doc.Spec ?? spec).Templates.TryGetValue(type, out TemplateSpec? effectSpec))
        {
            error = $"There is no block \"{block}\" or no template \"{type}\".";
            return null;
        }

        PortSpec? from = sourceSpec.Outputs.FirstOrDefault(p => p.Type == "audio");
        PortSpec? effectIn = effectSpec.Inputs.FirstOrDefault(p => p.Type == "audio");
        PortSpec? effectOut = effectSpec.Outputs.FirstOrDefault(p => p.Type == "audio");
        if (from is null || effectIn is null || effectOut is null)
        {
            error = $"\"{block}\" has no audio output, or \"{type}\" is not an audio effect.";
            return null;
        }

        string name = newName;
        for (int n = 2; Find(doc, name) is not null; n++)
        {
            name = newName + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        List<(string To, string Input)> consumers = [.. doc.Blocks.SelectMany(b => b.Froms.Where(f => f.Block == block && f.Output == from.Name && !f.Disabled).Select(f => (b.Name, f.Input)))];
        string current = text;
        string? failure = null;
        bool Step(Func<AmpleDocument, EditResult> make)
        {
            EditResult result = make(Parser.Parse(current, spec));
            if (!result.Ok)
            {
                failure = result.Error;
                return false;
            }

            current = Apply(current, result.Edits);
            return true;
        }

        bool done = Step(d => AddBlock(d, spec, type, name));
        foreach ((string parameter, string value) in parameters)
        {
            done = done && Step(d => SetParameter(d, name, parameter, value));
        }

        done = done && Step(d => Connect(d, spec, block, from.Name, name, effectIn.Name));
        string effectOutPublic = PortNames.Public(effectOut, effectSpec.Outputs, true);
        foreach ((string to, string input) in consumers)
        {
            done = done && Step(d => RewriteSource(d, to, input, name, effectOutPublic));
        }

        error = failure;
        return done ? current : null;
    }

    /// <summary>
    /// Takes an effect out of a chain, the mirror of <see cref="InsertEffectAfter"/>: everything that read the effect now reads what the effect read, and the
    /// block goes. A sidechain key into the effect goes with it. Returns the new text, or null with the reason in <paramref name="error"/>.
    /// </summary>
    public static string? BypassEffect(string text, SpecData spec, string block, out string? error)
    {
        error = null;
        AmpleDocument doc = Parser.Parse(text, spec);
        BlockNode? effect = Find(doc, block);
        if (effect is null || !(doc.Spec ?? spec).Templates.TryGetValue(effect.Type, out TemplateSpec? effectSpec))
        {
            error = $"There is no block \"{block}\".";
            return null;
        }

        PortSpec? effectIn = effectSpec.Inputs.FirstOrDefault(p => p.Type == "audio" && p.Name != "key");
        PortSpec? effectOut = effectSpec.Outputs.FirstOrDefault(p => p.Type == "audio");
        FromLine? source = effectIn is null ? null : effect.Froms.FirstOrDefault(f => f.Input == effectIn.Name && !f.Disabled);
        if (effectIn is null || effectOut is null || source is null)
        {
            error = $"\"{block}\" has no audio coming in, so there is nothing to connect its readers to.";
            return null;
        }

        List<(string To, string Input)> consumers = [.. doc.Blocks.SelectMany(b => b.Froms.Where(f => f.Block == block && f.Output == effectOut.Name).Select(f => (b.Name, f.Input)))];
        string current = text;
        string? failure = null;
        bool Step(Func<AmpleDocument, EditResult> make)
        {
            EditResult result = make(Parser.Parse(current, spec));
            if (!result.Ok)
            {
                failure = result.Error;
                return false;
            }

            current = Apply(current, result.Edits);
            return true;
        }

        bool done = true;
        foreach ((string to, string input) in consumers)
        {
            done = done && Step(d => RewriteSource(d, to, input, source.Block, Parser.Unquote(source.RawOutput)));
        }

        done = done && Step(d => RemoveBlock(d, block));
        error = failure;
        return done ? current : null;
    }

    /// <summary>
    /// Copies a block under a new name, right after the original: the same header, parameters, notes and inputs (its `from` lines), and the same placement.
    /// Nothing reads the copy yet. The layout line is not copied.
    /// </summary>
    public static EditResult CopyBlock(AmpleDocument doc, string block, string newName)
    {
        BlockNode? b = Find(doc, block);
        if (b is null)
        {
            return EditResult.Refuse($"There is no block named \"{block}\".");
        }

        if (b.CloseLine < 0)
        {
            return EditResult.Refuse($"Block \"{block}\" is not closed.");
        }

        if (Find(doc, newName) is not null)
        {
            return EditResult.Refuse($"Two blocks would be called \"{newName}\".");
        }

        string header = doc.Lines[b.Line];
        int typeEnd = header.IndexOf(b.Type, StringComparison.Ordinal) + b.Type.Length;
        int at = header.IndexOf(b.RawName, typeEnd, StringComparison.Ordinal);
        List<string> lines = [header[..at] + Quote(newName) + header[(at + b.RawName.Length)..]];
        for (int i = b.Line + 1; i <= b.CloseLine; i++)
        {
            lines.Add(doc.Lines[i]);
        }

        int end = b.CloseLine + 1;   // 0-based line after the block
        string insert = string.Join("\n", lines) + "\n";
        return EditResult.Of(new TextEdit(end + 1, 1, end + 1, 1, insert));
    }

    /// <summary>Makes an edit that replaces the whole text of a document by another (the way an operation that works on text, such as an insert of an effect, reports its result).</summary>
    public static EditResult ReplaceText(AmpleDocument doc, string newText)
    {
        string[] parts = doc.Text.Split('\n');
        return EditResult.Of(new TextEdit(1, 1, parts.Length, parts[^1].Length + 1, newText));
    }

    /// <summary>Removes the connection into an input.</summary>
    public static EditResult SetConnectionDisabled(AmpleDocument doc, string toBlock, string input, bool disabled)
    {
        FromLine? connection = Find(doc, toBlock)?.Froms.FirstOrDefault(f => f.Input == input);
        if (connection is null) return EditResult.Refuse("The input has no connection.");
        string line = doc.Text.Split('\n')[connection.Line].TrimEnd('\r');
        string replacement = connection.Disabled ? line[..line.LastIndexOf(" disabled", StringComparison.Ordinal)] : line;
        if (disabled) replacement += " disabled";
        return EditResult.Of(new TextEdit(connection.Line + 1, 1, connection.Line + 1, line.Length + 1, replacement));
    }

    /// <summary>Removes the connection into an input.</summary>
    public static EditResult Disconnect(AmpleDocument doc, string toBlock, string input)
    {
        BlockNode? dst = Find(doc, toBlock);
        FromLine? f = dst?.Froms.FirstOrDefault(x => x.Input == input);
        return f is null ? EditResult.Refuse($"Input \"{input}\" of \"{toBlock}\" has no connection.") : EditResult.Of(new TextEdit(f.Line + 1, 1, f.Line + 2, 1, string.Empty));
    }

    /// <summary>Sets a property that is driven by an expression, with an explicit choice. Modes: override (keep the expression, write bake EXPR as VALUE @hash override), bake (freeze what the expression gives now), again (remove the bake so the expression drives it again), literal (replace the expression by the value), expression (the value is the new expression).</summary>
    /// <param name="doc">The document.</param>
    /// <param name="block">The block.</param>
    /// <param name="name">The property.</param>
    /// <param name="value">The new value (ignored by bake and again).</param>
    /// <param name="mode">The mode.</param>
    /// <returns>The edit, or the reason it was refused.</returns>
    public static EditResult SetPropertyValue(AmpleDocument doc, string block, string name, string value, string mode)
    {
        BlockNode? b = Find(doc, block);
        Parameter? existing = b?.Parameters.FirstOrDefault(p => p.Name == name);
        PropertyBinding? binding = doc.Properties.FirstOrDefault(x => x.Block == block && x.Name == name);
        if (b is null || existing is null || binding is null) return SetParameter(doc, block, name, value);
        static string Compact(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", string.Empty, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        string expression = binding.ExpressionText.Length > 0 ? binding.ExpressionText : binding.Expression;
        string? text = mode switch
        {
            "override" => $"bake {expression} as {Compact(value)} @{binding.Hash} override",
            "bake" => $"bake {expression} as {Compact(binding.SourceText ?? binding.Value ?? string.Empty)} @{binding.Hash}",
            "again" => expression,
            "literal" => value,
            "expression" => value,
            _ => null,
        };
        if (text is null) return EditResult.Refuse("Choose a mode: override, bake, again, literal or expression.");
        string raw = doc.Lines[existing.Line];
        int start = raw.IndexOf(existing.Value, raw.IndexOf(name, StringComparison.Ordinal) + name.Length, StringComparison.Ordinal);
        if (start < 0) return EditResult.Refuse($"The source value of {name} could not be found.");
        return EditResult.Of(new TextEdit(existing.Line + 1, start + 1, existing.Line + 1, start + 1 + existing.Value.Length, text));
    }

    /// <summary>Bakes the clock-dependent values of a song as they were at an instant (the clock of the last build): the dynamic helpers without parameters get " bake as VALUE" in their declaration (the bodies stay), and the places in the blocks that read the clock directly or call a dynamic helper with parameters are written as "bake EXPRESSION as VALUE". With a block name only that block is touched, and then calls of helpers without parameters are baked at the call too, so the helper stays dynamic for the others.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="at">The clock of the build to bake.</param>
    /// <param name="block">The one block to bake, or null for the whole song.</param>
    /// <returns>The edits, or the reason it was refused.</returns>
    public static EditResult BakeDynamicHelpers(AmpleDocument doc, DateTime at, string? block = null)
    {
        if (block is not null && Find(doc, block) is null) return EditResult.Refuse($"There is no block named \"{block}\".");
        NotePipeline.Context context = new() { Tempo = (doc.Project?.Tempo ?? 120).ToString(System.Globalization.CultureInfo.InvariantCulture), BeatsPerBar = (decimal)(doc.Project?.BeatsPerBar ?? 4.0), Helpers = doc.HelperTable, Limits = LangLimits.From(doc.Settings), Clock = () => at, Capabilities = doc.CapabilityPolicy, MasterSeed = doc.Project?.MasterSeed ?? 0 };
        List<TextEdit> edits = [];
        List<string> left = [];
        int helpers = 0, sites = 0;
        if (block is null)
        {
            List<HelperInfo> targets = [.. doc.Helpers.Where(h => h.Kind == "helper" && h.Dynamic && !h.Baked && h.Params.Count == 0 && !h.Dependencies.Contains("os-random"))];
            foreach ((string name, string value) in targets.Count == 0 ? [] : NotePipeline.DynamicHelperValues(context, targets.Select(t => t.Name)))
            {
                HelperInfo h = targets.First(t => t.Name == name);
                System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(doc.Lines[h.Line], @"^(.*\)\s*(?:->\s*[A-Za-z_][\w<>?]*)?)\s*\{", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
                if (!m.Success) continue;
                int col = m.Groups[1].Length + 1;
                edits.Add(new TextEdit(h.Line + 1, col, h.Line + 1, col, " bake as " + value));
                helpers++;
            }
        }

        foreach (BlockNode b in doc.Blocks.Where(x => !x.Skipped && (block is null || x.Name == block)))
        {
            foreach (Parameter p in b.Parameters)
            {
                bool notes = b.Type == "midi-clip" && p.Name == "notes" && !p.Value.TrimStart().StartsWith('"') && !p.Value.TrimStart().StartsWith('{');
                if (!notes && !NotePipeline.IsPropertyExpression(p.Value)) continue;
                (IReadOnlyList<NotePipeline.BakeSite> found, IReadOnlyList<string> notBaked) = NotePipeline.BakeSitesIn(p.Value, context, block is not null);
                left.AddRange(notBaked.Select(x => b.Name + ": " + x));
                foreach (NotePipeline.BakeSite site in found)
                {
                    (int line, int col)? start = Locate(doc, p, notes, site.Start), end = Locate(doc, p, notes, site.End);
                    if (start is null || end is null) continue;
                    edits.Add(new TextEdit(start.Value.line + 1, start.Value.col + 1, start.Value.line + 1, start.Value.col + 1, "bake "));
                    edits.Add(new TextEdit(end.Value.line + 1, end.Value.col + 1, end.Value.line + 1, end.Value.col + 1, " as " + site.Value));
                    sites++;
                }
            }
        }

        if (edits.Count == 0) return EditResult.Refuse(left.Count > 0 ? "Nothing here can be baked as a single value: " + string.Join(", ", left.Distinct()) + " give notes or read other blocks (bake their notes in Jam instead)." : "There is nothing that reads the clock and can be baked (a helper that reads osRandom() cannot be baked: its value is not repeatable).");
        string summary = $"{helpers} helper(s) and {sites} place(s) baked" + (left.Count > 0 ? "; still dynamic: " + string.Join(", ", left.Distinct()) : string.Empty);
        return new EditResult(edits, null) { Summary = summary };
    }

    private static (int Line, int Col)? Locate(AmpleDocument doc, Parameter p, bool notes, int offset)
    {
        if (offset < 0 || offset > p.Value.Length) return null;
        if (notes)
        {
            string before = p.Value[..offset];
            int breaks = before.Count(ch => ch == '\n');
            return breaks == 0 ? (p.Line, p.ValueColumn + offset) : (p.Line + breaks, offset - before.LastIndexOf('\n') - 1);
        }

        string raw = doc.Lines[p.Line];
        int at = raw.IndexOf(p.Value, Math.Max(0, raw.IndexOf(p.Name, StringComparison.Ordinal)) + p.Name.Length, StringComparison.Ordinal);
        return at < 0 ? null : (p.Line, at + offset);
    }

    /// <summary>Bakes one call of a dynamic helper: name(args) becomes "bake name(args) as VALUE" with the value the call had at the given instant. The helper itself stays dynamic. The arguments must be values that do not read other blocks (numbers, words, constants, other calls).</summary>
    /// <param name="doc">The document.</param>
    /// <param name="line">The 1-based line of the call.</param>
    /// <param name="col">The 1-based column inside the name of the call.</param>
    /// <param name="at">The clock of the build to bake.</param>
    /// <returns>The edit, or the reason it was refused.</returns>
    public static EditResult BakeCallSite(AmpleDocument doc, int line, int col, DateTime at)
    {
        HelperInfo? h = Queries.HelperAt(doc, line, col);
        if (h is null || h.Kind != "helper" || !h.Dynamic) return EditResult.Refuse("Put the cursor on the name of a call of a dynamic helper, like phase() or hourShift(3).");
        if (h.Dependencies.Contains("os-random")) return EditResult.Refuse("A helper that reads osRandom() cannot be baked: its value is not repeatable.");
        Location? call = Queries.HelperOccurrences(doc, h).Skip(1).FirstOrDefault(l => l.Line == line && col >= l.Col && col <= l.EndCol);
        if (call is null) return EditResult.Refuse("That is not a call of the helper.");
        string code = doc.Lines[line - 1];
        if (code[..(call.Col - 1)].TrimEnd().EndsWith("bake", StringComparison.Ordinal)) return EditResult.Refuse("This call is baked already.");
        int open = call.EndCol - 1;
        while (open < code.Length && code[open] == ' ') open++;
        if (open >= code.Length || code[open] != '(') return EditResult.Refuse("Write the call with its arguments: name(...).");
        int depth = 0, close = -1;
        bool inString = false;
        for (int i = open; i < code.Length && close < 0; i++)
        {
            if (code[i] == '"') inString = !inString;
            else if (!inString && code[i] == '(') depth++;
            else if (!inString && code[i] == ')' && --depth == 0) close = i;
        }

        if (close < 0) return EditResult.Refuse("The call goes over several lines: put it on one line to bake it here.");
        NotePipeline.Context context = new() { Tempo = (doc.Project?.Tempo ?? 120).ToString(System.Globalization.CultureInfo.InvariantCulture), BeatsPerBar = (decimal)(doc.Project?.BeatsPerBar ?? 4.0), Helpers = doc.HelperTable, Limits = LangLimits.From(doc.Settings), Clock = () => at, Capabilities = doc.CapabilityPolicy, MasterSeed = doc.Project?.MasterSeed ?? 0 };
        string callText = code[(call.Col - 1)..(close + 1)];
        IReadOnlyList<(string Name, string Value)> values = NotePipeline.ValuesOf(context, [callText]);
        if (values.Count == 0) return EditResult.Refuse("The call could not be evaluated here: it gives notes, or its arguments read other blocks (bake the notes in Jam instead).");
        return EditResult.Of(new TextEdit(line, call.Col, line, close + 2, $"bake {callText} as {values[0].Value}"));
    }

    /// <summary>Renames a helper (or constant): its declaration and every call, in one step.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="oldName">The current name.</param>
    /// <param name="newName">The new name (lowerCamelCase).</param>
    /// <returns>The edits, or the reason it was refused.</returns>
    public static EditResult RenameHelper(AmpleDocument doc, string oldName, string newName)
    {
        HelperInfo? h = doc.Helpers.FirstOrDefault(x => string.Equals(x.Name, oldName, StringComparison.OrdinalIgnoreCase));
        if (h is null) return EditResult.Refuse($"There is no helper named \"{oldName}\".");
        if (!System.Text.RegularExpressions.Regex.IsMatch(newName, "^[A-Za-z_][A-Za-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1))) return EditResult.Refuse("A helper name is letters, digits and underscores, starting with a letter (lowerCamelCase).");
        if (doc.Helpers.Any(x => !ReferenceEquals(x, h) && string.Equals(x.Name, newName, StringComparison.OrdinalIgnoreCase))) return EditResult.Refuse($"Two declarations would be called \"{newName}\".");
        if (NoteCatalog.Signatures.ContainsKey(newName) || newName is "Int" or "Float" or "Round" or "Floor" or "Ceil" or "Truncate" or "count" or "span" or "min" or "max" or "abs" or "at" or "len" or "Note" or "currentTime" or "currentDate" or "osRandom" or "environment") return EditResult.Refuse($"\"{newName}\" is the name of an operator or a built-in.");
        return new EditResult([.. Queries.HelperOccurrences(doc, h).Select(l => new TextEdit(l.Line, l.Col, l.Line, l.EndCol, newName))], null);
    }

    /// <summary>Renames a block: its header, every from line that reads it and its layout line.</summary>
    public static EditResult RenameBlock(AmpleDocument doc, string oldName, string newName)
    {
        BlockNode? b = Find(doc, oldName);
        if (b is null)
        {
            return EditResult.Refuse($"There is no block named \"{oldName}\".");
        }

        if (Find(doc, newName) is not null)
        {
            return EditResult.Refuse($"Two blocks would be called \"{newName}\".");
        }

        List<TextEdit> edits = [];
        string header = doc.Lines[b.Line];
        int typeEnd = header.IndexOf(b.Type, StringComparison.Ordinal) + b.Type.Length;
        int at = header.IndexOf(b.RawName, typeEnd, StringComparison.Ordinal);
        edits.Add(new TextEdit(b.Line + 1, at + 1, b.Line + 1, at + 1 + b.RawName.Length, Quote(newName)));
        foreach (BlockNode other in doc.Blocks)
        {
            foreach (FromLine f in other.Froms.Where(x => x.Block == oldName))
            {
                int p = doc.Lines[f.Line].IndexOf(f.RawBlock, doc.Lines[f.Line].IndexOf("from", StringComparison.Ordinal) + 4, StringComparison.Ordinal);
                edits.Add(new TextEdit(f.Line + 1, p + 1, f.Line + 1, p + 1 + f.RawBlock.Length, Quote(newName)));
            }
        }

        for (int i = 0; i < doc.Lines.Count; i++)
        {
            string t = doc.Lines[i].TrimStart();
            if (t.StartsWith("layout ", StringComparison.Ordinal) && Parser.Unquote(LayoutRaw(t)) == oldName)
            {
                string raw = LayoutRaw(t);
                int p = doc.Lines[i].IndexOf(raw, doc.Lines[i].IndexOf("layout", StringComparison.Ordinal) + 6, StringComparison.Ordinal);
                edits.Add(new TextEdit(i + 1, p + 1, i + 1, p + 1 + raw.Length, Quote(newName)));
            }
        }

        RenameInView(doc, oldName, newName, edits);

        return new EditResult(edits, null);
    }

    /// <summary>The view block names blocks in <c>member</c>, <c>size</c> and <c>pin</c> lines; a rename follows them there, so a group does not lose its member.</summary>
    private static void RenameInView(AmpleDocument doc, string oldName, string newName, List<TextEdit> edits)
    {
        bool inView = false;
        for (int i = 0; i < doc.Lines.Count; i++)
        {
            string raw = doc.Lines[i], trimmed = raw.Trim();
            if (!inView)
            {
                inView = trimmed == "view {" && doc.Blocks.All(b => i < b.Line || i > b.CloseLine);
                continue;
            }

            if (raw == "}")
            {
                inView = false;
                continue;
            }

            string[] words = [.. System.Text.RegularExpressions.Regex.Matches(trimmed, "\"(?:[^\"\\\\]|\\\\.)*\"|\\S+").Select(m => m.Value)];
            // The words of a line that are block names: member/pin/size/line have one (the second word), order has all after the first.
            int last = words.Length == 2 && words[0] is "member" or "pin" || words.Length == 3 && words[0] is "size" or "line" ? 1 : words.Length >= 2 && words[0] == "order" ? words.Length - 1 : 0;
            int from = 0;
            for (int k = 1; k <= last; k++)
            {
                if (Parser.Unquote(words[k]) != oldName)
                {
                    continue;
                }

                int p = raw.IndexOf(words[k], from > 0 ? from : raw.IndexOf(words[0], StringComparison.Ordinal) + words[0].Length, StringComparison.Ordinal);
                edits.Add(new TextEdit(i + 1, p + 1, i + 1, p + 1 + words[k].Length, ViewBlock.Word(newName)));
                from = p + words[k].Length;
            }
        }
    }

    /// <summary>Sets one plugin setting of the song (<c>plugin.name</c>) in the settings line, which is created after the project line when the song has none.</summary>
    public static EditResult SetSetting(AmpleDocument doc, string key, string value)
    {
        System.Text.RegularExpressions.Regex shape = new("^[a-z][a-z0-9-]*\\.[A-Za-z][A-Za-z0-9]*$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        if (!shape.IsMatch(key))
        {
            return EditResult.Refuse($"A setting is written as plugin.name (found \"{key}\").");
        }

        Dictionary<string, string> all = new(doc.Settings, StringComparer.Ordinal) { [key] = value };
        return WriteSettings(doc, all);
    }

    /// <summary>Removes one plugin setting from the song (the plugin's own default applies again).</summary>
    public static EditResult RemoveSetting(AmpleDocument doc, string key)
    {
        if (!doc.Settings.ContainsKey(key))
        {
            return EditResult.Of();
        }

        Dictionary<string, string> all = new(doc.Settings, StringComparer.Ordinal);
        all.Remove(key);
        return WriteSettings(doc, all);
    }

    private static EditResult WriteSettings(AmpleDocument doc, Dictionary<string, string> all)
    {
        string line = all.Count == 0 ? string.Empty : "settings { " + string.Join("  ", all.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + " " + Quote(p.Value))) + " }";
        if (doc.SettingsLine >= 0)
        {
            return line.Length == 0
                ? EditResult.Of(new TextEdit(doc.SettingsLine + 1, 1, doc.SettingsLine + 2, 1, string.Empty))
                : EditResult.Of(new TextEdit(doc.SettingsLine + 1, 1, doc.SettingsLine + 1, doc.Lines[doc.SettingsLine].Length + 1, line));
        }

        if (line.Length == 0)
        {
            return EditResult.Of();
        }

        int after = doc.Project?.Line ?? -1;
        return EditResult.Of(new TextEdit(after + 2, 1, after + 2, 1, line + "\n"));
    }

    /// <summary>Quotes a name when it is not a plain identifier.</summary>
    public static string Quote(string name) => name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? name : JsonSerializer.Serialize(name);

    private static string LayoutRaw(string trimmed)
    {
        string rest = trimmed["layout ".Length..].TrimStart();
        if (rest.StartsWith('"'))
        {
            int end = 1;
            while (end < rest.Length && !(rest[end] == '"' && rest[end - 1] != '\\'))
            {
                end++;
            }

            return rest[..Math.Min(end + 1, rest.Length)];
        }

        return rest.Split(' ')[0];
    }

    private static BlockNode? Find(AmpleDocument doc, string name) => doc.Blocks.FirstOrDefault(b => !b.Skipped && b.Name == name);

    private static bool DependsOn(AmpleDocument doc, string block, string candidate)
    {
        HashSet<string> seen = [];
        bool Walk(string name)
        {
            if (!seen.Add(name))
            {
                return false;
            }

            BlockNode? b = Find(doc, name);
            return b is not null && b.Froms.Any(f => f.Block == candidate || Walk(f.Block));
        }

        return Walk(block);
    }

    private static TextEdit InsertLineAfter(AmpleDocument doc, int zeroBasedLine, string text) =>
        new(zeroBasedLine + 1, doc.Lines[zeroBasedLine].Length + 1, zeroBasedLine + 1, doc.Lines[zeroBasedLine].Length + 1, "\n" + text);

    private static int Offset(int[] starts, string[] lines, int line, int col)
    {
        int l = Math.Clamp(line, 1, lines.Length);
        int offset = starts[l - 1] + Math.Min(col - 1, lines[l - 1].Length);
        return line > lines.Length ? starts[lines.Length - 1] + lines[^1].Length : offset;
    }
}
