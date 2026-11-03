using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>A parameter a module declares, with its default value text.</summary>
/// <param name="Name">Parameter name.</param>
/// <param name="Default">Default value as written.</param>
public sealed record ModuleParam(string Name, string Default);

/// <summary>An output a module offers: the block (or nested instance) and port it comes from.</summary>
/// <param name="Name">Output name outside.</param>
/// <param name="Block">Block or nested instance inside the module.</param>
/// <param name="Port">Output port of that block.</param>
public sealed record ModuleOutput(string Name, string Block, string Port);

/// <summary>A problem with a module or a use of one. Runtime stage codes AMP5320 to AMP5328.</summary>
/// <param name="Code">Diagnostic code.</param>
/// <param name="Message">One plain sentence.</param>
/// <param name="Line">0-based line of the <c>use</c> in the song.</param>
/// <param name="Module">The module concerned, or null.</param>
/// <param name="Severity">The severity (an error unless it says otherwise).</param>
public sealed record ModuleDiagnostic(string Code, string Message, int Line, string? Module, Severity Severity = Severity.Error);

/// <summary>A module file found by a resolver.</summary>
/// <param name="Name">Name as used.</param>
/// <param name="Text">The text of the file.</param>
/// <param name="Origin">Where it came from (a path or <c>project</c>), for messages.</param>
public sealed record ModuleSource(string Name, string Text, string Origin);

/// <summary>A module that was used (for the lock file and the credits).</summary>
/// <param name="Name">Module name.</param>
/// <param name="Version">The version the file declares.</param>
/// <param name="Hash">sha256 of the file text, <c>sha256:</c>-prefixed.</param>
/// <param name="Origin">Where it came from.</param>
/// <param name="Size">The size of the file text in bytes.</param>
public sealed record ModuleUsage(string Name, string Version, string Hash, string Origin, long Size);

/// <summary>A parsed module file: <c>module &lt;name&gt; version &lt;v&gt;</c>, then <c>param</c>, <c>input</c> and <c>output</c> lines, then ordinary blocks (and nested <c>use</c> statements).</summary>
public sealed class ModuleDefinition
{
    /// <summary>Gets the module name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the module version.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Gets the declared parameters.</summary>
    public IReadOnlyList<ModuleParam> Params { get; init; } = [];

    /// <summary>Gets the declared inputs.</summary>
    public IReadOnlyList<string> Inputs { get; init; } = [];

    /// <summary>Gets the declared outputs.</summary>
    public IReadOnlyList<ModuleOutput> Outputs { get; init; } = [];

    /// <summary>Gets the body text: the file with the declaration lines blanked, so line numbers stay.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Gets the problems of the file itself (AMP5326).</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    private static readonly Regex Header = new("^module\\s+(\"(?:[^\"\\\\]|\\\\.)*\"|[\\w-]+(?:/[\\w-]+)*)\\s+version\\s+(\\d+(?:\\.\\d+)*)$", RegexOptions.None, TimeSpan.FromSeconds(2));
    private static readonly Regex ParamLine = new("^param\\s+([\\w-]+)\\s+(.+)$", RegexOptions.None, TimeSpan.FromSeconds(2));
    private static readonly Regex InputLine = new("^input\\s+([\\w-]+)$", RegexOptions.None, TimeSpan.FromSeconds(2));
    private static readonly Regex OutputLine = new("^output\\s+([\\w-]+)\\s+(\"(?:[^\"\\\\]|\\\\.)*\"|[^\\s.\"]+)\\.(\"(?:[^\"\\\\]|\\\\.)*\"|\\S+)$", RegexOptions.None, TimeSpan.FromSeconds(2));

    /// <summary>Parses a module file; it never throws.</summary>
    public static ModuleDefinition Parse(string text)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List<string> problems = [];
        string name = string.Empty;
        string version = string.Empty;
        List<ModuleParam> ps = [];
        List<string> inputs = [];
        List<ModuleOutput> outputs = [];
        bool headerSeen = false;
        int depth = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line.EndsWith('{'))
            {
                depth++;
                continue;
            }

            if (line == "}")
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth > 0)
            {
                continue;
            }

            Match m;
            if ((m = Header.Match(line)).Success)
            {
                if (headerSeen)
                {
                    problems.Add($"Line {i + 1}: a module file has one module line.");
                }

                headerSeen = true;
                name = Parser.Unquote(m.Groups[1].Value);
                version = m.Groups[2].Value;
                lines[i] = string.Empty;
            }
            else if ((m = ParamLine.Match(line)).Success)
            {
                ps.Add(new ModuleParam(m.Groups[1].Value, m.Groups[2].Value.Trim()));
                lines[i] = string.Empty;
            }
            else if ((m = InputLine.Match(line)).Success)
            {
                inputs.Add(m.Groups[1].Value);
                lines[i] = string.Empty;
            }
            else if ((m = OutputLine.Match(line)).Success)
            {
                outputs.Add(new ModuleOutput(m.Groups[1].Value, Parser.Unquote(m.Groups[2].Value), Parser.Unquote(m.Groups[3].Value)));
                lines[i] = string.Empty;
            }
            else if (line.StartsWith("module", StringComparison.Ordinal) || line.StartsWith("param", StringComparison.Ordinal) || line.StartsWith("input", StringComparison.Ordinal) || line.StartsWith("output", StringComparison.Ordinal))
            {
                problems.Add($"Line {i + 1}: expected \"module <name> version <v>\", \"param <name> <default>\", \"input <name>\" or \"output <name> <block>.<port>\".");
                lines[i] = string.Empty;
            }
        }

        if (!headerSeen)
        {
            problems.Add("The file has no \"module <name> version <v>\" line.");
        }

        foreach (string d in ps.GroupBy(p => p.Name).Where(g => g.Count() > 1).Select(g => g.Key))
        {
            problems.Add($"The parameter \"{d}\" is declared twice.");
        }

        return new ModuleDefinition { Name = name, Version = version, Params = ps, Inputs = inputs, Outputs = outputs, Body = string.Join('\n', lines), Problems = problems };
    }

    /// <summary>True when a use pinned to <paramref name="pinned"/> may take a module of version <paramref name="actual"/>: the same major, and not older than the pin.</summary>
    public static bool Fits(string pinned, string actual)
    {
        if (pinned.Length == 0)
        {
            return true;
        }

        int[] a = Numbers(pinned);
        int[] b = Numbers(actual);
        if (a.Length == 0 || b.Length == 0 || a[0] != b[0])
        {
            return false;
        }

        for (int i = 1; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0;
            int y = i < b.Length ? b[i] : 0;
            if (y != x)
            {
                return y > x;
            }
        }

        return true;
    }

    private static int[] Numbers(string v) => [.. v.Split('.').Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : -1)];
}

/// <summary>Finds the module file of a name (project folder, linked folders, packs); null when there is none.</summary>
/// <param name="name">The module name as written in the song.</param>
public delegate ModuleSource? ModuleResolver(string name);

/// <summary>The song with every <c>use</c> replaced by the blocks of its module.</summary>
/// <param name="Text">The expanded song text (parse it for building).</param>
/// <param name="Diagnostics">Problems with modules and their use.</param>
/// <param name="Used">The module files that were used.</param>
/// <param name="LineOrigin">For every line of <see cref="Text"/>, the 0-based line of the original song it stands for (a generated line stands for the <c>use</c> line).</param>
/// <param name="Components">The components the song used (their hashes are what a song pins), or null when it used none.</param>
public sealed record ExpandedSong(string Text, IReadOnlyList<ModuleDiagnostic> Diagnostics, IReadOnlyList<ModuleUsage> Used, IReadOnlyList<int> LineOrigin, IReadOnlyList<ComponentUsage>? Components = null)
{
    /// <summary>The prefix of the name of a component in the list of things a song pins (<c>component:&lt;canonical id&gt;</c>, with the semantic hash as its hash).</summary>
    public const string ComponentPrefix = "component:";

    /// <summary>Gets what the song pins: its modules, and its components as entries with <see cref="ComponentPrefix"/> (the version and the semantic hash of each).</summary>
    public IReadOnlyList<ModuleUsage> LockUsages => [.. Used, .. (Components ?? []).Select(c => new ModuleUsage(ComponentPrefix + c.Id, c.Version, c.Semantic, c.Origin, 0))];
}

/// <summary>
/// Expands module instances into plain blocks. Every block of an instance is named <c>instance/block</c> (a nested instance
/// <c>outer/inner/block</c>), and a block's seed is derived from its name, so the seeds of an instance depend on the path of the
/// instance: the same module used twice gives two different, individually stable sets of seeds. Parameters are substituted
/// into values as <c>$name</c>; module inputs are written <c>from in.&lt;input&gt; -&gt; port</c> inside the module.
/// </summary>
public static class ModuleExpander
{
    private const int MaxDepth = 8;
    private static readonly Regex Plain = new("^[A-Za-z0-9_-]+$", RegexOptions.None, TimeSpan.FromSeconds(2));
    private static readonly Regex Placeholder = new("\\$([A-Za-z_][\\w-]*)", RegexOptions.None, TimeSpan.FromSeconds(2));

    /// <summary>Expands the modules of a song, then the instances of its components. A song without <c>use</c> statements and without instances of components comes back unchanged.</summary>
    /// <param name="songText">The song text.</param>
    /// <param name="spec">The language data.</param>
    /// <param name="resolve">Finds module files.</param>
    /// <param name="components">The files components can come from (resolved imports), or null.</param>
    /// <returns>The expanded song.</returns>
    public static ExpandedSong Expand(string songText, SpecData spec, ModuleResolver resolve, IReadOnlyList<ComponentSource>? components = null)
    {
        ExpandedSong result = ExpandWithComponents(songText, spec, resolve, components);
        // what the expanders write uses the internal port names (in, out, mix); the text that the build reads uses the stream names (lint L010)
        return string.Equals(result.Text, songText, StringComparison.Ordinal) ? result : result with { Text = AmpleMigration.RenamePorts(result.Text, spec) };
    }

    private static ExpandedSong ExpandWithComponents(string songText, SpecData spec, ModuleResolver resolve, IReadOnlyList<ComponentSource>? components)
    {
        ExpandedSong modules = ExpandModules(songText, spec, resolve);
        ComponentExpansion expanded = ComponentExpander.Expand(modules.Text, spec, components);
        if (ReferenceEquals(expanded.Text, modules.Text) && expanded.Used.Count == 0 && expanded.Diagnostics.Count == 0) return modules;
        int Origin(int line) => modules.LineOrigin.Count == 0 ? line : modules.LineOrigin[Math.Clamp(line, 0, modules.LineOrigin.Count - 1)];
        return new ExpandedSong(
            expanded.Text,
            [.. modules.Diagnostics, .. expanded.Diagnostics.Select(d => d with { Line = Origin(d.Line) })],
            modules.Used,
            [.. expanded.LineOrigin.Select(Origin)],
            expanded.Used);
    }

    private static ExpandedSong ExpandModules(string songText, SpecData spec, ModuleResolver resolve)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(resolve);
        AmpleDocument doc = Parser.Parse(songText, spec);
        string[] lines = songText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (doc.Uses.Count == 0)
        {
            return new ExpandedSong(songText, [], [], [.. Enumerable.Range(0, lines.Length)]);
        }

        Run run = new(spec, resolve);
        Dictionary<string, UseNode> topUses = doc.Uses.GroupBy(u => u.Instance).ToDictionary(g => g.Key, g => g.First());
        Dictionary<string, Expansion> done = [];
        HashSet<string> active = [];

        Expansion? Top(string instance)
        {
            if (done.TryGetValue(instance, out Expansion? e))
            {
                return e;
            }

            if (!topUses.TryGetValue(instance, out UseNode? u))
            {
                return null;
            }

            if (!active.Add(instance))
            {
                run.Diagnostics.Add(new ModuleDiagnostic("AMP5327", $"The modules \"{instance}\" depends on use each other in a loop.", u.Line, u.Module));
                return new Expansion([], []);
            }

            Expansion result = run.ExpandUse(u, string.Empty, u.Line, Top, null, 0, []);
            active.Remove(instance);
            done[instance] = result;
            return result;
        }

        foreach (UseNode u in doc.Uses)
        {
            Top(u.Instance);
        }

        List<string> output = [];
        List<int> origin = [];
        HashSet<int> skip = [];
        Dictionary<int, UseNode> useAt = doc.Uses.ToDictionary(u => u.Line);
        foreach (UseNode u in doc.Uses)
        {
            for (int l = u.Line; l <= (u.CloseLine < 0 ? lines.Length - 1 : u.CloseLine); l++)
            {
                skip.Add(l);
            }
        }

        Dictionary<int, FromLine> songFroms = doc.Blocks.SelectMany(b => b.Froms).Where(f => topUses.ContainsKey(f.Block)).ToDictionary(f => f.Line);
        for (int i = 0; i < lines.Length; i++)
        {
            if (useAt.TryGetValue(i, out UseNode? u))
            {
                if (done.TryGetValue(u.Instance, out Expansion? e) && ReferenceEquals(topUses[u.Instance], u))
                {
                    output.AddRange(e.Lines);
                    origin.AddRange(e.Lines.Select(_ => u.Line));
                }

                continue;
            }

            if (skip.Contains(i))
            {
                continue;
            }

            string line = lines[i];
            if (songFroms.TryGetValue(i, out FromLine? f))
            {
                Expansion? e = Top(f.Block);
                if (e is not null && e.Outputs.TryGetValue(f.Output, out (string Block, string Port) target))
                {
                    int indent = line.Length - line.TrimStart().Length;
                    line = new string(' ', indent) + $"from {Q(target.Block)}.{Q(target.Port)} -> {f.RawInput}";
                }
                else if (e is not null)
                {
                    run.Diagnostics.Add(new ModuleDiagnostic("AMP5325", $"The module instance \"{f.Block}\" has no output \"{f.Output}\". Its outputs: {string.Join(", ", e.Outputs.Keys.Order(StringComparer.Ordinal))}.", i, topUses[f.Block].Module));
                }
            }

            output.Add(line);
            origin.Add(i);
        }

        return new ExpandedSong(string.Join('\n', output), run.Diagnostics, [.. run.Used.Values.OrderBy(m => m.Name, StringComparer.Ordinal)], origin);
    }

    internal static string Q(string name) => Plain.IsMatch(name) ? name : "\"" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    internal sealed record Expansion(List<string> Lines, Dictionary<string, (string Block, string Port)> Outputs);

    private sealed class Run(SpecData spec, ModuleResolver resolve)
    {
        public List<ModuleDiagnostic> Diagnostics { get; } = [];

        public Dictionary<string, ModuleUsage> Used { get; } = [];

        private readonly Dictionary<string, ModuleDefinition?> definitions = [];

        /// <summary>Expands one use. <paramref name="prefix"/> is the path of the enclosing instance; <paramref name="sources"/> finds the outputs of other top-level instances; <paramref name="inner"/> resolves names inside an enclosing module.</summary>
        public Expansion ExpandUse(UseNode use, string prefix, int reportLine, Func<string, Expansion?> sources, Func<FromLine, (string Block, string Port)?>? inner, int depth, Dictionary<string, string> outerParams)
        {
            string path = prefix.Length == 0 ? use.Instance : prefix + "/" + use.Instance;
            if (depth > MaxDepth)
            {
                Diagnostics.Add(new ModuleDiagnostic("AMP5327", $"Modules are nested more than {MaxDepth.ToString(CultureInfo.InvariantCulture)} deep at \"{path}\".", reportLine, use.Module));
                return new Expansion([], []);
            }

            ModuleDefinition? def = Definition(use, reportLine);
            if (def is null)
            {
                return new Expansion([], []);
            }

            // Parameters.
            Dictionary<string, string> values = def.Params.ToDictionary(p => p.Name, p => p.Default);
            foreach (Parameter p in use.Parameters)
            {
                if (!values.ContainsKey(p.Name))
                {
                    Diagnostics.Add(new ModuleDiagnostic("AMP5322", $"The module \"{def.Name}\" has no parameter \"{p.Name}\". Its parameters: {(def.Params.Count == 0 ? "none" : string.Join(", ", def.Params.Select(x => x.Name)))}.", reportLine, def.Name));
                }
                else
                {
                    values[p.Name] = Substitute(p.Value, outerParams, reportLine, def.Name, false);
                }
            }

            // Input bindings: where the module's inputs come from.
            Dictionary<string, (string Block, string Port)> bindings = [];
            foreach (FromLine f in use.Froms)
            {
                if (!def.Inputs.Contains(f.Input))
                {
                    Diagnostics.Add(new ModuleDiagnostic("AMP5323", $"The module \"{def.Name}\" has no input \"{f.Input}\". Its inputs: {(def.Inputs.Count == 0 ? "none" : string.Join(", ", def.Inputs))}.", reportLine, def.Name));
                    continue;
                }

                (string Block, string Port)? source = Source(f, prefix, sources, inner, reportLine, def.Name);
                if (source is not null)
                {
                    bindings[f.Input] = source.Value;
                }
            }

            foreach (string input in def.Inputs.Where(i => !bindings.ContainsKey(i)))
            {
                Diagnostics.Add(new ModuleDiagnostic("AMP5323", $"The input \"{input}\" of the module instance \"{path}\" is not connected. Add: from <block>.<output> -> {input}", reportLine, def.Name));
            }

            // The body.
            AmpleDocument body = Parser.Parse(def.Body, spec, new ParseOptions { RequireProjectLine = false });
            string[] bodyLines = def.Body.Split('\n');
            List<string> lines = [];
            Dictionary<string, Expansion> nested = [];
            HashSet<string> innerBlocks = [.. body.Blocks.Select(b => b.Name)];
            HashSet<string> innerUses = [.. body.Uses.Select(u => u.Instance)];

            (string Block, string Port)? Inside(FromLine f)
            {
                if (f.Block == "in")
                {
                    return bindings.TryGetValue(f.Output, out (string Block, string Port) b) ? b : null;
                }

                if (innerUses.Contains(f.Block))
                {
                    Expansion e = NestedFor(f.Block);
                    if (e.Outputs.TryGetValue(f.Output, out (string Block, string Port) t))
                    {
                        return t;
                    }

                    Diagnostics.Add(new ModuleDiagnostic("AMP5325", $"The module instance \"{f.Block}\" inside \"{def.Name}\" has no output \"{f.Output}\".", reportLine, def.Name));
                    return null;
                }

                return (path + "/" + f.Block, f.Output);
            }

            HashSet<string> activeNested = [];
            Expansion NestedFor(string instance)
            {
                if (nested.TryGetValue(instance, out Expansion? e))
                {
                    return e;
                }

                UseNode u = body.Uses.First(x => x.Instance == instance);
                if (!activeNested.Add(instance))
                {
                    Diagnostics.Add(new ModuleDiagnostic("AMP5327", $"The instances \"{instance}\" inside \"{def.Name}\" depend on each other in a loop.", reportLine, def.Name));
                    return new Expansion([], []);
                }

                // Parameter values of the nested use may use this module's parameters.
                UseNode substituted = new() { Module = u.Module, Version = u.Version, Instance = u.Instance, RawInstance = u.RawInstance, Line = u.Line, CloseLine = u.CloseLine };
                substituted.Froms.AddRange(u.Froms);
                substituted.Parameters.AddRange(u.Parameters);
                Expansion result = ExpandUse(substituted, path, reportLine, sources, Inside, depth + 1, values);
                activeNested.Remove(instance);
                nested[instance] = result;
                return result;
            }

            foreach (BlockNode b in body.Blocks.Where(x => !x.Skipped))
            {
                string header = bodyLines[b.Line].Trim();
                int at = header.IndexOf(b.RawName, b.Type.Length, StringComparison.Ordinal);
                string newName = path + "/" + b.Name;
                lines.Add(at < 0 ? header : header[..at] + Q(newName) + header[(at + b.RawName.Length)..]);
                foreach (Parameter p in b.Parameters)
                {
                    lines.Add($"  {p.Name} {Substitute(p.Value, values, reportLine, def.Name, true)}");
                }

                foreach (FromLine f in b.Froms)
                {
                    (string Block, string Port)? s = Inside(f);
                    if (s is null)
                    {
                        if (f.Block == "in")
                        {
                            // The missing binding was reported above.
                            continue;
                        }

                        continue;
                    }

                    lines.Add($"  from {Q(s.Value.Block)}.{Q(s.Value.Port)} -> {f.RawInput}");
                }

                lines.Add("}");
            }

            // Nested instances not reached through an output or an input are still part of the module.
            foreach (UseNode u in body.Uses)
            {
                _ = NestedFor(u.Instance);
            }

            foreach (Expansion e in nested.Values)
            {
                lines.AddRange(e.Lines);
            }

            Dictionary<string, (string Block, string Port)> outputs = [];
            foreach (ModuleOutput o in def.Outputs)
            {
                if (innerUses.Contains(o.Block))
                {
                    if (NestedFor(o.Block).Outputs.TryGetValue(o.Port, out (string Block, string Port) t))
                    {
                        outputs[o.Name] = t;
                    }
                    else
                    {
                        Diagnostics.Add(new ModuleDiagnostic("AMP5325", $"The output \"{o.Name}\" of the module \"{def.Name}\" points at \"{o.Block}.{o.Port}\", which does not exist.", reportLine, def.Name));
                    }
                }
                else if (innerBlocks.Contains(o.Block))
                {
                    outputs[o.Name] = (path + "/" + o.Block, o.Port);
                }
                else
                {
                    Diagnostics.Add(new ModuleDiagnostic("AMP5325", $"The output \"{o.Name}\" of the module \"{def.Name}\" points at the block \"{o.Block}\", which does not exist.", reportLine, def.Name));
                }
            }

            return new Expansion(lines, outputs);
        }

        private (string Block, string Port)? Source(FromLine f, string prefix, Func<string, Expansion?> sources, Func<FromLine, (string Block, string Port)?>? inner, int reportLine, string module)
        {
            if (inner is not null)
            {
                return inner(f);
            }

            Expansion? other = sources(f.Block);
            if (other is null)
            {
                return (f.Block, f.Output);
            }

            if (other.Outputs.TryGetValue(f.Output, out (string Block, string Port) t))
            {
                return t;
            }

            Diagnostics.Add(new ModuleDiagnostic("AMP5325", $"The module instance \"{f.Block}\" has no output \"{f.Output}\".", reportLine, module));
            return null;
        }

        private ModuleDefinition? Definition(UseNode use, int reportLine)
        {
            if (!definitions.TryGetValue(use.Module, out ModuleDefinition? def))
            {
                ModuleSource? source = resolve(use.Module);
                if (source is null)
                {
                    Diagnostics.Add(new ModuleDiagnostic("AMP5320", $"There is no module \"{use.Module}\" in the project, its linked folders or the packs.", reportLine, use.Module));
                    definitions[use.Module] = null;
                    return null;
                }

                def = ModuleDefinition.Parse(source.Text);
                if (def.Problems.Count > 0)
                {
                    Diagnostics.Add(new ModuleDiagnostic("AMP5326", $"The module file for \"{use.Module}\" ({source.Origin}) is damaged: {def.Problems[0]}", reportLine, use.Module));
                    definitions[use.Module] = null;
                    return null;
                }

                definitions[use.Module] = def;
                Used[use.Module] = new ModuleUsage(use.Module, def.Version, "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(source.Text))), source.Origin, Encoding.UTF8.GetByteCount(source.Text));
            }

            if (def is null)
            {
                return null;
            }

            if (!ModuleDefinition.Fits(use.Version, def.Version))
            {
                Diagnostics.Add(new ModuleDiagnostic("AMP5321", $"The song wants module \"{use.Module}\" {use.Version} but the file is version {def.Version} (same major version and not older is needed).", reportLine, use.Module));
                return null;
            }

            return def;
        }

        private string Substitute(string value, Dictionary<string, string> values, int reportLine, string module, bool inModule) => Placeholder.Replace(value, m =>
        {
            if (values.TryGetValue(m.Groups[1].Value, out string? v))
            {
                return v;
            }

            if (inModule)
            {
                Diagnostics.Add(new ModuleDiagnostic("AMP5324", $"The module \"{module}\" uses ${m.Groups[1].Value}, which it does not declare as a parameter.", reportLine, module));
            }

            return m.Value;
        });
    }
}
