using System.Text;
using System.Text.RegularExpressions;
using Ample.Language;

namespace Ample.Server;

/// <summary>An export marker kept in the song as a comment line: <c># export bake &lt;block&gt;</c> (use the rendered output of this block as the new source in a Derived export) or <c># export as-is &lt;block&gt;[.port]</c> (always include this source as it is).</summary>
internal sealed partial class TxMarks
{
    /// <summary>Gets the blocks that are a Derived export boundary.</summary>
    public SortedSet<string> Bake { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the sources (<c>block</c> or <c>block.port</c>) that are always included as they are.</summary>
    public SortedSet<string> AsIs { get; } = new(StringComparer.Ordinal);

    /// <summary>Reads the marker lines of a song.</summary>
    public static TxMarks Parse(string text)
    {
        TxMarks marks = new();
        foreach (string line in text.Split('\n'))
        {
            Match m = MarkLine().Match(line.TrimEnd('\r'));
            if (!m.Success)
            {
                continue;
            }

            (m.Groups[1].Value == "bake" ? marks.Bake : marks.AsIs).Add(m.Groups[2].Value);
        }

        return marks;
    }

    /// <summary>The song text with one marker set or cleared (<paramref name="kind"/> is <c>bake</c>, <c>as-is</c> or <c>none</c> to clear both).</summary>
    public static string With(string text, string target, string kind)
    {
        List<string> lines = [.. text.Split('\n')];
        lines.RemoveAll(l => MarkLine().Match(l.TrimEnd('\r')) is { Success: true } m && m.Groups[2].Value == target && (kind == "none" || m.Groups[1].Value == kind));
        if (kind is "bake" or "as-is")
        {
            while (lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            lines.Add($"# export {kind} {target}");
            lines.Add(string.Empty);
        }

        return string.Join('\n', lines);
    }

    [GeneratedRegex(@"^#\s*export\s+(bake|as-is)\s+(\S+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex MarkLine();
}

/// <summary>A connection between two blocks of the song.</summary>
internal sealed record TxEdge(string FromBlock, string FromPort, string ToBlock, string ToInput, int Line, bool Disabled);

/// <summary>A place where a Derived export swaps the original graph for rendered audio: the output <c>Port</c> of <c>Block</c> becomes an audio file.</summary>
internal sealed record TxCut(string Block, string Port, bool Explicit, string Origin, bool RawSource)
{
    /// <summary>Gets the key <c>block.port</c>.</summary>
    public string Key => Block + "." + Port;
}

/// <summary>What a Derived export would do to the graph.</summary>
internal sealed class TxDerivedPlan
{
    /// <summary>Gets the cuts that are used.</summary>
    public List<TxCut> Cuts { get; } = [];

    /// <summary>Gets the blocks that stay in the exported graph.</summary>
    public SortedSet<string> Live { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the sources that are still embedded as they are.</summary>
    public SortedSet<string> KeptSources { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the problems that must be solved before the export (marker conflicts, boundaries that cannot render).</summary>
    public List<string> Conflicts { get; } = [];

    /// <summary>Gets the warnings.</summary>
    public List<string> Warnings { get; } = [];
}

/// <summary>The structure of a song as a graph, for export: what is active, what is a source, where a Derived export can swap in rendered audio.</summary>
internal sealed class TxGraph
{
    private static readonly string[] MainOutputs = ["master", "mix", "mixed", "audio", "out"];
    private static readonly HashSet<string> Effects = new(["reverb", "eq", "compressor", "delay", "chorus", "filter", "distortion", "bitcrusher", "flanger", "phaser", "gate", "limiter", "deesser", "width", "normalize", "reverse", "rewind", "tremolo", "peq", "denoise", "convolution", "granular", "ambience", "meter", "pitch", "stretch", "shift", "cut", "fade", "volume", "pan", "duck", "scratch", "autotune", "vocoder", "chop"], StringComparer.Ordinal);
    private static readonly HashSet<string> Merges = new(["mixer", "bus", "master", "crossfade"], StringComparer.Ordinal);

    private readonly Dictionary<string, BlockNode> blocks;
    private readonly List<TxEdge> edges = [];
    private readonly SpecData spec;

    public TxGraph(AmpleDocument doc, SpecData spec)
    {
        Doc = doc;
        this.spec = spec;
        blocks = doc.Blocks.Where(b => !b.Skipped).GroupBy(b => b.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (BlockNode b in blocks.Values)
        {
            foreach (FromLine f in b.Froms)
            {
                if (blocks.ContainsKey(f.Block))
                {
                    edges.Add(new TxEdge(f.Block, f.Output, b.Name, f.Input, f.Line, f.Disabled));
                }
            }
        }

        Master = doc.Blocks.LastOrDefault(b => !b.Skipped && b.Type == "master") ?? doc.Blocks.LastOrDefault(b => !b.Skipped && spec.Templates.TryGetValue(b.Type, out TemplateSpec? t) && t.Outputs.Any(o => o.Type == "audio"));
        Active = [];
        if (Master is not null)
        {
            Stack<string> todo = new([Master.Name]);
            while (todo.Count > 0)
            {
                string name = todo.Pop();
                if (!blocks.TryGetValue(name, out BlockNode? b) || IsDisabled(b) || !Active.Add(name))
                {
                    continue;
                }

                foreach (TxEdge e in edges.Where(x => x.ToBlock == name && !x.Disabled))
                {
                    todo.Push(e.FromBlock);
                }
            }
        }
    }

    /// <summary>Gets the song.</summary>
    public AmpleDocument Doc { get; }

    /// <summary>Gets the block that is the final output.</summary>
    public BlockNode? Master { get; }

    /// <summary>Gets the names of the blocks that contribute to the final output (traced backwards from it).</summary>
    public HashSet<string> Active { get; }

    /// <summary>Gets the block by name.</summary>
    public BlockNode? Block(string name) => blocks.GetValueOrDefault(name);

    /// <summary>Gets all blocks.</summary>
    public IEnumerable<BlockNode> All => blocks.Values;

    /// <summary>Gets the connections.</summary>
    public IReadOnlyList<TxEdge> Edges => edges;

    /// <summary>Whether the block is switched off in its header.</summary>
    public static bool IsDisabled(BlockNode b) => b.Flags.Contains("disabled");

    private TemplateSpec? Template(BlockNode b) => spec.Templates.GetValueOrDefault(b.Type);

    /// <summary>The output that carries the sound of the block as a whole (a take of the block can be rendered from it), or null when the block makes no audio.</summary>
    public string? MainOut(BlockNode b)
    {
        TemplateSpec? t = Template(b);
        if (t is null)
        {
            return null;
        }

        string[] audio = [.. t.Outputs.Where(o => o.Type == "audio").Select(o => o.Name)];
        return MainOutputs.FirstOrDefault(audio.Contains) ?? audio.FirstOrDefault();
    }

    /// <summary>The first audio input of the block (the one the sound passes through), or null.</summary>
    public string? MainIn(BlockNode b) => Template(b)?.Inputs.FirstOrDefault(i => i.Type == "audio")?.Name;

    /// <summary>The output ports of a block that carry audio.</summary>
    public IEnumerable<string> AudioOuts(BlockNode b) => Template(b)?.Outputs.Where(o => o.Type == "audio").Select(o => o.Name) ?? [];

    /// <summary>The connections that leave an output port towards active blocks.</summary>
    public IEnumerable<TxEdge> Consumers(string block, string port) => edges.Where(e => e.FromBlock == block && e.FromPort == port && !e.Disabled && Active.Contains(e.ToBlock));

    /// <summary>Whether the block is a merge point (a mixer): the sound of several paths is added there.</summary>
    public static bool IsMerge(BlockNode b) => Merges.Contains(b.Type);

    /// <summary>Whether the block changes the one sound that passes through it (an effect).</summary>
    public static bool IsEffect(BlockNode b) => Effects.Contains(b.Type);

    /// <summary>Whether a block is reachable downstream of an output.</summary>
    public bool Reaches(string fromBlock, string? fromPort, string target)
    {
        HashSet<string> seen = [];
        Stack<string> todo = new();
        foreach (TxEdge e in edges.Where(e => e.FromBlock == fromBlock && (fromPort is null || e.FromPort == fromPort) && !e.Disabled && Active.Contains(e.ToBlock)))
        {
            todo.Push(e.ToBlock);
        }

        while (todo.Count > 0)
        {
            string at = todo.Pop();
            if (at == target)
            {
                return true;
            }

            if (!seen.Add(at))
            {
                continue;
            }

            foreach (TxEdge e in edges.Where(e => e.FromBlock == at && !e.Disabled && Active.Contains(e.ToBlock)))
            {
                todo.Push(e.ToBlock);
            }
        }

        return false;
    }

    /// <summary>Works out where a Derived export swaps the graph for rendered audio. <paramref name="isSource"/> says which blocks read files of their own.</summary>
    public TxDerivedPlan PlanDerived(TxMarks marks, Func<BlockNode, bool> isSource)
    {
        TxDerivedPlan plan = new();
        Dictionary<string, TxCut> candidates = new(StringComparer.Ordinal);
        bool AsIsPort(string block, string port) => marks.AsIs.Contains(block) || marks.AsIs.Contains(block + "." + port);

        foreach (string name in marks.Bake)
        {
            BlockNode? b = Block(name);
            string? main = b is null ? null : MainOut(b);
            if (b is null || !Active.Contains(name))
            {
                plan.Warnings.Add($"The boundary on {name} is ignored: the block is not part of the final result.");
            }
            else if (main is null)
            {
                plan.Conflicts.Add($"{name} cannot be a Derived export boundary: it makes no audio that can be rendered.");
            }
            else
            {
                candidates[name + "." + main] = new TxCut(name, main, true, name + "." + main, false);
            }
        }

        foreach (string asIs in marks.AsIs)
        {
            string block = asIs.Split('.')[0];
            BlockNode? b = Block(block);
            if (b is null || !Active.Contains(block))
            {
                plan.Warnings.Add($"Always include as-is on {asIs} is ignored: the block is not part of the final result.");
                continue;
            }

            if (!isSource(b))
            {
                plan.Conflicts.Add($"Always include as-is only applies to sources that read files; {block} does not read any.");
                continue;
            }

            foreach (TxCut e in candidates.Values.Where(c => c.Explicit))
            {
                string? port = asIs.Contains('.', StringComparison.Ordinal) ? asIs[(block.Length + 1)..] : null;
                if (e.Block == block || Reaches(block, port, e.Block))
                {
                    plan.Conflicts.Add($"{asIs} is set to be included as it is, but {e.Block} is a Derived boundary on the same path: choose one of them.");
                }
            }
        }

        foreach (BlockNode source in blocks.Values.Where(b => Active.Contains(b.Name) && isSource(b)))
        {
            foreach (string port in AudioOuts(source))
            {
                TxEdge[] consumers = [.. Consumers(source.Name, port)];
                if (consumers.Length == 0 || AsIsPort(source.Name, port))
                {
                    continue;
                }

                foreach (TxEdge edge in consumers)
                {
                    BlockNode target = blocks[edge.ToBlock];
                    string? targetMain = MainOut(target);
                    if (IsEffect(target) && edge.ToInput == MainIn(target) && targetMain is not null)
                    {
                        candidates.TryAdd(target.Name + "." + targetMain, new TxCut(target.Name, targetMain, false, source.Name + "." + port, false));
                    }
                    else
                    {
                        candidates.TryAdd(source.Name + "." + port, new TxCut(source.Name, port, false, source.Name + "." + port, true));
                    }
                }
            }
        }

        // The graph that is left: trace backwards from the final output and stop at the first boundary on each path.
        HashSet<string> reached = new(StringComparer.Ordinal);
        Stack<string> work = new();
        if (Master is not null)
        {
            work.Push(Master.Name);
        }

        while (work.Count > 0)
        {
            string name = work.Pop();
            if (!plan.Live.Add(name))
            {
                continue;
            }

            foreach (TxEdge e in edges.Where(x => x.ToBlock == name && !x.Disabled && Active.Contains(x.FromBlock)))
            {
                string key = e.FromBlock + "." + e.FromPort;
                if (candidates.ContainsKey(key))
                {
                    reached.Add(key);
                }
                else
                {
                    work.Push(e.FromBlock);
                }
            }
        }

        plan.Cuts.AddRange(candidates.Values.Where(c => reached.Contains(c.Key)).OrderBy(c => c.Key, StringComparer.Ordinal));
        foreach (string name in plan.Live.Where(n => isSource(blocks[n])))
        {
            plan.KeptSources.Add(name);
            bool marked = marks.AsIs.Any(a => a.Split('.')[0] == name);
            if (!marked)
            {
                plan.Warnings.Add($"{name} reads files of its own and no Derived boundary replaces it, so its files are included as they are.");
            }
        }

        return plan;
    }

    /// <summary>The blocks that a cut replaces: everything upstream of it that is not needed elsewhere in the exported graph.</summary>
    public SortedSet<string> BakedBy(TxCut cut, IReadOnlySet<string> live)
    {
        SortedSet<string> baked = new(StringComparer.Ordinal);
        Stack<string> todo = new([cut.Block]);
        while (todo.Count > 0)
        {
            string name = todo.Pop();
            if (live.Contains(name) || !Active.Contains(name) || !baked.Add(name))
            {
                continue;
            }

            foreach (TxEdge e in edges.Where(x => x.ToBlock == name && !x.Disabled))
            {
                todo.Push(e.FromBlock);
            }
        }

        return baked;
    }

    /// <summary>The song text of a Derived export: only the blocks that stay, with the cut places connected to new audio inputs.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="assetFile">The project-relative file of each cut (by key) and the number of bars it lasts.</param>
    /// <param name="inputName">The block name of the audio input of each cut (by key).</param>
    public string DerivedText(TxDerivedPlan plan, IReadOnlyDictionary<string, (string File, int Bars)> assetFile, IReadOnlyDictionary<string, string> inputName)
    {
        List<string> lines = [.. Doc.Lines];
        bool[] drop = new bool[lines.Count];
        foreach (BlockNode b in blocks.Values.Where(b => !plan.Live.Contains(b.Name)))
        {
            for (int i = b.Line; i <= (b.CloseLine < 0 ? b.Line : b.CloseLine) && i < lines.Count; i++)
            {
                drop[i] = true;
            }

            // comments are never removed: the comment lines above and inside a left-out block stay in the text
            for (int i = b.Line; i <= (b.CloseLine < 0 ? b.Line : b.CloseLine) && i < lines.Count; i++)
            {
                if (lines[i].TrimStart().StartsWith('#'))
                {
                    drop[i] = false;
                }
            }
        }

        foreach (BlockNode b in blocks.Values.Where(b => plan.Live.Contains(b.Name)))
        {
            foreach (FromLine f in b.Froms)
            {
                string key = f.Block + "." + f.Output;
                if (inputName.TryGetValue(key, out string? input))
                {
                    string indent = new([.. lines[f.Line].TakeWhile(char.IsWhiteSpace)]);
                    lines[f.Line] = $"{indent}from {input}.audio -> {f.RawInput}" + (f.Disabled ? " disabled" : string.Empty);
                }
            }
        }

        HashSet<string> gone = [.. blocks.Keys.Where(n => !plan.Live.Contains(n))];
        Regex view = new(@"^#\s*view\s+(?:pin|size)\s+(\S+)", RegexOptions.CultureInvariant);
        StringBuilder text = new();
        for (int i = 0; i < lines.Count; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (drop[i])
            {
                continue;
            }

            // a marker or view line that names something that is not here anymore stays as a plain comment, so it cannot act on a block that is gone
            bool stale = trimmed.StartsWith("# export ", StringComparison.Ordinal) || (view.Match(trimmed) is { Success: true } m && gone.Contains(m.Groups[1].Value));
            text.Append(stale ? new string([.. lines[i].TakeWhile(char.IsWhiteSpace)]) + "# (Derived) " + trimmed.TrimStart('#').TrimStart() : lines[i]);
            if (i < lines.Count - 1)
            {
                text.Append('\n');
            }
        }

        string result = text.ToString().TrimEnd('\n') + "\n";
        StringBuilder added = new();
        added.Append("\n# ---- the audio inputs of a Derived export: rendered from the original graph, so what came before them is no longer here ----\n");
        foreach (TxCut cut in plan.Cuts)
        {
            (string file, int bars) = assetFile[cut.Key];
            BlockNode origin = blocks[cut.Block];
            List<string> baked = [.. BakedBy(cut, plan.Live)];
            added.Append(FormattableString.Invariant($"# Baked from {cut.Key} ({(cut.Explicit ? "explicit boundary" : cut.RawSource ? "straight from the source" : "automatic boundary")}); it replaces: {string.Join(", ", baked)}\n"));
            added.Append(FormattableString.Invariant($"import-audio {inputName[cut.Key]} at 1 for {bars} {{\n  file {file}\n}}\n"));
            _ = origin;
        }

        return result + added;
    }
}
