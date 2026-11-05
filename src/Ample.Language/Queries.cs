namespace Ample.Language;

/// <summary>A place in a document: 1-based line, start column and exclusive end column.</summary>
/// <param name="Line">Line.</param>
/// <param name="Col">Start column (UTF-16 index + 1).</param>
/// <param name="EndCol">Exclusive end column.</param>
public sealed record Location(int Line, int Col, int EndCol);

/// <summary>An outline entry.</summary>
/// <param name="Name">Block name.</param>
/// <param name="Type">Block type.</param>
/// <param name="Header">Where the header is.</param>
/// <param name="LastLine">Last line of the block (closing brace line), 1-based.</param>
public sealed record DocumentSymbol(string Name, string Type, Location Header, int LastLine);

/// <summary>One completion proposal.</summary>
/// <param name="Label">Text to insert.</param>
/// <param name="Kind">block-type, parameter, block, output, input, keyword, class.</param>
/// <param name="Detail">Short explanation.</param>
/// <param name="Insert">The text a chosen item puts in, when it is not the label.</param>
public sealed record CompletionItem(string Label, string Kind, string Detail, string? Insert = null);

/// <summary>A setting a plugin accepts in the song's <c>settings { … }</c> line, as the editor proposes it.</summary>
/// <param name="Name">The full name, <c>plugin.setting</c>.</param>
/// <param name="Detail">What it does (the plugin's own description).</param>
/// <param name="Options">The values it can take when that is a fixed list (empty for free text and numbers).</param>
/// <param name="Default">The value the plugin uses when the song does not say (empty when there is none).</param>
public sealed record SettingHint(string Name, string Detail, IReadOnlyList<string> Options, string Default);

/// <summary>Read-only questions about a document, used by the language server and the editor.</summary>
public static class Queries
{
    /// <summary>Lists the blocks as an outline.</summary>
    public static IReadOnlyList<DocumentSymbol> DocumentSymbols(AmpleDocument doc) =>
    [
        .. doc.Blocks.Where(b => !b.Skipped).Select(b =>
        {
            Location header = NameLocation(doc, b);
            return new DocumentSymbol(b.Name, b.Type, header, (b.CloseLine >= 0 ? b.CloseLine : doc.Lines.Count - 1) + 1);
        }),
    ];

    /// <summary>Finds the definition (block header name) of the block, or the declaration of the helper, referenced at the position.</summary>
    public static Location? FindDefinition(AmpleDocument doc, int line, int col)
    {
        BlockNode? target = BlockAt(doc, line, col);
        if (target is not null) return NameLocation(doc, target);
        return HelperAt(doc, line, col) is { } h ? HelperDeclaration(doc, h) : null;
    }

    /// <summary>The component whose type is written at the position (the first word of a block header), or null.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="line">The 1-based line.</param>
    /// <param name="col">The 1-based column.</param>
    /// <returns>The component (the default version of that name), or null.</returns>
    public static ComponentDefinition? ComponentTypeAt(AmpleDocument doc, int line, int col)
    {
        string raw = Line(doc, line);
        string trimmed = raw.TrimStart();
        int start = raw.Length - trimmed.Length;
        int space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        string word = space < 0 ? trimmed : trimmed[..space];
        if (word.Length == 0 || col - 1 < start || col - 1 > start + word.Length) return null;
        IEnumerable<ComponentDefinition> all = doc.Components.Concat(doc.ImportedComponents).Where(c => c.Name == word);
        return ComponentVersions.Default(all);
    }

    private static readonly string[] VolatileNames = ["currentTime", "currentDate", "osRandom", "environment"];

    /// <summary>The helper, constant or type of the file that the word at the position names (names are compared ignoring case), or null.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="line">The 1-based line.</param>
    /// <param name="col">The 1-based column.</param>
    /// <returns>The declaration, or null.</returns>
    public static HelperInfo? HelperAt(AmpleDocument doc, int line, int col)
    {
        if (doc.Helpers.Count == 0) return null;
        string raw = Line(doc, line);
        int hash = raw.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0 && col - 1 > hash) return null;
        string? word = WordAt(raw, col);
        return word is null ? null : doc.Helpers.FirstOrDefault(h => string.Equals(h.Name, word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Where the name of a helper is declared.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="h">The helper.</param>
    /// <returns>The location of the name on its declaration line.</returns>
    public static Location HelperDeclaration(AmpleDocument doc, HelperInfo h)
    {
        string raw = doc.Lines[h.Line];
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(raw, @"^(?:dynamic\s+)?(?:helper|type|let)\s+(?:[A-Za-z][\w<>]*\s+)?(" + System.Text.RegularExpressions.Regex.Escape(h.Name) + @")(?![\w-])", System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        return m.Success ? new Location(h.Line + 1, m.Groups[1].Index + 1, m.Groups[1].Index + 1 + h.Name.Length) : new Location(h.Line + 1, 1, 1 + h.Name.Length);
    }

    /// <summary>Every place a helper or constant is written: its declaration and its calls (outside strings and comments).</summary>
    /// <param name="doc">The document.</param>
    /// <param name="h">The helper.</param>
    /// <returns>The locations.</returns>
    public static IReadOnlyList<Location> HelperOccurrences(AmpleDocument doc, HelperInfo h)
    {
        List<Location> found = [HelperDeclaration(doc, h)];
        System.Text.RegularExpressions.Regex word = new(@"(?<![\w-])" + System.Text.RegularExpressions.Regex.Escape(h.Name) + @"(?![\w-])", System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        for (int i = 0; i < doc.Lines.Count; i++)
        {
            string code = MaskStringsAndComments(doc.Lines[i]);
            foreach (System.Text.RegularExpressions.Match m in word.Matches(code))
            {
                if (i == h.Line && found[0].Col == m.Index + 1) continue;
                string after = code[(m.Index + m.Length)..].TrimStart();
                bool call = after.StartsWith('(');
                bool constant = h.Kind == "const" && !code.TrimStart().StartsWith(h.Name + " ", StringComparison.OrdinalIgnoreCase);
                if (call || constant) found.Add(new Location(i + 1, m.Index + 1, m.Index + 1 + m.Length));
            }
        }

        return found;
    }

    private static string MaskStringsAndComments(string line)
    {
        char[] chars = line.ToCharArray();
        bool inString = false;
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] == '"') { inString = !inString; chars[i] = ' '; }
            else if (inString) chars[i] = ' ';
            else if (chars[i] == '#' && !(i > 0 && char.IsLetterOrDigit(line[i - 1]))) { for (int k = i; k < chars.Length; k++) chars[k] = ' '; break; }
        }

        return new string(chars);
    }

    private static string Signature(string name, IEnumerable<string> parameters, string? returns) => $"{name}({string.Join(", ", parameters)})" + (returns is null ? string.Empty : $" -> {returns}");

    private static string HelperText(HelperInfo h, string? inferred = null) => Signature(h.Name, h.Params.Select(p => $"{p.Type} {p.Name}" + (p.Default is null ? string.Empty : " = " + p.Default)), h.Returns ?? inferred);

    /// <summary>The hover of a variable (a let, a var, a constant, a parameter or a loop variable): what it is, its value, the type that was worked out without running anything and the types it can have; on the let or var keyword, what the keyword does and what it declares.</summary>
    private static string? VariableHover(AmpleDocument doc, int line, int col, string word)
    {
        if (doc.HelperTable.VariableSpots.Count == 0) return null;
        int at = line - 1;
        foreach (int declLine in doc.HelperTable.VariableSpots.Select(s => s.Line).Where(l => l <= at).Distinct().OrderByDescending(l => l))
        {
            int offset = col - 1;
            for (int k = declLine; k < at && k < doc.Lines.Count; k++) offset += doc.Lines[k].Length + 1;
            VariableSpot? spot = doc.HelperTable.VariableSpots.FirstOrDefault(s => s.Line == declLine && offset >= s.Start && offset < s.End);
            if (spot is null) continue;
            VariableDetail v = spot.Detail;
            string signature = $"{v.Kind switch { "const" => "let", "parameter" or "loop variable" => v.Kind, _ => v.Kind }} {v.Name}: {v.Type}";
            if (spot.Keyword && NoteCatalog.Builtins.TryGetValue(word, out NoteSignature? keyword)) return $"`{keyword.Id}` — keyword\n\n{keyword.Doc}\n\nDeclares `{v.Name}: {v.Type}`.\n\nBuilt in.";
            List<string> parts = [$"`{signature}`"];
            if (v.Kind == "parameter" && doc.Docs.TryGetValue(spot.Line, out DocComment? comment) && comment.ParamText(v.Name) is { Length: > 0 } note) parts.Add(note);
            if (v.ValueText.Length > 0) parts.Add($"= `{v.ValueText}`");
            parts.Add(v.Origin.Length == 0 ? $"**Type** `{v.Type}`" : $"**Type** `{v.Type}` — {(v.Origin == "the declared type" ? v.Origin : "from " + v.Origin)}");
            if (v.Potential.Count > 1) parts.Add("**Potential types** " + string.Join(", ", v.Potential.Select(t => $"`{t}`")) + (v.Kind == "var" ? " — it is assigned values of these types." : "."));
            parts.Add(v.Kind == "const" ? "constant" : v.Kind);
            return string.Join("\n\n", parts);
        }

        return null;
    }

    private static string? LanguageHover(AmpleDocument doc, string raw, int col, string word, bool insideBlock, int line)
    {
        if (VariableHover(doc, line, col, word) is { } variable) return variable;
        // a quantity such as 2bars, 250ms or 7st is one word to the lexer: the unit is what is explained
        if (word.Length > 1 && char.IsDigit(word[0]))
        {
            int k = 0;
            while (k < word.Length && (char.IsDigit(word[k]) || word[k] == '.')) k++;
            if (k < word.Length && NoteCatalog.Builtins.TryGetValue(word[k..], out NoteSignature? unit) && unit.Kind == "unit") return $"`{unit.Id}` — unit\n\n{unit.Doc}\n\nBuilt in.";
        }

        if (doc.Helpers.FirstOrDefault(h => string.Equals(h.Name, word, StringComparison.OrdinalIgnoreCase)) is { } helper)
        {
            string kind = helper.Kind switch { "const" => "constant", "type" => "type alias", _ => helper.Effect == "dynamic" ? "dynamic helper: its result can differ between builds until it is baked" : "helper (pure)" };
            string deps = helper.Dependencies.Count == 0 ? string.Empty : "\n\nDepends on: " + string.Join(", ", helper.Dependencies) + ".";
            string baked = helper.Baked ? "\n\nBaked: every call uses the baked value; the body stays so that it can be made dynamic again." : string.Empty;
            doc.Docs.TryGetValue(helper.Line, out DocComment? comment);
            string parameters = helper.Kind != "helper" || helper.Params.Count == 0 ? string.Empty : "\n\n" + string.Join('\n', helper.Params.Select(p => $"- `{p.Type} {p.Name}`" + (p.Default is null ? string.Empty : $" (default `{p.Default}`)") + DocNote(comment, p)));
            string described = comment is null ? string.Empty : comment.Markdown(helper.Kind == "helper" ? helper.Returns : null);
            string? inferred = helper.Kind == "helper" && helper.Returns is null ? doc.HelperTable.InferredReturn(helper.Name) : null;
            string returns = helper.Kind != "helper" || comment?.Returns is not null ? string.Empty : helper.Returns is not null ? $"\n\nReturns `{helper.Returns}`." : inferred is not null ? $"\n\nReturns `{inferred}` — worked out from what the body returns." : string.Empty;
            return $"`{(helper.Kind == "helper" ? HelperText(helper, inferred) : helper.Name + (helper.Returns is null ? string.Empty : ": " + helper.Returns))}`" + (described.Length == 0 ? string.Empty : "\n\n" + described) + parameters + returns + $"\n\n{kind}{deps}{baked}";
        }

        int end = raw.IndexOf(word, Math.Clamp(col - 1 - word.Length, 0, raw.Length), StringComparison.Ordinal);
        string after = end < 0 ? string.Empty : raw[(end + word.Length)..].TrimStart();
        if (VolatileNames.Contains(word, StringComparer.OrdinalIgnoreCase))
        {
            string capability = word.Equals("osRandom", StringComparison.OrdinalIgnoreCase) ? "os-random" : word.Equals("environment", StringComparison.OrdinalIgnoreCase) ? "environment" : "clock";
            CapabilityState? state = doc.Capabilities.FirstOrDefault(c => c.Name == capability);
            string status = state is null ? string.Empty : state.Effective ? "enabled" : "**" + state.Reason + "**";
            string signature = word.Equals("environment", StringComparison.OrdinalIgnoreCase) ? "environment(String name) -> String" : word.Equals("osRandom", StringComparison.OrdinalIgnoreCase) ? "osRandom() -> Int" : word + "() -> a date and time (year, month, day, hour, minute, second, weekday)";
            return $"`{signature}`\n\nVolatile: its value can differ between builds, so a helper that uses it is a dynamic helper. Needs the capability `{capability}`: {status}.";
        }

        if (after.StartsWith('(') && NoteCatalog.Signatures.TryGetValue(word, out NoteSignature? sig))
        {
            return SignatureMarkdown(sig, sig.Receiver is null ? string.Empty : $"{sig.Receiver} |> ");
        }

        // the other built-ins of the language layer: functions (Round, min ...), methods (seed.fork), units (2bars) and the words of a helper body (for, emit ...)
        string before = end <= 0 ? string.Empty : raw[..end];
        if (NoteCatalog.Builtins.TryGetValue(word, out NoteSignature? builtin))
        {
            switch (builtin.Kind)
            {
                case "function" when after.StartsWith('('):
                    return SignatureMarkdown(builtin, string.Empty);
                case "method" when after.StartsWith('(') && before.EndsWith('.'):
                    return SignatureMarkdown(builtin, builtin.Receiver is null ? string.Empty : $"{builtin.Receiver}.");
                case "unit" when before.Length > 0 && (char.IsDigit(before[^1]) || before[^1] == '.'):
                    return $"`{builtin.Id}` — unit\n\n{builtin.Doc}\n\nBuilt in.";
                case "keyword" when !insideBlock && (before.Trim().Length == 0 || before.TrimEnd().EndsWith('{') || before.TrimEnd().EndsWith(';') || before.TrimEnd().EndsWith("dynamic", StringComparison.Ordinal)):
                    return $"`{builtin.Id}` — keyword\n\n{builtin.Doc}\n\nBuilt in.";
            }
        }

        return null;
    }

    /// <summary>The note of a parameter from the doc comment: what it means, and the type the comment gives when it is not the declared one (a union, for example).</summary>
    private static string DocNote(DocComment? comment, HelperParamInfo p)
    {
        string text = comment?.ParamText(p.Name) is { Length: > 0 } t ? " — " + t : string.Empty;
        return comment?.ParamType(p.Name) is { } documented && !string.Equals(documented.Replace(" ", string.Empty, StringComparison.Ordinal), p.Type.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase) ? text + $" *(documented as `{documented}`)*" : text;
    }

    private static string SignatureMarkdown(NoteSignature sig, string receiver)
    {
        string typeOf(NoteParam p) => p.Type == "Range" && p.Of is not null ? $"Range<{p.Of}>" : p.Type;
        string parameters = sig.Params.Count == 0 ? string.Empty : "\n\n" + string.Join('\n', sig.Params.Select(p => $"- `{typeOf(p)} {p.Name}`" + (p.Default is null ? string.Empty : $" (default `{p.Default}`)") + (p.Doc is { Length: > 0 } d ? " — " + d : string.Empty)));
        return $"`{receiver}{Signature(sig.Id, sig.Params.Select(p => typeOf(p) + " " + p.Name + (p.Default is null ? string.Empty : " = " + p.Default)), sig.Returns)}`" + (string.IsNullOrEmpty(sig.Doc) ? string.Empty : "\n\n" + sig.Doc) + parameters + "\n\nBuilt in.";
    }

    /// <summary>The signature of the call open at the end of the text (the text before the cursor): label, parameter labels, the active parameter and documentation, or null when the cursor is not inside a known call.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="before">The text of the document up to the cursor.</param>
    /// <returns>The signature, or null.</returns>
    public static (string Label, IReadOnlyList<string> Parameters, int Active, string Documentation)? SignatureAt(AmpleDocument doc, string before)
    {
        List<(string Name, int ArgStart, int Index, bool Piped)> stack = [];
        bool inString = false;
        for (int i = 0; i < before.Length; i++)
        {
            char c = before[i];
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '#' && !(i > 0 && char.IsLetterOrDigit(before[i - 1]))) { int eol = before.IndexOf('\n', i); if (eol < 0) return null; i = eol; continue; }
            if (c == '(')
            {
                int s = i;
                while (s > 0 && (char.IsLetterOrDigit(before[s - 1]) || before[s - 1] is '_' or '-')) s--;
                stack.Add((before[s..i], i + 1, 0, before[..s].TrimEnd().EndsWith("|>", StringComparison.Ordinal)));
            }
            else if (c == ')' && stack.Count > 0) stack.RemoveAt(stack.Count - 1);
            else if (c == ',' && stack.Count > 0) stack[^1] = (stack[^1].Name, i + 1, stack[^1].Index + 1, stack[^1].Piped);
        }

        if (stack.Count == 0 || stack[^1].Name.Length == 0) return null;
        (string name, int argStart, int index, bool piped) = stack[^1];
        System.Text.RegularExpressions.Match named = System.Text.RegularExpressions.Regex.Match(before[argStart..], @"^\s*([A-Za-z][\w-]*)\s*=", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        if (doc.Helpers.FirstOrDefault(h => h.Kind == "helper" && string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)) is { } helper)
        {
            List<string> helperLabels = [.. helper.Params.Select(p => $"{p.Type} {p.Name}" + (p.Default is null ? string.Empty : " = " + p.Default))];
            int skip = piped && helper.Params.Count > 0 && helper.Params[0].Type == "Notes" ? 1 : 0;
            int active = named.Success ? Math.Max(0, helper.Params.ToList().FindIndex(p => p.Name == named.Groups[1].Value)) : Math.Min(Math.Max(0, helperLabels.Count - 1), index + skip);
            return (Signature(helper.Name, helperLabels, helper.Returns), helperLabels, active, helper.Effect == "dynamic" ? "dynamic helper" : "helper");
        }

        if (!NoteCatalog.Signatures.TryGetValue(name, out NoteSignature? sig)) return null;
        List<string> labels = [.. sig.Params.Select(p => (p.Type == "Range" && p.Of is not null ? $"Range<{p.Of}>" : p.Type) + " " + p.Name + (p.Default is null ? string.Empty : " = " + p.Default))];
        int at = named.Success ? Math.Max(0, sig.Params.ToList().FindIndex(p => p.Name == named.Groups[1].Value)) : Math.Min(Math.Max(0, labels.Count - 1), index);
        return (Signature(sig.Id, labels, sig.Returns), labels, at, sig.Doc ?? string.Empty);
    }

    /// <summary>Finds the header and every <c>from</c> reference of the block at the position, or the declaration and the calls of the helper.</summary>
    public static IReadOnlyList<Location> FindReferences(AmpleDocument doc, int line, int col)
    {
        BlockNode? target = BlockAt(doc, line, col);
        if (target is null)
        {
            return HelperAt(doc, line, col) is { } h ? HelperOccurrences(doc, h) : [];
        }

        List<Location> found = [NameLocation(doc, target)];
        foreach (BlockNode b in doc.Blocks)
        {
            foreach (FromLine f in b.Froms.Where(x => x.Block == target.Name))
            {
                int at = doc.Lines[f.Line].IndexOf(f.RawBlock, doc.Lines[f.Line].IndexOf("from", StringComparison.Ordinal) + 4, StringComparison.Ordinal);
                found.Add(new Location(f.Line + 1, at + 1, at + 1 + f.RawBlock.Length));
            }
        }

        return found;
    }

    /// <summary>Gives hover text for a block type, block name or parameter name at the position, or null.</summary>
    public static string? HoverAt(AmpleDocument doc, SpecData spec, int line, int col)
    {
        spec = doc.Spec ?? spec;   // the templates of the components of this song are block types too
        string raw = Line(doc, line);
        string? word = WordAt(raw, col);
        if (word is null)
        {
            return null;
        }

        if (ComponentHover(doc, spec, line - 1, word) is { } componentText)
        {
            return componentText;
        }

        BlockNode? header = doc.Blocks.FirstOrDefault(b => b.Line == line - 1);
        if (header is null && LanguageHover(doc, raw, col, word, doc.Blocks.Any(b => b.Line < line - 1 && (b.CloseLine < 0 || b.CloseLine >= line - 1)), line) is { } languageText)
        {
            return languageText;
        }

        if (header is not null && header.Type == word && spec.Templates.TryGetValue(word, out TemplateSpec? ht))
        {
            return Describe(ht);
        }

        // the doc comment above a block says what it is for: shown when its name is hovered
        if (header is not null && header.Name == word && doc.Docs.TryGetValue(header.Line, out DocComment? blockDoc))
        {
            return $"`{header.Type} {header.Name}`\n\n{blockDoc.Markdown()}";
        }

        BlockNode? owner = doc.Blocks.FirstOrDefault(b => b.Line < line - 1 && (b.CloseLine < 0 || b.CloseLine >= line - 1));
        if (owner is not null && spec.Templates.TryGetValue(owner.Type, out TemplateSpec? ot) && ot.Parameters.TryGetValue(word, out string? def) && raw.TrimStart().StartsWith(word, StringComparison.Ordinal))
        {
            return $"{word}: parameter of {owner.Type}, default {def}.";
        }

        BlockNode? block = BlockAt(doc, line, col);
        if (block is not null && spec.Templates.TryGetValue(block.Type, out TemplateSpec? bt))
        {
            return $"{block.Name}: {Describe(bt)}" + (doc.Docs.TryGetValue(block.Line, out DocComment? aboveBlock) ? "\n\n" + aboveBlock.Markdown() : string.Empty);
        }

        return null;
    }

    /// <summary>Proposes completions at the position.</summary>
    /// <param name="doc">The parsed song.</param>
    /// <param name="spec">The language spec.</param>
    /// <param name="line">The 1-based line.</param>
    /// <param name="col">The 1-based column.</param>
    /// <param name="settingHints">What the installed plugins accept in a <c>settings</c> line; null or empty when it is not known.</param>
    public static IReadOnlyList<CompletionItem> CompletionAt(AmpleDocument doc, SpecData spec, int line, int col, IReadOnlyList<SettingHint>? settingHints = null)
    {
        spec = doc.Spec ?? spec;
        string raw = Line(doc, line);
        string before = raw[..Math.Clamp(col - 1, 0, raw.Length)];
        string trimmed = before.TrimStart();
        if (ComponentCompletions(doc, spec, line - 1, trimmed) is { } inComponent)
        {
            return inComponent;
        }

        if (trimmed.StartsWith("settings", StringComparison.Ordinal) && (trimmed.Length == 8 || trimmed[8] is ' ' or '{' or '\t'))
        {
            return SettingsLineCompletions(trimmed, settingHints ?? []);
        }

        BlockNode? owner = doc.Blocks.FirstOrDefault(b => b.Line < line - 1 && (b.CloseLine < 0 || b.CloseLine >= line - 1));
        if (owner is null && InViewBlock(doc, line - 1) is { } viewState)
        {
            return ViewBlockCompletions(doc, trimmed, before.EndsWith(' '), viewState.InGroup);
        }

        if (owner is null)
        {
            if (trimmed.StartsWith('#'))
            {
                return ViewLineCompletions(doc, trimmed);
            }

            if (trimmed.Contains(' ', StringComparison.Ordinal))
            {
                string[] words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                bool atEnd = before.EndsWith(' ');
                if (words.Length >= 3 && words[2].Contains('@', StringComparison.Ordinal) && (atEnd || words.Length > 3))
                {
                    return [new("at", "keyword", "Where the block starts, in bars."), new("for", "keyword", "How long the block lasts, in bars.")];
                }

                return words.Length == 2 && !atEnd ? [] : words.Length == 2 ? [new("at", "keyword", "Where the block starts, in bars."), new("for", "keyword", "How long the block lasts, in bars."), new("pinned@", "class", "Keeps this version of the block (major.minor); leave it out to follow the definition.")] : [new("at", "keyword", "Where the block starts, in bars."), new("for", "keyword", "How long the block lasts, in bars.")];
            }

            return
            [
                .. spec.Templates.Values.Select(t => new CompletionItem(t.Id, "block-type", t.Title)),
                new("# view", "keyword", "Layout lines kept in the song: groups, sizes and pins."),
                new("composition", "keyword", "The song line: composition \"name\" { tempo N  master-seed N }"),
                new("settings", "keyword", "Settings of a plugin for this song: settings { plugin.name value }"),
                new("features", "keyword", "Module switches"),
                new("lint", "keyword", "Lint levels"),
                new("layout", "keyword", "Node editor position"),
                new("view", "keyword", "The groups, block sizes and pinned blocks of the canvas: view { group … size … pin … }"),
            ];
        }

        if (!spec.Templates.TryGetValue(owner.Type, out TemplateSpec? t2))
        {
            return [];
        }

        // the inputs of this block that have no source yet (an input takes one source)
        HashSet<string> taken = new(owner.Froms.Where(f => !f.Disabled && f.Line != line - 1).Select(f => f.Input), StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<PortSpec> freeInputs = [.. t2.Inputs.Where(p => !taken.Contains(p.Name))];
        if (trimmed == "-")
        {
            // a dash on a line of its own may be the start of an arrow or of a number: Ctrl+Space then offers the connections that fit (an output of another block into a free input of this one)
            List<CompletionItem> lanes = [];
            foreach (BlockNode other in doc.Blocks.Where(b => !b.Skipped && !ReferenceEquals(b, owner) && spec.Templates.ContainsKey(b.Type)).OrderBy(b => b.Line < owner.Line ? 0 : 1).ThenByDescending(b => b.Line < owner.Line ? b.Line : -b.Line))
            {
                foreach (PortSpec output in spec.Templates[other.Type].Outputs)
                {
                    foreach (PortSpec input in freeInputs.Where(p => p.Type == output.Type))
                    {
                        string connection = "from " + Edits.Quote(other.Name) + "." + output.Name + " -> " + input.Name;
                        lanes.Add(new CompletionItem(connection, "lane", output.Type + ": " + other.Type + " into " + input.Name, connection));
                    }
                }
            }

            return [.. lanes.Take(30)];
        }

        if (trimmed.StartsWith("from ", StringComparison.Ordinal))
        {
            string rest = trimmed[5..];
            if (rest.Contains("->", StringComparison.Ordinal))
            {
                // the inputs this output can feed: the free ones of the same type (all free ones when the output is not known yet)
                string? type = OutputType(doc, spec, rest[..rest.IndexOf("->", StringComparison.Ordinal)]);
                string gap = rest.TrimEnd().EndsWith("->", StringComparison.Ordinal) ? " " : string.Empty;
                return [.. freeInputs.Where(p => type is null || p.Type == type).Select(p => new CompletionItem(p.Name, "input", p.Type + " input", gap + p.Name))];
            }

            if (rest.TrimEnd().EndsWith('-') && rest.Contains('.', StringComparison.Ordinal))
            {
                // a dash after the output: the arrow with each input that fits
                string? type = OutputType(doc, spec, rest.TrimEnd()[..^1]);
                return [.. freeInputs.Where(p => type is null || p.Type == type).Select(p => new CompletionItem("-> " + p.Name, "lane", p.Type + " input", "-> " + p.Name))];
            }

            int dot = rest.IndexOf('.', StringComparison.Ordinal);
            if (dot > 0)
            {
                string blockName = Parser.Unquote(rest[..dot]);
                BlockNode? src = doc.Blocks.FirstOrDefault(b => b.Name == blockName);
                return src is not null && spec.Templates.TryGetValue(src.Type, out TemplateSpec? st) ? [.. st.Outputs.Select(p => new CompletionItem(p.Name, "output", p.Type))] : [];
            }

            return [.. doc.Blocks.Where(b => !b.Skipped && !ReferenceEquals(b, owner)).Select(b => new CompletionItem(Edits.Quote(b.Name), "block", b.Type))];
        }

        if (trimmed.Length == 0 || !trimmed.Contains(' ', StringComparison.Ordinal))
        {
            return
            [
                new("from", "keyword", "Connect an output to an input"),
                .. t2.Parameters.Where(p => owner.Parameters.All(x => x.Name != p.Key)).Select(p => new CompletionItem(p.Key, "parameter", "default " + p.Value)),
            ];
        }

        // the value of a parameter that has a list of choices (a combo, a toggle): "target |"
        string[] parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (owner.Type == "midi-clip" && parts.Length == 2 && parts[0] == "notes")
        {
            string entry = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim('"') ?? string.Empty;
            int separator = entry.LastIndexOf(';');
            if (separator >= 0)
            {
                string prefix = entry[(separator + 1)..];
                if (!prefix.Contains('=', StringComparison.Ordinal))
                {
                    return [.. new[] { ("pan=", "Pan -100..100"), ("fine=", "Fine pitch -100..100 cents"), ("rel=", "Release 0..5000 ms"), ("cut=", "Cutoff 0..100%"), ("slide=", "Slide 0 or 1"), ("port=", "Portamento 0..2000 ms"), ("tie=", "Tie identity 0..2000000 (inherited instrument routing)") }
                        .Where(item => item.Item1.StartsWith(prefix, StringComparison.Ordinal))
                        .Select(item => new CompletionItem(item.Item1, "value", item.Item2))];
                }
            }
        }
        if (parts.Length == 2 && parts[0] == "file" && (owner.Type is "gm" or "midi-render") && parts[1].StartsWith("pack:", StringComparison.Ordinal))
        {
            const string pack = "musescore-general";
            string prefix = parts[1]["pack:".Length..];
            return pack.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? [new(pack, "value", "General MIDI SoundFont pack; keep the pack: prefix.")] : [];
        }
        if (parts.Length >= 1 && t2.ParameterSpecs.TryGetValue(parts[0], out TemplateParameterSpec? ps) && ps.Options is { Count: > 0 })
        {
            return [.. ps.Options.Select(o => new CompletionItem(o.Contains(' ', StringComparison.Ordinal) ? "\"" + o + "\"" : o, "value", ps.Documentation ?? ps.Label))];
        }

        return [];
    }

    /// <summary>The type of the output a from line names ("beat.audio"), or null when the block or the output is not known.</summary>
    private static string? OutputType(AmpleDocument doc, SpecData spec, string source)
    {
        string text = source.Trim();
        int dot = text.LastIndexOf('.');
        if (dot <= 0)
        {
            return null;
        }

        BlockNode? block = doc.Blocks.FirstOrDefault(b => b.Name == Parser.Unquote(text[..dot]));
        return block is not null && spec.Templates.TryGetValue(block.Type, out TemplateSpec? template) ? template.Outputs.FirstOrDefault(p => p.Name == text[(dot + 1)..].Trim())?.Type : null;
    }

    /// <summary>The names and values inside <c>settings { name value name value }</c>: names the plugins accept and are not stated yet, then the values of the name just typed.</summary>
    private static IReadOnlyList<CompletionItem> SettingsLineCompletions(string trimmed, IReadOnlyList<SettingHint> hints)
    {
        int open = trimmed.IndexOf('{', StringComparison.Ordinal);
        if (open < 0)
        {
            return [new("{ ", "keyword", "Start the settings: settings { plugin.name value }")];
        }

        string inside = trimmed[(open + 1)..];
        (List<string> words, bool inQuote, bool closed) = SettingsWords(inside);
        if (closed)
        {
            return [];
        }

        bool atEnd = !inQuote && (inside.Length == 0 || char.IsWhiteSpace(inside[^1]));
        bool typingValue = atEnd ? words.Count % 2 == 1 : words.Count % 2 == 0;
        if (typingValue && words.Count > 0)
        {
            string key = words[atEnd ? ^1 : ^2];
            SettingHint? hint = hints.FirstOrDefault(h => h.Name == key);
            if (hint is null)
            {
                return [];
            }

            return [.. hint.Options.Select(o => new CompletionItem(o.Contains(' ', StringComparison.Ordinal) ? "\"" + o + "\"" : o, "value", hint.Detail))];
        }

        HashSet<string> stated = [.. words.Where((_, i) => i % 2 == 0)];
        return [.. hints.Where(h => !stated.Contains(h.Name)).Select(h => new CompletionItem(h.Name, "parameter", h.Default.Length > 0 ? $"{h.Detail} (default {h.Default})" : h.Detail))];
    }

    /// <summary>Splits the text after <c>settings {</c> into words, keeping a quoted value with spaces as one word; reports an open quote and whether the closing brace was passed.</summary>
    private static (List<string> Words, bool InQuote, bool Closed) SettingsWords(string inside)
    {
        List<string> words = [];
        System.Text.StringBuilder current = new();
        bool quoted = false, has = false;
        foreach (char ch in inside)
        {
            if (quoted)
            {
                if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(ch);
                }
            }
            else if (ch == '"')
            {
                quoted = true;
                has = true;
            }
            else if (ch == '}')
            {
                if (has)
                {
                    words.Add(current.ToString());
                }

                return (words, false, true);
            }
            else if (ch is ' ' or '\t')
            {
                if (has)
                {
                    words.Add(current.ToString());
                    current.Clear();
                    has = false;
                }
            }
            else
            {
                current.Append(ch);
                has = true;
            }
        }

        if (has)
        {
            words.Add(current.ToString());
        }

        return (words, quoted, false);
    }

    /// <summary>Says whether a line (0-based) is inside the <c>view { }</c> block at the end of the song, and whether inside one of its groups.</summary>
    private static (bool InGroup, bool Unused)? InViewBlock(AmpleDocument doc, int index)
    {
        bool inView = false, inGroup = false;
        for (int i = 0; i < doc.Lines.Count && i < index; i++)
        {
            string t = doc.Lines[i].Trim();
            if (!inView)
            {
                inView = t == "view {" && doc.Blocks.All(b => i < b.Line || i > b.CloseLine);
            }
            else if (t == "}")
            {
                if (inGroup)
                {
                    inGroup = false;
                }
                else
                {
                    inView = false;
                }
            }
            else if (!inGroup && t.StartsWith("group ", StringComparison.Ordinal) && t.EndsWith('{'))
            {
                inGroup = true;
            }
        }

        return inView ? (inGroup, false) : null;
    }

    private static IReadOnlyList<CompletionItem> ViewBlockCompletions(AmpleDocument doc, string trimmed, bool space, bool inGroup)
    {
        string[] words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        IReadOnlyList<CompletionItem> blocks = [.. doc.Blocks.Where(b => !b.Skipped).Select(b => new CompletionItem(Edits.Quote(b.Name), "block", b.Type))];
        if (words.Length == 0 || (words.Length == 1 && !space))
        {
            return inGroup
                ? [new("title", "keyword", "The name shown on the group."), new("member", "keyword", "A block in the group: member <block>"), new("x", "keyword", "The place of the group."), new("y", "keyword", "The place of the group."), new("label", "keyword", "label <key> <text>")]
                :
                [
                    new("section", "keyword", "A named stretch of bars: section \"Drop\" at 33 for 32 (bars 1 to 256, no overlap)."),
                    new("line", "keyword", "A name for the line that holds a block: line <block> \"Name\""),
                    new("order", "keyword", "The order of the lines, by a block of each: order <block> ..."),
                    new("group", "keyword", "A group of blocks: group <id> {"),
                    new("size", "keyword", "The scale of a block: size <block> <scale>"),
                    new("pin", "keyword", "Keeps a block in place: pin <block>"),
                ];
        }

        if (inGroup)
        {
            return words[0] == "member" && (words.Length == 1 || (words.Length == 2 && !space)) ? blocks : [];
        }

        return words[0] switch
        {
            "line" or "size" or "pin" when words.Length == 1 || (words.Length == 2 && !space) => blocks,
            "order" => blocks,
            "section" when words.Length == 2 && space => [new("at", "keyword", "The first bar of the section.")],
            "section" when words.Length == 4 && space => [new("for", "keyword", "How many bars the section lasts.")],
            _ => [],
        };
    }

    /// <summary>The comment lines the studio keeps in the song: <c># view group</c>, <c># view size</c> and <c># view pin</c>.</summary>
    private static IReadOnlyList<CompletionItem> ViewLineCompletions(AmpleDocument doc, string trimmed)
    {
        string[] words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool space = trimmed.EndsWith(' ');
        if (words.Length == 1 || (words.Length == 2 && !space))
        {
            return words.Length == 1 && space || words.Length == 2 && !"view".StartsWith(words[1], StringComparison.Ordinal) ? [] : [new("view", "keyword", "Layout lines kept in the song: groups, sizes and pins.")];
        }

        if (words[1] != "view")
        {
            return [];
        }

        if (words.Length == 2 || (words.Length == 3 && !space))
        {
            return [new("group", "keyword", "A group of blocks: # view group {json}"), new("size", "keyword", "The scale of a block: # view size <block> <scale>"), new("pin", "keyword", "Keeps a block in place: # view pin <block>")];
        }

        return words[2] is "size" or "pin" && (words.Length == 3 || (words.Length == 4 && !space))
            ? [.. doc.Blocks.Where(b => !b.Skipped).Select(b => new CompletionItem(Edits.Quote(b.Name), "block", b.Type))]
            : [];
    }

    /// <summary>What the hover says inside a declaration of a component: its contract on the header line, a port or a property on its own line.</summary>
    private static string? ComponentHover(AmpleDocument doc, SpecData spec, int line0, string word)
    {
        ComponentDefinition? d = doc.Components.FirstOrDefault(c => line0 >= c.StartLine && line0 <= c.EndLine);
        if (d is null) return null;
        if (line0 == d.HeaderLine) return ComponentSummary(doc, spec, d);
        if (d.IsAudio && d.ProcessLine >= 0 && line0 > d.ProcessLine && AudioProcessors.Catalog(spec).TryGetValue(word, out AudioProcessor? processor)) return "**" + word + "**: " + AudioSignature(processor);
        if (d.Properties.FirstOrDefault(p => p.Line == line0 && p.Name == word) is { } property)
        {
            string constraints = string.Join(", ", property.Attributes.Where(a => !ComponentAttributes.Presentation.Contains(a.Name)).Select(a => a.Name + "(" + string.Join(", ", a.Args) + ")"));
            return $"`property {property.Name}: {property.Type}{(property.Default is null ? string.Empty : " = " + property.Default)}`" + (constraints.Length > 0 ? "\n\n" + constraints : string.Empty) + (property.Doc is { Summary.Length: > 0 } pd ? "\n\n" + pd.Summary : string.Empty) + "\n\nproperty";
        }

        ComponentPort? port = d.Inputs.Concat(d.Outputs).FirstOrDefault(p => p.Line == line0 && p.Name == word);
        if (port is not null)
        {
            bool input = d.Inputs.Contains(port);
            return $"`{(input ? "input" : "output")} {port.Name}: {port.Type}`" + (input && port.Optional ? "\n\nOptional" + (port.Fallback is null ? "." : $": when nothing is connected the property `{port.Fallback}` stands in.") : string.Empty) + (port.Doc is { Summary.Length: > 0 } pd ? "\n\n" + pd.Summary : string.Empty) + "\n\n" + (input ? "input" : "output");
        }

        return null;
    }

    private static string ComponentSummary(AmpleDocument doc, SpecData spec, ComponentDefinition d)
    {
        SealCheck seal = ComponentHashing.Check(d, ComponentHashing.DependenciesOf(d, doc.Spec ?? spec, () => doc.HelperTable.ClosureHashes(string.Join('\n', d.ProcessBody)), [.. doc.Components, .. doc.ImportedComponents]));
        List<string> parts = [$"`component {d.DeclaredName} version {d.Version}`"];
        if (d.Doc is { Summary.Length: > 0 } summary) parts.Add(summary.Summary);
        if (d.Inputs.Count > 0) parts.Add("**Inputs**\n" + string.Join('\n', d.Inputs.Select(i => $"- `{i.Name}: {i.Type}`" + (i.Optional ? " (optional" + (i.Fallback is null ? ")" : $", fallback `{i.Fallback}`)") : string.Empty))));
        if (d.Properties.Count > 0) parts.Add("**Properties**\n" + string.Join('\n', d.Properties.Select(p => $"- `{p.Name}: {p.Type}{(p.Default is null ? string.Empty : " = " + p.Default)}`" + (p.Doc is { Summary.Length: > 0 } pd ? " — " + pd.Summary : string.Empty))));
        if (d.Outputs.Count > 0) parts.Add("**Output** " + string.Join(", ", d.Outputs.Select(o => $"`{o.Name}: {o.Type}`")));
        parts.Add($"id `{d.Id}`, semantic `{ComponentHashing.Abbreviate(seal.Semantic)}`, payload `{ComponentHashing.Abbreviate(seal.Payload)}`");
        if (seal.State == SealState.Invalid) parts.Add("**Seal void:** " + seal.Reason);
        parts.Add("component · " + seal.State.ToString().ToLowerInvariant() + (d.Deprecation is not null ? " · deprecated" : string.Empty));
        return string.Join("\n\n", parts);
    }

    /// <summary>The completions inside a component declaration: attribute names after <c>#[</c>, the statements at the start of a line, and the types after a colon. Null when the line is not inside a component.</summary>
    private static IReadOnlyList<CompletionItem>? ComponentCompletions(AmpleDocument doc, SpecData spec, int line0, string before)
    {
        ComponentDefinition? d = doc.Components.FirstOrDefault(c => line0 > c.HeaderLine && line0 < c.EndLine);
        if (d is null) return null;
        int callOpen = before.LastIndexOf("#[", StringComparison.Ordinal);
        if (callOpen >= 0 && before.IndexOf(']', callOpen) < 0) return [.. ComponentAttributes.All.Values.Select(a => new CompletionItem(a.Name, "attribute", a.Doc))];
        if (d.UiLine >= 0 && line0 > d.UiLine && line0 <= d.UiEndLine) return UiCompletions(d, before);
        if (System.Text.RegularExpressions.Regex.IsMatch(before, @"#\[Widget\(\w*$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)))
            return [.. ComponentUiCatalog.Widgets.Select(w => new CompletionItem(w.Key, "widget", w.Value))];
        if (System.Text.RegularExpressions.Regex.IsMatch(before, @"^(?:input|output|property)\s+[\w-]+\s*:\s*[\w<>]*$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)))
            return [.. NoteCatalog.Types.Values.Select(t => new CompletionItem(t.Name, "type", t.Doc ?? string.Empty)), new("Notes", "type", "Notes"), new("Audio", "type", "Audio: a lazy graph of audio processors, rendered by the build"), new("Bool", "type", "true or false")];
        if (d.IsAudio && d.ProcessLine >= 0 && line0 > d.ProcessLine)
        {
            // inside the process of an Audio component: after |> only what takes Audio, inside a call the parameters of that processor
            IReadOnlyDictionary<string, AudioProcessor> catalog = AudioProcessors.Catalog(spec);
            System.Text.RegularExpressions.Match callMatch = System.Text.RegularExpressions.Regex.Match(before, @"([A-Za-z_][\w-]*)\s*\(([^()]*)$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
            if (callMatch.Success && catalog.TryGetValue(callMatch.Groups[1].Value, out AudioProcessor? inside))
                return [.. inside.Parameters.Select(p => new CompletionItem(p.Name + "=", "parameter", p.Type + (p.Unit.Length > 0 ? " (" + p.Unit + ")" : string.Empty) + ", default " + p.Default)), .. inside.ExtraInputs.Select(i => new CompletionItem(i + "=", "input", "an Audio input of " + inside.Name))];
            if (System.Text.RegularExpressions.Regex.IsMatch(before, @"\|>\s*[\w-]*$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)))
                return [.. catalog.Values.Select(p => new CompletionItem(p.Name, "processor", AudioSignature(p))), new("mix", "processor", "mix(a, b, ...): sums audio values (a bus block)")];
            if (before.Trim().All(char.IsLetter))
                return [new("let", "keyword", "let name = audio |> processor(...)"), new("emit", "keyword", "emit audio |> processor(...)  (several outputs: emit ... -> name)"), .. d.Inputs.Where(i => i.Type.TrimEnd('?') == "Audio").Select(i => new CompletionItem(i.Name, "input", "Audio input")), new("mix", "processor", "mix(a, b, ...)")];
        }

        if (before.Trim().Length > 0 && !before.Trim().All(char.IsLetter)) return null;
        return
        [
            new("input", "keyword", "input <name>: Notes { optional true  fallback <property> }"),
            new("output", "keyword", "output <name>: Notes"),
            new("property", "keyword", "property <name>: <Type> = <default>"),
            new("process", "keyword", "process { ... }: what the component makes (a helper body; emit the notes)"),
            new("requires", "keyword", "requires clock: the capabilities the process needs"),
            new("ui", "keyword", "ui { ... }: the layout of the property panel"),
            new("deprecated", "keyword", "deprecated { message \"...\"  replacement <component@version> }"),
            new("migrate", "keyword", "migrate <from> -> <to> { impact none ... }"),
            new("id", "keyword", "id \"com.you.name\": the canonical identity"),
            new("current", "keyword", "this version is the default of its component"),
        ];
    }

    /// <summary>The completions inside the ui block of a component: widgets and containers at the start of an item, the properties after a widget, the sizes after size.</summary>
    private static IReadOnlyList<CompletionItem> UiCompletions(ComponentDefinition d, string before)
    {
        string[] words = before.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        bool atWord = before.Length == 0 || !char.IsWhiteSpace(before[^1]);
        string previous = words.Length >= (atWord ? 2 : 1) ? words[atWord ? ^2 : ^1] : string.Empty;
        if (previous == "size") return [.. ComponentUiCatalog.Sizes.Order(StringComparer.Ordinal).Select(s => new CompletionItem(s, "size", "small, medium or large"))];
        if (previous == "preview") return [new("live", "keyword", "the sound changes while a knob moves (advisory, in the browser)"), new("off", "keyword", "no live preview")];
        if (previous == "x" || previous == "y" || previous == "=") return [.. d.Properties.Select(p => new CompletionItem(p.Name, "property", p.Type))];
        if (ComponentUiCatalog.Widgets.TryGetValue(previous, out _) && previous != "xy")
            return previous == "meter" ? [.. d.Outputs.Select(o => new CompletionItem(o.Name, "output", o.Type))] : [.. d.Properties.Select(p => new CompletionItem(p.Name, "property", p.Type + (p.Default is null ? string.Empty : " = " + p.Default)))];
        if (previous == "xy") return [new("x=", "keyword", "the property of the horizontal axis"), new("y=", "keyword", "the property of the vertical axis")];
        return
        [
            .. ComponentUiCatalog.Widgets.Select(w => new CompletionItem(w.Key, "widget", w.Value)),
            .. ComponentUiCatalog.Containers.Select(c => new CompletionItem(c.Key, "container", c.Value)),
            new("preview", "keyword", "preview live: ask for the live preview"),
            new("size", "keyword", "size s|m|l"),
            new("label", "keyword", "label \"text\""),
        ];
    }

    /// <summary>The call of an audio processor as a signature: <c>delay(time: Duration, feedback: Percent, ...)</c>.</summary>
    /// <param name="p">The processor.</param>
    /// <returns>The signature with its capabilities.</returns>
    public static string AudioSignature(AudioProcessor p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return $"Audio |> {p.Name}({string.Join(", ", p.Parameters.Take(6).Select(x => x.Name + ": " + x.Type))}{(p.Parameters.Count > 6 ? ", ..." : string.Empty)}) -> Audio. Block {p.Template}@{p.Version}; browser preview: {(p.ClientPreview ? "yes" : "no")}{(p.LatencyMs > 0 ? $"; latency {p.LatencyMs} ms" : string.Empty)}.";
    }

    private static string Describe(TemplateSpec t) =>
        $"{t.Title} ({t.Id}, version {t.Version}). {t.Description} Inputs: {(t.Inputs.Count == 0 ? "none" : string.Join(", ", t.Inputs.Select(p => $"{PortNames.Written(p)}:{p.Type}").Distinct()))}. Outputs: {(t.Outputs.Count == 0 ? "none" : string.Join(", ", t.Outputs.Select(p => $"{PortNames.Written(p)}:{p.Type}").Distinct()))}.";

    private static string Line(AmpleDocument doc, int line) => line >= 1 && line <= doc.Lines.Count ? doc.Lines[line - 1] : string.Empty;

    private static Location NameLocation(AmpleDocument doc, BlockNode b)
    {
        string raw = doc.Lines[b.Line];
        int typeEnd = raw.IndexOf(b.Type, StringComparison.Ordinal) + b.Type.Length;
        int at = raw.IndexOf(b.RawName, Math.Max(typeEnd, 0), StringComparison.Ordinal);
        return new Location(b.Line + 1, at + 1, at + 1 + b.RawName.Length);
    }

    private static BlockNode? BlockAt(AmpleDocument doc, int line, int col)
    {
        foreach (BlockNode b in doc.Blocks.Where(x => !x.Skipped))
        {
            Location header = NameLocation(doc, b);
            if (header.Line == line && col >= header.Col && col <= header.EndCol)
            {
                return b;
            }

            foreach (FromLine f in doc.Blocks.SelectMany(x => x.Froms).Where(f => f.Line == line - 1 && f.Block == b.Name))
            {
                int at = doc.Lines[f.Line].IndexOf(f.RawBlock, doc.Lines[f.Line].IndexOf("from", StringComparison.Ordinal) + 4, StringComparison.Ordinal);
                if (col >= at + 1 && col <= at + 1 + f.RawBlock.Length)
                {
                    return b;
                }
            }
        }

        return null;
    }

    private static string? WordAt(string raw, int col)
    {
        int i = Math.Clamp(col - 1, 0, raw.Length);
        int start = i;
        while (start > 0 && IsWord(raw[start - 1]))
        {
            start--;
        }

        int end = i;
        while (end < raw.Length && IsWord(raw[end]))
        {
            end++;
        }

        return end > start ? raw[start..end] : null;
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c is '_' or '-';
}
