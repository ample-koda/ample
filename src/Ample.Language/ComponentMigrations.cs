using System.Globalization;
using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>How much a migration can change what a song sounds like. Anything above <see cref="Equivalent"/> is never applied without an acknowledgement.</summary>
public enum MigrationImpact
{
    /// <summary>Nothing audible changes and nothing is rewritten that carried a value.</summary>
    None = 0,

    /// <summary>The notes stay the same; the properties are written differently.</summary>
    Equivalent = 1,

    /// <summary>The notes can change.</summary>
    MusicalChange = 2,

    /// <summary>Something is lost (a property without a place in the new version).</summary>
    Destructive = 3,
}

/// <summary>One thing a migration does to an instance.</summary>
/// <param name="Kind">rename, copy, set, drop, drop-unmapped, port or version.</param>
/// <param name="Target">The property or port (or the block type for a version change).</param>
/// <param name="Before">The value before, or null.</param>
/// <param name="After">The value after, or null.</param>
public sealed record MigrationChange(string Kind, string Target, string? Before, string? After);

/// <summary>One step of a chain: from a version to the next one (it may be another component).</summary>
/// <param name="FromId">The canonical id it starts from.</param>
/// <param name="From">The version it starts from.</param>
/// <param name="ToId">The canonical id it arrives at.</param>
/// <param name="To">The version it arrives at.</param>
/// <param name="Impact">The impact the migration states (musicalChange when it does not say).</param>
/// <param name="Stated">Whether the migration states its impact.</param>
/// <param name="Message">The message of the migration, or empty.</param>
public sealed record MigrationStep(string FromId, string From, string ToId, string To, MigrationImpact Impact, bool Stated, string Message);

/// <summary>What a migration of one instance would do, said before anything is written.</summary>
/// <param name="Ok">Whether a migration could be worked out.</param>
/// <param name="Problem">Why not (AMP2221/AMP2222 wording), or empty.</param>
/// <param name="Steps">The chain of steps.</param>
/// <param name="Impact">The highest impact of the chain.</param>
/// <param name="NeedsAcknowledgement">Whether the person must acknowledge it (impact above equivalent).</param>
/// <param name="Messages">The messages of the steps.</param>
/// <param name="Changes">The changes, in order.</param>
/// <param name="Warnings">Things worth knowing that do not stop it.</param>
/// <param name="Text">The text of the song after the migration (the old text when it is not Ok).</param>
/// <param name="BlockType">The component the block is an instance of afterwards.</param>
/// <param name="BlockVersion">The version the block has afterwards.</param>
public sealed record MigrationPlan(bool Ok, string Problem, IReadOnlyList<MigrationStep> Steps, MigrationImpact Impact, bool NeedsAcknowledgement, IReadOnlyList<string> Messages, IReadOnlyList<MigrationChange> Changes, IReadOnlyList<string> Warnings, string Text, string BlockType, string BlockVersion);

/// <summary>Migrations of component instances (docs/language/19-components.md, "Deprecation and migration"): a small, pure, bounded, typed program over the properties and ports of one instance. Chains are followed and their impacts combined; a version can be replaced by another component.</summary>
public static partial class ComponentMigrations
{
    private const int MaxStatements = 64, MaxChain = 32, MaxExpression = 400, MaxDepth = 32;

    [GeneratedRegex(@"^(rename|copy|port)\s+([A-Za-z_][\w-]*(?:\.[A-Za-z_][\w-]*)?)\s*->\s*([A-Za-z_][\w-]*(?:\.[A-Za-z_][\w-]*)?)$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MoveStatement();

    [GeneratedRegex(@"^set\s+([A-Za-z_][\w-]*)\s*=\s*(.+)$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SetStatement();

    [GeneratedRegex(@"^drop\s+([A-Za-z_][\w-]*)$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DropStatement();

    [GeneratedRegex(@"^impact\s+(none|equivalent|musicalChange|destructive)$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ImpactStatement();

    [GeneratedRegex(@"^message\s+""((?:[^""\\]|\\.)*)""$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MessageStatement();

    [GeneratedRegex(@"^(\s*)(?<type>\S+)(?<mid>\s+(?:""[^""]*""|\S+))(?:\s+(?<cls>stable|pinned|live)(?:@(?<ver>[^\s{]+))?(?=\s|\{))?", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Header();

    private sealed record Node(string Id, SemVer Version);

    private sealed record Edge(ComponentDefinition Owner, ComponentMigration Migration, Node From, Node To);

    private static MigrationPlan No(string text, string problem) => new(false, problem, [], MigrationImpact.None, false, [], [], [], text, string.Empty, string.Empty);

    /// <summary>Reads a migration body into statements. A statement that cannot be read is reported with the line it is on.</summary>
    /// <param name="migration">The migration.</param>
    /// <returns>The statements as (line, kind, a, b), and the problems as (line, message).</returns>
    public static (IReadOnlyList<(int Line, string Kind, string A, string B)> Statements, IReadOnlyList<(int Line, string Message)> Problems) Read(ComponentMigration migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        List<(int, string, string, string)> statements = [];
        List<(int, string)> problems = [];
        foreach ((int line, string raw) in migration.Body)
        {
            string code = StripComment(raw).Trim().TrimEnd('}').Trim();
            if (code.Length == 0 || code == "{") continue;
            if (ImpactStatement().IsMatch(code) || MessageStatement().IsMatch(code)) continue;
            if (MoveStatement().Match(code) is { Success: true } move) statements.Add((line, move.Groups[1].Value, move.Groups[2].Value, move.Groups[3].Value));
            else if (SetStatement().Match(code) is { Success: true } set) statements.Add((line, "set", set.Groups[1].Value, set.Groups[2].Value.Trim()));
            else if (DropStatement().Match(code) is { Success: true } drop) statements.Add((line, "drop", drop.Groups[1].Value, string.Empty));
            else problems.Add((line, $"\"{code}\" is not a migration statement (rename a -> b, copy a -> b, set a = expression, drop a, port a -> b, impact none|equivalent|musicalChange|destructive, message \"...\")."));
        }

        if (statements.Count > MaxStatements) problems.Add((migration.Line, $"A migration has at most {MaxStatements} statements."));
        return (statements, problems);
    }

    private static string StripComment(string line)
    {
        bool inString = false;
        for (int k = 0; k < line.Length; k++)
        {
            if (line[k] == '"' && (k == 0 || line[k - 1] != '\\')) inString = !inString;
            else if (!inString && line[k] == '#') return line[..k];
        }

        return line;
    }

    private static MigrationImpact ImpactOf(ComponentMigration m, out bool stated)
    {
        stated = Enum.TryParse(m.Impact, true, out MigrationImpact impact) && m.Impact.Length > 0;
        return stated ? impact : MigrationImpact.MusicalChange;
    }

    private static Node Resolve(ComponentMigration m, ComponentDefinition owner, bool target)
    {
        string text = target ? m.To : m.From ?? owner.Version.ToString();
        int at = text.IndexOf('@', StringComparison.Ordinal);
        string id = at > 0 ? text[..at] : owner.Id;
        string version = at > 0 ? text[(at + 1)..] : text;
        return new Node(id, SemVer.TryParse(version, out SemVer v) ? v : new SemVer(-1, 0, 0, []));
    }

    /// <summary>Works out what migrating one instance to a version would do: the chain, the impact, the changes, and the text of the song afterwards. Nothing is written.</summary>
    /// <param name="songText">The text of the song.</param>
    /// <param name="definitions">Every component the song can see (own, embedded and imported).</param>
    /// <param name="block">The instance.</param>
    /// <param name="target">The target as <c>version</c> (the same component) or <c>id@version</c> (another one).</param>
    /// <param name="spec">The language data.</param>
    /// <returns>The plan.</returns>
    public static MigrationPlan Plan(string songText, IReadOnlyList<ComponentDefinition> definitions, string block, string target, SpecData spec)
    {
        ArgumentNullException.ThrowIfNull(songText);
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(spec);
        AmpleDocument doc = Parser.Parse(songText, spec, new ParseOptions { Components = definitions, RequireProjectLine = false });
        BlockNode? node = doc.Blocks.FirstOrDefault(b => b.Name == block);
        if (node is null) return No(songText, $"There is no block \"{block}\".");
        List<ComponentDefinition> family = [.. doc.Components.Concat(definitions).Where(d => d.Name == node.Type).DistinctBy(d => (d.Id, d.Version.ToString()))];
        ComponentDefinition? current = family.Count == 0 ? null : VersionRequest.TryParse(node.Version, out VersionRequest request) ? ComponentVersions.Pick(family, request) : ComponentVersions.Default(family);
        if (current is null) return No(songText, $"The block \"{block}\" is not an instance of a component.");
        List<ComponentDefinition> all = [.. doc.Components.Concat(definitions).DistinctBy(d => (d.Id, d.Version.ToString()))];
        int tat = target.IndexOf('@', StringComparison.Ordinal);
        string targetId = tat > 0 ? target[..tat] : current.Id;
        if (!SemVer.TryParse(tat > 0 ? target[(tat + 1)..] : target, out SemVer targetVersion)) return No(songText, $"\"{target}\" is not a version (1.2.3, or id@1.2.3 for another component).");
        ComponentDefinition? goal = all.FirstOrDefault(d => d.Id == targetId && d.Version.SameAs(targetVersion));
        if (goal is null) return No(songText, $"There is no component {targetId} {targetVersion} to migrate to.");
        if (goal.Id == current.Id && goal.Version.SameAs(current.Version)) return No(songText, "The block is on that version already.");

        // the chain: breadth first over the migrations, every migration is an edge from the version it starts at to the one it arrives at
        List<Edge> edges = [];
        foreach (ComponentDefinition d in all)
        {
            foreach (ComponentMigration m in d.Migrations) edges.Add(new Edge(d, m, Resolve(m, d, false), Resolve(m, d, true)));
        }

        Dictionary<(string, string), Edge?> came = new() { [(current.Id, current.Version.ToString())] = null };
        Queue<(string Id, SemVer Version)> queue = new([(current.Id, current.Version)]);
        bool found = false;
        while (queue.Count > 0 && !found && came.Count <= MaxChain * 8)
        {
            (string id, SemVer version) = queue.Dequeue();
            foreach (Edge e in edges.Where(e => e.From.Id == id && e.From.Version.SameAs(version)))
            {
                (string, string) key = (e.To.Id, e.To.Version.ToString());
                if (came.ContainsKey(key)) continue;
                came[key] = e;
                if (e.To.Id == goal.Id && e.To.Version.SameAs(goal.Version)) { found = true; break; }
                queue.Enqueue((e.To.Id, e.To.Version));
            }
        }

        if (!found)
        {
            string hint = current.Deprecation?.Replacement is { Length: > 0 } r ? $" The deprecation names {r} as the replacement, but no migration leads there." : string.Empty;
            return No(songText, $"AMP2222: There is no migration from {current.DeclaredName} {current.Version} to {goal.DeclaredName} {goal.Version}.{hint} Change the version by hand, or add a migration to the newer version.");
        }

        List<Edge> chain = [];
        for (Edge? e = came[(goal.Id, goal.Version.ToString())]; e is not null; e = came[(e.From.Id, e.From.Version.ToString())]) chain.Insert(0, e);
        if (chain.Count > MaxChain) return No(songText, $"A migration chain has at most {MaxChain} steps.");

        // run the statements over the values of the instance
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (Parameter p in node.Parameters) values[p.Name] = p.Value.Trim();
        List<MigrationChange> changes = [];
        HashSet<string> consumed = new(StringComparer.Ordinal);
        List<string> warnings = [];
        List<string> messages = [];
        List<(string From, string To)> portRenames = [], outputRenames = [];
        MigrationImpact impact = MigrationImpact.None;
        List<MigrationStep> steps = [];
        ComponentDefinition at = current;
        foreach (Edge e in chain)
        {
            ComponentDefinition? arrive = all.FirstOrDefault(d => d.Id == e.To.Id && d.Version.SameAs(e.To.Version));
            if (arrive is null) return No(songText, $"AMP2222: The migration leads to {e.To.Id} {e.To.Version}, which does not exist.");
            (IReadOnlyList<(int Line, string Kind, string A, string B)> statements, IReadOnlyList<(int Line, string Message)> problems) = Read(e.Migration);
            if (problems.Count > 0) return No(songText, $"AMP2221: line {problems[0].Line + 1}: {problems[0].Message}");
            Dictionary<string, string> old = new(values, StringComparer.Ordinal);
            foreach (ComponentProperty p in at.Properties) if (!old.ContainsKey(p.Name) && p.Default is { Length: > 0 }) old[p.Name] = p.Default;
            foreach ((int line, string kind, string a, string b) in statements)
            {
                switch (kind)
                {
                    case "rename":
                    case "copy":
                        if (!values.TryGetValue(a, out string? moved))
                        {
                            if (!old.TryGetValue(a, out moved)) { warnings.Add($"{e.Migration.To}: line {line + 1}: {a} has no value, so there is nothing to {kind}."); break; }
                        }

                        if (values.ContainsKey(b) && b != a) return No(songText, $"AMP2222: line {line + 1}: {kind} {a} -> {b} would overwrite the value of {b}.");
                        values[b] = moved;
                        if (kind == "rename") values.Remove(a);
                        changes.Add(new MigrationChange(kind, $"{a} -> {b}", moved, moved));
                        break;
                    case "drop":
                        if (values.Remove(a, out string? dropped)) changes.Add(new MigrationChange("drop", a, dropped, null));
                        break;
                    case "port":
                        // port input.audio -> input.audio2: the side is written in front of the name (without it the old component says which side it is)
                        string oldPort = a[(a.IndexOf('.', StringComparison.Ordinal) + 1)..], newPort = b[(b.IndexOf('.', StringComparison.Ordinal) + 1)..];
                        bool isOutput = a.StartsWith("output.", StringComparison.Ordinal) || (!a.Contains('.', StringComparison.Ordinal) && at.Outputs.Any(o => o.Name == oldPort) && at.Inputs.All(i => i.Name != oldPort));
                        if (oldPort != newPort) (isOutput ? outputRenames : portRenames).Add((oldPort, newPort));
                        changes.Add(new MigrationChange("port", $"{(isOutput ? "output" : "input")} {oldPort} -> {newPort}", null, null));
                        break;
                    default:
                        {
                            foreach (Match used in Regex.Matches(b, @"\bold\.([A-Za-z_][\w-]*)", RegexOptions.None, TimeSpan.FromSeconds(1))) consumed.Add(used.Groups[1].Value);
                            string? result = Evaluate(b, old, out string? failure);
                            if (result is null) return No(songText, $"AMP2222: line {line + 1}: {failure}");
                            values.TryGetValue(a, out string? before);
                            values[a] = result;
                            changes.Add(new MigrationChange("set", a, before, result));
                            break;
                        }
                }
            }

            MigrationImpact stepImpact = ImpactOf(e.Migration, out bool stated);
            if (!stated) warnings.Add($"{e.Migration.To}: the migration does not say its impact; it counts as musicalChange.");
            impact = (MigrationImpact)Math.Max((int)impact, (int)stepImpact);
            if (e.Migration.Message.Length > 0) messages.Add(e.Migration.Message);
            steps.Add(new MigrationStep(e.From.Id, e.From.Version.ToString(), e.To.Id, e.To.Version.ToString(), stepImpact, stated, e.Migration.Message));
            at = arrive;
        }

        // what the new version does not know can only be dropped: that loses a value
        Dictionary<string, string> kept = new(StringComparer.Ordinal);
        foreach ((string name, string value) in values)
        {
            ComponentProperty? property = goal.Property(name);
            if (property is not null) { kept[name] = value; continue; }
            if (current.Property(name) is not null || chain.Any(c => all.FirstOrDefault(d => d.Id == c.From.Id && d.Version.SameAs(c.From.Version))?.Property(name) is not null))
            {
                if (consumed.Contains(name))
                {
                    changes.Add(new MigrationChange("drop", name, value, null));   // a value a calculation took over is retired, not lost
                    continue;
                }

                changes.Add(new MigrationChange("drop-unmapped", name, value, null));
                impact = MigrationImpact.Destructive;
                warnings.Add($"{name} has no place in {goal.DeclaredName} {goal.Version} and is removed (destructive).");
                continue;
            }

            kept[name] = value;   // a parameter every block has (tint, layout, ...)
        }

        foreach ((string name, string value) in kept)
        {
            ComponentProperty? property = goal.Property(name);
            if (property is not null && TypeProblem(property.Type, value) is { } problem) return No(songText, $"AMP2222: the value of {name} after the migration is {value}, which is not {problem}.");
        }

        // the text afterwards
        List<TextEdit> edits = [];
        foreach (Parameter p in node.Parameters)
        {
            if (!kept.TryGetValue(p.Name, out string? now)) { edits.AddRange(Edits.RemoveParameter(doc, block, p.Name).Edits); continue; }
            if (now != p.Value.Trim()) edits.AddRange(Edits.SetParameter(doc, block, p.Name, now).Edits);
        }

        foreach ((string name, string value) in kept)
        {
            if (node.Parameters.Any(p => p.Name == name)) continue;
            EditResult add = Edits.SetParameter(doc, block, name, value);
            if (!add.Ok) return No(songText, add.Error!);
            edits.AddRange(add.Edits);
        }

        foreach (FromLine from in node.Froms)
        {
            string input = from.Input;
            foreach ((string a, string b) in portRenames) if (input == a) input = b;
            if (input == from.Input) continue;
            string text = doc.Lines[from.Line];
            int arrow = text.LastIndexOf("->", StringComparison.Ordinal);
            if (arrow < 0) continue;
            edits.Add(new TextEdit(from.Line + 1, arrow + 1, from.Line + 1, text.Length + 1, "-> " + input));
        }

        // the connections that read an output that is renamed follow it
        foreach (BlockNode consumer in doc.Blocks)
        {
            foreach (FromLine from in consumer.Froms.Where(x => x.Block == block))
            {
                string output = from.Output;
                foreach ((string a, string b) in outputRenames) if (output == a) output = b;
                if (output == from.Output) continue;
                string text = doc.Lines[from.Line];
                string old = from.RawBlock + "." + from.RawOutput;
                int position = text.IndexOf(old, StringComparison.Ordinal);
                if (position >= 0) edits.Add(new TextEdit(from.Line + 1, position + 1, from.Line + 1, position + 1 + old.Length, from.RawBlock + "." + output));
            }
        }

        if (Header().Match(doc.Lines[node.Line]) is { Success: true } header)
        {
            string newType = goal.Name;
            string replacement = header.Groups[1].Value + newType + header.Groups["mid"].Value + " pinned@" + goal.Version;   // v1: a chosen version of a component is a pin
            edits.Add(new TextEdit(node.Line + 1, 1, node.Line + 1, 1 + header.Length, replacement));
        }
        else
        {
            return No(songText, "The header of the block could not be read, so its version cannot be changed here.");
        }

        changes.Add(new MigrationChange("version", goal.Name, $"{current.Version}", $"{goal.Version}"));
        string migrated = Edits.Apply(songText, edits);
        return new MigrationPlan(true, string.Empty, steps, impact, impact > MigrationImpact.Equivalent, messages, changes, warnings, migrated, goal.Name, goal.Version.ToString());
    }

    private static string? TypeProblem(string type, string value)
    {
        string t = type.TrimEnd('?');
        string v = value.Trim();
        return t switch
        {
            "Int" => Regex.IsMatch(v, @"^-?\d+$", RegexOptions.None, TimeSpan.FromSeconds(1)) ? null : "an Int",
            "Float" or "Number" => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? null : "a number",
            "Bool" => v is "true" or "false" ? null : "true or false",
            "Duration" => Regex.IsMatch(v, @"^-?[0-9]+(\.[0-9]+)?[A-Za-z]*$", RegexOptions.None, TimeSpan.FromSeconds(1)) ? null : "a duration (1bar, 2beats, 250ms)",
            _ => null,
        };
    }

    // --- expressions: literals, old.<property>, + - * /, ( ), min max clamp round abs; units are kept when they agree ---

    private sealed record Quantity(double Value, string Unit);

    /// <summary>Evaluates the expression of a <c>set</c> statement over the values the instance had before the step. Bounded: at most 400 characters and 32 levels.</summary>
    /// <param name="expression">The expression.</param>
    /// <param name="old">The values before the step.</param>
    /// <param name="failure">Why it failed, or null.</param>
    /// <returns>The value as written in a song, or null.</returns>
    public static string? Evaluate(string expression, IReadOnlyDictionary<string, string> old, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(old);
        return EvaluateWith(expression, w => w.StartsWith("old.", StringComparison.Ordinal) && old.TryGetValue(w[4..], out string? v) ? v : null, out failure);
    }

    /// <summary>Evaluates an expression over named values (the properties of a component): numbers with an optional unit (<c>35%</c>, <c>80Hz</c>, <c>0.25beat</c>), the names, <c>+ - * /</c>, brackets and <c>min max clamp round abs int if</c> (a Bool is 1 or 0; <c>if(condition, a, b)</c> picks a or b). Bounded like a migration expression.</summary>
    /// <param name="expression">The expression.</param>
    /// <param name="scope">The values by name.</param>
    /// <param name="failure">Why it failed, or null.</param>
    /// <returns>The value as written in a song, or null.</returns>
    public static string? EvaluateScoped(string expression, IReadOnlyDictionary<string, string> scope, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return EvaluateWith(expression, w => scope.TryGetValue(w, out string? v) ? v : null, out failure);
    }

    private static string? EvaluateWith(string expression, Func<string, string?> lookup, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(expression);
        failure = null;
        string text = expression.Trim();
        if (text.Length > MaxExpression) { failure = $"An expression has at most {MaxExpression} characters."; return null; }
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') return text;
        if (text is "true" or "false") return text;
        try
        {
            int pos = 0;
            Quantity q = Sum(text, ref pos, lookup, 0);
            SkipSpaces(text, ref pos);
            if (pos != text.Length) throw new FormatException($"Unexpected \"{text[pos..]}\" in the expression.");
            return Format(q);
        }
        catch (FormatException ex)
        {
            failure = ex.Message;
            return null;
        }
    }

    private static string Format(Quantity q) => q.Value.ToString("0.############", CultureInfo.InvariantCulture) + q.Unit;

    private static void SkipSpaces(string t, ref int p)
    {
        while (p < t.Length && char.IsWhiteSpace(t[p])) p++;
    }

    private static Quantity Sum(string t, ref int p, Func<string, string?> lookup, int depth)
    {
        if (depth > MaxDepth) throw new FormatException("The expression is nested too deep.");
        Quantity left = Product(t, ref p, lookup, depth);
        while (true)
        {
            SkipSpaces(t, ref p);
            if (p >= t.Length || t[p] is not ('+' or '-')) return left;
            char op = t[p++];
            Quantity right = Product(t, ref p, lookup, depth);
            if (left.Unit != right.Unit) throw new FormatException($"Cannot add {Format(left)} and {Format(right)}: the units differ.");
            left = new Quantity(op == '+' ? left.Value + right.Value : left.Value - right.Value, left.Unit);
        }
    }

    private static Quantity Product(string t, ref int p, Func<string, string?> lookup, int depth)
    {
        Quantity left = Atom(t, ref p, lookup, depth);
        while (true)
        {
            SkipSpaces(t, ref p);
            if (p >= t.Length || t[p] is not ('*' or '/')) return left;
            char op = t[p++];
            Quantity right = Atom(t, ref p, lookup, depth);
            if (left.Unit.Length > 0 && right.Unit.Length > 0 && !(op == '/' && left.Unit == right.Unit)) throw new FormatException($"Cannot {(op == '*' ? "multiply" : "divide")} {Format(left)} and {Format(right)}: both have a unit.");
            if (op == '/' && right.Value == 0) throw new FormatException("Division by zero.");
            string unit = op == '/' && left.Unit == right.Unit ? string.Empty : left.Unit.Length > 0 ? left.Unit : right.Unit;
            left = new Quantity(op == '*' ? left.Value * right.Value : left.Value / right.Value, unit);
        }
    }

    private static Quantity Atom(string t, ref int p, Func<string, string?> lookup, int depth)
    {
        SkipSpaces(t, ref p);
        if (p >= t.Length) throw new FormatException("The expression ends too early.");
        if (t[p] == '(')
        {
            p++;
            Quantity inner = Sum(t, ref p, lookup, depth + 1);
            SkipSpaces(t, ref p);
            if (p >= t.Length || t[p] != ')') throw new FormatException("A bracket is not closed.");
            p++;
            return inner;
        }

        if (t[p] == '-')
        {
            p++;
            Quantity n = Atom(t, ref p, lookup, depth + 1);
            return new Quantity(-n.Value, n.Unit);
        }

        if (char.IsDigit(t[p]) || t[p] == '.')
        {
            int start = p;
            while (p < t.Length && (char.IsDigit(t[p]) || t[p] == '.')) p++;
            string number = t[start..p];
            int unitStart = p;
            if (p < t.Length && t[p] == '%') p++;
            else while (p < t.Length && char.IsLetter(t[p])) p++;
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) throw new FormatException($"\"{number}\" is not a number.");
            return new Quantity(value, t[unitStart..p]);
        }

        if (char.IsLetter(t[p]) || t[p] == '_')
        {
            int start = p;
            while (p < t.Length && (char.IsLetterOrDigit(t[p]) || t[p] is '_' or '-' or '.')) p++;
            string word = t[start..p];
            SkipSpaces(t, ref p);
            if (p < t.Length && t[p] == '(')
            {
                p++;
                List<Quantity> args = [];
                SkipSpaces(t, ref p);
                if (p < t.Length && t[p] == ')') p++;
                else
                {
                    while (true)
                    {
                        args.Add(Sum(t, ref p, lookup, depth + 1));
                        SkipSpaces(t, ref p);
                        if (p < t.Length && t[p] == ',') { p++; continue; }
                        if (p < t.Length && t[p] == ')') { p++; break; }
                        throw new FormatException($"The call {word}(...) is not closed.");
                    }
                }

                return Call(word, args);
            }

            string name = word.StartsWith("old.", StringComparison.Ordinal) ? word[4..] : word;
            string raw = lookup(word) ?? throw new FormatException(word.StartsWith("old.", StringComparison.Ordinal) ? $"The property {name} had no value before the migration." : $"\"{word}\" has no value here (a migration reads old.<property>).");
            if (raw.Trim() is "true" or "false") return new Quantity(raw.Trim() == "true" ? 1 : 0, string.Empty);   // a Bool is 1 or 0 in a calculation
            Match m = Regex.Match(raw.Trim(), @"^(-?[0-9]+(?:\.[0-9]+)?)([A-Za-z]*|%)$", RegexOptions.None, TimeSpan.FromSeconds(1));
            if (!m.Success) throw new FormatException($"The value of {name} ({raw.Trim()}) is not a number, so it cannot be used in a calculation.");
            return new Quantity(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[2].Value);
        }

        throw new FormatException($"Unexpected \"{t[p]}\" in the expression.");
    }

    private static Quantity Call(string name, List<Quantity> a)
    {
        // if(condition, a, b): a when the condition is not 0 (true is 1, false is 0), otherwise b; a and b have the same unit
        if (name == "if")
        {
            if (a.Count != 3) throw new FormatException("if needs three values: if(condition, a, b).");
            if (a[1].Unit != a[2].Unit) throw new FormatException("The two results of if(condition, a, b) must have the same unit.");
            return a[0].Value != 0 ? a[1] : a[2];
        }

        string unit = a.Count > 0 ? a[0].Unit : string.Empty;
        if (a.Any(x => x.Unit != unit)) throw new FormatException($"The arguments of {name} must have the same unit.");
        return name switch
        {
            "min" when a.Count == 2 => new Quantity(Math.Min(a[0].Value, a[1].Value), unit),
            "max" when a.Count == 2 => new Quantity(Math.Max(a[0].Value, a[1].Value), unit),
            "clamp" when a.Count == 3 => new Quantity(Math.Clamp(a[0].Value, a[1].Value, a[2].Value), unit),
            "round" when a.Count == 1 => new Quantity(Math.Round(a[0].Value, MidpointRounding.ToEven), unit),
            "abs" when a.Count == 1 => new Quantity(Math.Abs(a[0].Value), unit),
            "int" when a.Count == 1 => new Quantity(Math.Truncate(a[0].Value), unit),   // true is 1 and false is 0 already; int cuts the decimals
            _ => throw new FormatException($"{name} with {a.Count} argument(s) is not known (min(a, b), max(a, b), clamp(v, lo, hi), round(v), abs(v), int(v), if(condition, a, b))."),
        };
    }

    /// <summary>Gets the lines of a migration the checker complains about (AMP2221), so that a broken migration is seen in the file that holds it.</summary>
    /// <param name="definition">The component.</param>
    /// <returns>The problems.</returns>
    public static IReadOnlyList<ComponentIssue> Check(ComponentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<ComponentIssue> issues = [];
        foreach (ComponentMigration m in definition.Migrations)
        {
            (IReadOnlyList<(int Line, string Kind, string A, string B)> statements, IReadOnlyList<(int Line, string Message)> problems) = Read(m);
            foreach ((int line, string message) in problems) issues.Add(new ComponentIssue(line, 0, 1, "AMP2221", Severity.Error, message, string.Empty));
            if (m.Impact.Length == 0) issues.Add(new ComponentIssue(m.Line, 0, 1, "AMP2221", Severity.Warning, "This migration does not say its impact (impact none, equivalent, musicalChange or destructive): it counts as musicalChange.", string.Empty));
            else if (!Enum.TryParse(m.Impact, true, out MigrationImpact _)) issues.Add(new ComponentIssue(m.Line, 0, 1, "AMP2221", Severity.Error, $"\"{m.Impact}\" is not an impact (none, equivalent, musicalChange, destructive).", string.Empty));
            foreach ((int line, string kind, string a, string b) in statements)
            {
                if (kind == "set" && b.Length > MaxExpression) issues.Add(new ComponentIssue(line, 0, 1, "AMP2221", Severity.Error, $"An expression has at most {MaxExpression} characters.", string.Empty));
            }
        }

        return issues;
    }
}
