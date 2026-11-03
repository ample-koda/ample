using System.Text;
using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>A problem with a component that calls another component.</summary>
/// <param name="Component">The component that has the problem.</param>
/// <param name="Code">The diagnostic code.</param>
/// <param name="Message">The message.</param>
/// <param name="Severity">The severity.</param>
public sealed record NestIssue(ComponentDefinition Component, string Code, string Message, Severity Severity = Severity.Error);

/// <summary>
/// A Notes component can call another Notes component like a function: <c>emit koda.notes.tripleNotes(source, copies=2)</c>. The first unnamed argument is the first input, the other inputs and the properties
/// are given by name, what is left out takes the default of the called component. Because the process of a Notes component is a helper, the call is rewritten to the call of that helper before the helper
/// system sees it. Components never call themselves, directly or through each other (A calls B and B calls A is an error), and they are nested at most four deep, like audio components.
/// </summary>
public static class ComponentNesting
{
    /// <summary>The most components that can be nested below the first one.</summary>
    public const int MaxDepth = 4;

    private static readonly Regex NamedArg = new(@"^\s*([A-Za-z_][\w-]*)\s*=(?!=)\s*(.*)$", RegexOptions.Singleline, TimeSpan.FromSeconds(1));

    /// <summary>The Notes components that a component calls (by the names it writes in its process), each with the default version of the family.</summary>
    /// <param name="d">The calling component.</param>
    /// <param name="all">The components that are visible.</param>
    /// <returns>The called components.</returns>
    public static IReadOnlyList<ComponentDefinition> Callees(ComponentDefinition d, IReadOnlyList<ComponentDefinition> all)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(all);
        Dictionary<string, ComponentDefinition> byName = Families(all);
        if (byName.Count == 0) return [];
        Regex call = CallPattern(byName.Keys);
        List<ComponentDefinition> found = [];
        foreach (Match m in call.Matches(Mask(string.Join('\n', d.ProcessBody))).Cast<Match>())
        {
            ComponentDefinition target = PickVersion(m, byName, all, d, null);
            if (!found.Contains(target)) found.Add(target);
        }

        return found;
    }

    /// <summary>Finds the components that call each other in a circle (or themselves) and the chains that are nested too deep.</summary>
    /// <param name="all">The components that are visible.</param>
    /// <returns>The problems, one per component that has one.</returns>
    public static IReadOnlyList<NestIssue> Analyze(IReadOnlyList<ComponentDefinition> all)
    {
        ArgumentNullException.ThrowIfNull(all);
        // the same list is asked again for every component that has a call: read it once
        return Analyses.GetValue(all, list => Read(list));
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<ComponentDefinition>, IReadOnlyList<NestIssue>> Analyses = [];

    private static List<NestIssue> Read(IReadOnlyList<ComponentDefinition> all)
    {
        List<NestIssue> issues = [];
        Dictionary<ComponentDefinition, IReadOnlyList<ComponentDefinition>> graph = [];
        foreach (ComponentDefinition d in all.Where(c => !c.IsAudio && c.ProcessLine >= 0)) graph[d] = Callees(d, all);
        HashSet<ComponentDefinition> reported = [];
        foreach (ComponentDefinition start in graph.Keys)
        {
            List<ComponentDefinition> path = [];
            Walk(start, graph, path, issues, reported);
        }

        foreach (ComponentDefinition d in graph.Keys) issues.AddRange(Unpinned(d, all));
        foreach (ComponentDefinition d in graph.Keys.Where(k => !reported.Contains(k)))
        {
            int depth = Depth(d, graph, []);
            if (depth > MaxDepth) issues.Add(new NestIssue(d, "AMP2233", $"{d.Name} calls components that are nested {depth} deep: components are nested at most four deep and never call themselves."));
        }

        return issues;
    }

    /// <summary>The process of a component with every call of another Notes component written as the call of its helper.</summary>
    /// <param name="d">The component.</param>
    /// <param name="all">The components that are visible.</param>
    /// <param name="issues">Receives the problems with the calls (an unknown argument, a missing input), or null.</param>
    /// <returns>The lines of the process.</returns>
    public static IReadOnlyList<string> Rewrite(ComponentDefinition d, IReadOnlyList<ComponentDefinition> all, List<NestIssue>? issues = null)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(all);
        Dictionary<string, ComponentDefinition> byName = Families(all);
        if (byName.Count == 0) return d.ProcessBody;
        string text = string.Join('\n', d.ProcessBody);
        string masked = Mask(text);
        if (issues is not null)
        {
            // a call that looks like a component (a dotted name) and is none of the visible ones: say so instead of leaving a syntax error
            foreach (Match unknown in DottedCall.Matches(masked).Cast<Match>().Where(u => !byName.ContainsKey(u.Groups[1].Value)))
                issues.Add(new NestIssue(d, "AMP2248", $"{unknown.Groups[1].Value} is not a component that can be called here: is its file imported? The components in sight: {string.Join(", ", byName.Keys.Order(StringComparer.Ordinal).Take(8))}."));
        }

        Regex call = CallPattern(byName.Keys);
        if (!call.IsMatch(masked)) return d.ProcessBody;
        HashSet<ComponentDefinition> circle = InCircle(d, all);
        string rewritten = RewriteText(text, d, all, byName, call, circle, issues);
        return [.. rewritten.Split('\n')];
    }

    /// <summary>Replaces the matches of a pattern in the code of a text (not in its strings and comments) with what a map gives for the matched text.</summary>
    /// <param name="text">The text.</param>
    /// <param name="pattern">The pattern.</param>
    /// <param name="map">Gives the replacement for a matched text.</param>
    /// <returns>The text with the replacements.</returns>
    internal static string ReplaceInCode(string text, Regex pattern, Func<string, string> map)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(map);
        StringBuilder sb = new();
        int pos = 0;
        foreach (Match m in pattern.Matches(Mask(text)).Cast<Match>())
        {
            sb.Append(text, pos, m.Index - pos).Append(map(text.Substring(m.Index, m.Length)));
            pos = m.Index + m.Length;
        }

        return sb.Append(text, pos, text.Length - pos).ToString();
    }

    private static readonly Regex DottedCall = new(@"(?<![\w.\-])([A-Za-z_][\w-]*(?:\.[A-Za-z_][\w-]*)+)(?:@[0-9^~][\w.\-]*)?\s*\(", RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>The same text with every character of a string and of a comment (<c>#</c> to the end of the line) replaced by a space, so that brackets and names in them are not read as code.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The masked text, of the same length.</returns>
    internal static string Mask(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        char[] masked = text.ToCharArray();
        bool quote = false, comment = false;
        for (int i = 0; i < masked.Length; i++)
        {
            char c = masked[i];
            if (c == '\n') { comment = false; continue; }
            if (comment) { masked[i] = ' '; continue; }
            if (quote)
            {
                if (c == '\\' && i + 1 < masked.Length && masked[i + 1] != '\n') { masked[i] = ' '; masked[i + 1] = ' '; i++; }
                else if (c == '"') quote = false;
                else masked[i] = ' ';
                continue;
            }

            if (c == '"') quote = true;
            else if (c == '#') { comment = true; masked[i] = ' '; }
        }

        return new string(masked);
    }

    private static string RewriteText(string text, ComponentDefinition owner, IReadOnlyList<ComponentDefinition> all, Dictionary<string, ComponentDefinition> byName, Regex call, HashSet<ComponentDefinition> circle, List<NestIssue>? issues)
    {
        StringBuilder sb = new();
        string masked = Mask(text);
        int pos = 0;
        while (true)
        {
            Match m = call.Match(masked, pos);
            if (!m.Success)
            {
                sb.Append(text, pos, text.Length - pos);
                break;
            }

            int open = m.Index + m.Length - 1;
            int close = CloseOf(masked, open);
            if (close < 0)
            {
                // a bracket that never closes: leave this one as it is and go on after it, so later calls are still read
                sb.Append(text, pos, m.Index + m.Length - pos);
                pos = m.Index + m.Length;
                continue;
            }

            sb.Append(text, pos, m.Index - pos);
            ComponentDefinition callee = PickVersion(m, byName, all, owner, issues);
            List<string> args = [.. SplitArgs(masked[(open + 1)..close]).Select(r => RewriteText(text[(open + 1 + r.Start)..(open + 1 + r.End)], owner, all, byName, call, circle, issues))];
            int original = text[m.Index..(close + 1)].Count(c => c == '\n');
            if (m.Groups["pipe"].Success)
            {
                // x |> name(args): what is before the pipe is the first input
                string soFar = sb.ToString();
                int start = ReceiverStart(Mask(soFar));
                string taken = soFar[start..];
                sb.Length = start;
                args.Insert(0, taken.Trim());
                original += taken.Count(c => c == '\n') - taken.Trim().Count(c => c == '\n');
            }

            string replaced = circle.Contains(callee) ? "emptyNotes()" : Call(callee, args, owner, issues);
            int now = replaced.Count(c => c == '\n');
            sb.Append(replaced);
            if (original > now) sb.Append('\n', original - now);
            pos = close + 1;
        }

        return sb.ToString();
    }

    private static string Call(ComponentDefinition callee, List<string> args, ComponentDefinition owner, List<NestIssue>? issues)
    {
        Dictionary<string, string> named = new(StringComparer.Ordinal);
        List<string> positional = [];
        foreach (string arg in args)
        {
            Match nm = NamedArg.Match(arg);
            if (!nm.Success) positional.Add(arg.Trim());
            else if (callee.Inputs.Any(i => i.Name == nm.Groups[1].Value) || callee.Properties.Any(p => p.Name == nm.Groups[1].Value)) named[nm.Groups[1].Value] = nm.Groups[2].Value.Trim();
            else issues?.Add(new NestIssue(owner, "AMP2248", $"{callee.Name} has no input or property \"{nm.Groups[1].Value}\". It has: {string.Join(", ", callee.Inputs.Select(i => i.Name).Concat(callee.Properties.Select(p => p.Name)))}."));
        }

        List<string> given = [];
        int next = 0;
        foreach (ComponentPort input in callee.Inputs)
        {
            string? value = named.GetValueOrDefault(input.Name);
            if (value is null && next < positional.Count) value = positional[next++];
            bool has = value is not null;
            if (value is null)
            {
                if (input.Fallback is not null) value = Default(callee.Property(input.Fallback));
                else if (input.Optional) value = "emptyNotes()";
                else
                {
                    issues?.Add(new NestIssue(owner, "AMP2248", $"{callee.Name} needs the input \"{input.Name}\": {callee.Name}({input.Name}, ...)."));
                    value = "emptyNotes()";
                }
            }

            given.Add(value);
            if (input.Optional || input.Fallback is not null) given.Add(has ? "true" : "false");
        }

        if (next < positional.Count) issues?.Add(new NestIssue(owner, "AMP2248", $"{callee.Name} has {callee.Inputs.Count} input{(callee.Inputs.Count == 1 ? string.Empty : "s")}: the other values are given by name (property=value)."));
        foreach (ComponentProperty p in callee.Properties) given.Add(named.TryGetValue(p.Name, out string? v) ? v : Default(p));
        return callee.HelperName + "(" + string.Join(", ", given) + ")";
    }

    private static string Default(ComponentProperty? p)
    {
        string text = (p?.Default ?? string.Empty).Trim();
        if (p is not null && p.Type.TrimEnd('?') == "Notes")
        {
            if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1].Trim();
            return text.Length == 0 ? "emptyNotes()" : text;
        }

        return text.Length == 0 ? "0" : text;
    }

    private static Dictionary<string, ComponentDefinition> Families(IReadOnlyList<ComponentDefinition> all)
    {
        Dictionary<string, ComponentDefinition> byName = new(StringComparer.Ordinal);
        foreach (IGrouping<string, ComponentDefinition> family in all.Where(c => !c.IsAudio && c.Name.Length > 0).GroupBy(c => c.Name, StringComparer.Ordinal))
        {
            ComponentDefinition? pick = ComponentVersions.Default(family);
            if (pick is not null) byName[family.Key] = pick;
        }

        // a file that imports with an alias writes the declared name once the registry has read it; a song writes the exported name: both find the component
        foreach (IGrouping<string, ComponentDefinition> family in all.Where(c => !c.IsAudio && c.DeclaredName.Length > 0 && c.DeclaredName != c.Name).GroupBy(c => c.DeclaredName, StringComparer.Ordinal))
        {
            ComponentDefinition? pick = ComponentVersions.Default(family);
            if (pick is not null) byName.TryAdd(family.Key, pick);
        }

        return byName;
    }

    /// <summary>The component a call means: the default version of its family, or the one that a pin (<c>name@1.2(...)</c>) asks for.</summary>
    private static ComponentDefinition PickVersion(Match call, Dictionary<string, ComponentDefinition> byName, IReadOnlyList<ComponentDefinition> all, ComponentDefinition owner, List<NestIssue>? issues)
    {
        string name = call.Groups["name"].Value;
        ComponentDefinition fallback = byName[name];
        if (!call.Groups["ver"].Success) return fallback;
        string pin = call.Groups["ver"].Value;
        if (!VersionRequest.TryParse(pin, out VersionRequest request))
        {
            issues?.Add(new NestIssue(owner, "AMP2248", $"\"{pin}\" is not a version request for {name} (1.8, 1.8.0, ^1.2 or ~1.2)."));
            return fallback;
        }

        List<ComponentDefinition> family = [.. all.Where(c => !c.IsAudio && (c.Name == name || c.DeclaredName == name))];
        ComponentDefinition? pick = ComponentVersions.Pick(family, request);
        if (pick is null) issues?.Add(new NestIssue(owner, "AMP2248", $"No version of {name} matches @{request}. Versions: {string.Join(", ", family.Select(f => f.Version.ToString()).Order(StringComparer.Ordinal))}."));
        return pick ?? fallback;
    }

    /// <summary>The calls of a component that name no version while their family has more than one: a newer version that becomes the default changes what the caller makes.</summary>
    private static List<NestIssue> Unpinned(ComponentDefinition d, IReadOnlyList<ComponentDefinition> all)
    {
        List<NestIssue> issues = [];
        if (d.Seal is not null) return issues;
        Dictionary<string, ComponentDefinition> byName = Families(all);
        if (byName.Count == 0) return issues;
        foreach (Match m in CallPattern(byName.Keys).Matches(Mask(string.Join('\n', d.ProcessBody))).Cast<Match>().Where(x => !x.Groups["ver"].Success))
        {
            string name = m.Groups["name"].Value;
            if (all.Where(c => !c.IsAudio && (c.Name == name || c.DeclaredName == name)).Select(c => c.Version.ToString()).Distinct(StringComparer.Ordinal).Count() > 1)
                issues.Add(new NestIssue(d, "AMP2249", $"{d.Name} calls {name} without a version while {name} has more than one: the default version ({byName[name].Version}) is used, and another default later changes what this component makes. Write {name}@{byName[name].Version}(...) to keep it.", Severity.Info));
        }

        return issues;
    }

    private static Regex CallPattern(IEnumerable<string> names) => new(@"(?<pipe>\|>\s*)?(?<![\w.\-])(?<name>" + string.Join("|", names.OrderByDescending(n => n.Length).Select(Regex.Escape)) + @")(?:@(?<ver>[0-9^~][\w.\-]*))?\s*\(", RegexOptions.None, TimeSpan.FromSeconds(1));

    private static readonly Regex Starts = new(@"(\bemit\s+|\blet\s+[A-Za-z_][\w-]*\s*=(?!=)\s*)", RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>Where the value that a pipe step receives starts in the text so far: after the last unmatched opening bracket or comma, or after the last <c>emit</c> or <c>let x =</c>.</summary>
    private static int ReceiverStart(string soFar)
    {
        int depth = 0, boundary = 0;
        bool quote = false;
        for (int i = soFar.Length - 1; i >= 0 && boundary == 0; i--)
        {
            char c = soFar[i];
            if (c == '"') quote = !quote;
            else if (quote) continue;
            else if (c is ')' or ']' or '}') depth++;
            else if (c is '(' or '[' or '{')
            {
                if (depth == 0) boundary = i + 1;
                else depth--;
            }
            else if (c == ',' && depth == 0) boundary = i + 1;
        }

        int start = boundary;
        foreach (Match keyword in Starts.Matches(soFar[boundary..]).Cast<Match>()) start = boundary + keyword.Index + keyword.Length;
        return start;
    }

    /// <summary>The text of a call of a component (<c>helper(args)</c>), for the places that build one from a call they read themselves.</summary>
    /// <param name="callee">The called component.</param>
    /// <param name="args">The arguments as written (the first unnamed one is the first input).</param>
    /// <param name="owner">The component that calls, for the messages.</param>
    /// <param name="issues">Receives the problems, or null.</param>
    /// <returns>The call of the helper.</returns>
    public static string CallText(ComponentDefinition callee, IReadOnlyList<string> args, ComponentDefinition owner, List<NestIssue>? issues = null)
    {
        ArgumentNullException.ThrowIfNull(callee);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(owner);
        return Call(callee, [.. args], owner, issues);
    }

    private static int CloseOf(string text, int open)
    {
        int depth = 0;
        bool quote = false;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"') quote = !quote;
            else if (!quote)
            {
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}' && --depth == 0) return i;
            }
        }

        return -1;
    }

    private static List<(int Start, int End)> SplitArgs(string inner)
    {
        List<(int Start, int End)> args = [];
        int depth = 0, start = 0;
        bool quote = false;
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c == '"') quote = !quote;
            else if (!quote)
            {
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}') depth--;
                else if (c == ',' && depth == 0)
                {
                    args.Add((start, i));
                    start = i + 1;
                }
            }
        }

        if (inner[start..].Trim().Length > 0) args.Add((start, inner.Length));
        return args;
    }

    private static void Walk(ComponentDefinition node, Dictionary<ComponentDefinition, IReadOnlyList<ComponentDefinition>> graph, List<ComponentDefinition> path, List<NestIssue> issues, HashSet<ComponentDefinition> reported)
    {
        int at = path.IndexOf(node);
        if (at >= 0)
        {
            List<ComponentDefinition> circle = [.. path.Skip(at)];
            if (circle.Any(reported.Contains)) return;
            string way = string.Join(" -> ", circle.Append(node).Select(c => c.Name));
            string text = circle.Count == 1
                ? $"{node.Name} calls itself ({way}): a component never calls itself."
                : circle.Count == 2
                    ? $"{circle[0].Name} calls {circle[1].Name} and {circle[1].Name} calls {circle[0].Name} ({way}): components never call each other in a circle."
                    : $"These components call each other in a circle: {way}. A component never calls itself, directly or through other components.";
            foreach (ComponentDefinition c in circle)
            {
                reported.Add(c);
                issues.Add(new NestIssue(c, "AMP2247", text));
            }

            return;
        }

        if (!graph.TryGetValue(node, out IReadOnlyList<ComponentDefinition>? calls)) return;
        path.Add(node);
        foreach (ComponentDefinition next in calls) Walk(next, graph, path, issues, reported);
        path.RemoveAt(path.Count - 1);
    }

    private static int Depth(ComponentDefinition node, Dictionary<ComponentDefinition, IReadOnlyList<ComponentDefinition>> graph, HashSet<ComponentDefinition> seen)
    {
        if (!seen.Add(node) || !graph.TryGetValue(node, out IReadOnlyList<ComponentDefinition>? calls)) return 0;
        int deepest = 0;
        foreach (ComponentDefinition next in calls) deepest = Math.Max(deepest, 1 + Depth(next, graph, seen));
        seen.Remove(node);
        return deepest;
    }

    /// <summary>The components that stand in a circle with a component, calls to which are left out of its helper (the circle is reported, not run).</summary>
    private static HashSet<ComponentDefinition> InCircle(ComponentDefinition d, IReadOnlyList<ComponentDefinition> all)
    {
        HashSet<ComponentDefinition> circle = [];
        foreach (NestIssue issue in Analyze(all).Where(i => i.Code == "AMP2247" && (ReferenceEquals(i.Component, d) || i.Component.Name == d.Name)))
        {
            foreach (ComponentDefinition callee in Callees(d, all)) if (Reaches(callee, d, all, [])) circle.Add(callee);
            _ = issue;
        }

        return circle;
    }

    private static bool Reaches(ComponentDefinition from, ComponentDefinition target, IReadOnlyList<ComponentDefinition> all, HashSet<ComponentDefinition> seen)
    {
        if (from.Name == target.Name) return true;
        if (!seen.Add(from)) return false;
        foreach (ComponentDefinition next in Callees(from, all)) if (Reaches(next, target, all, seen)) return true;
        return false;
    }
}
