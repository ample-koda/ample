using System.Text.RegularExpressions;

namespace Ample.Language;

public static partial class ComponentExpander
{
    /// <summary>What an audio instance is going to become: the processors it holds, and where each of its outputs comes from.</summary>
    private sealed record AudioPlan(string Name, BlockNode Block, AudioGraph Graph, Dictionary<string, (string Block, string Port)> Outputs, Dictionary<string, string> InputPorts)
    {
        /// <summary>Gets the inputs that may stay unconnected.</summary>
        public HashSet<string> Optional { get; init; } = new(StringComparer.Ordinal);
    }

    private static string NodeBlock(string instance, AudioNode node) => Edits.Quote(instance + "__" + node.Id);

    private static AudioPlan PlanAudio(BlockNode b, ComponentDefinition def, SpecData spec, double tempo, List<ModuleDiagnostic> diagnostics, IReadOnlyList<ComponentDefinition> visible)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (ComponentProperty p in def.Properties)
        {
            Parameter? given = b.Parameters.LastOrDefault(x => x.Name == p.Name);
            string text = given?.Value.Trim() ?? p.Default ?? string.Empty;
            if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1];
            values[p.Name] = text;
        }

        AudioGraph graph = AudioGraphBuilder.Build(def, spec, values, tempo, visible);
        foreach (ComponentIssue issue in graph.Issues) diagnostics.Add(new ModuleDiagnostic(issue.Code, $"{b.Type} \"{b.Name}\": {issue.Message}", b.Line, b.Type));
        Dictionary<string, (string Block, string Port)> outputs = new(StringComparer.Ordinal);
        foreach ((string output, string source) in graph.Outputs)
        {
            if (source.StartsWith("node:", StringComparison.Ordinal))
            {
                AudioNode node = graph.Nodes.First(n => n.Id == source[5..]);
                outputs[output] = (NodeBlock(b.Name, node), node.OutputPort);
            }
            else outputs[output] = (string.Empty, source[3..]);   // an input that goes straight to an output: resolved with the connection of the instance
        }

        return new AudioPlan(b.Name, b, graph, outputs, def.Inputs.Where(i => i.Type.TrimEnd('?') is "Audio" or "Notes").ToDictionary(i => i.Name, i => i.Name, StringComparer.Ordinal)) { Optional = [.. def.Inputs.Where(i => i.Optional).Select(i => i.Name)] };
    }

    /// <summary>Resolves where a connection of an instance comes from: the block and the port of the one that feeds it, following an audio instance to the processor that gives that output.</summary>
    private static (string Block, string Port)? Source(FromLine from, Dictionary<string, AudioPlan> plans, BlockNode owner, int depth = 0)
    {
        if (depth > 16) return null;
        if (!plans.TryGetValue(from.Block, out AudioPlan? plan)) return (from.RawBlock, from.RawOutput);
        if (!plan.Outputs.TryGetValue(from.Output, out (string Block, string Port) found)) return null;
        if (found.Block.Length > 0) return found;
        // an input that goes straight to the output of that instance: the reader gets what feeds that input (pass-through, no block is made)
        FromLine? feeder = plan.Block.Froms.FirstOrDefault(x => !x.Disabled && x.Input == found.Port);
        return feeder is null ? null : Source(feeder, plans, owner, depth + 1);
    }

    /// <summary>The placement of an instance as its header writes it (<c> at 2 for 3</c>), or empty.</summary>
    private static string PlacementOf(string header)
    {
        Match rest = HeaderRest().Match(header);
        return rest.Success ? rest.Groups[2].Value.TrimEnd() : string.Empty;
    }

    private static string[] LowerAudio(BlockNode b, AudioPlan plan, Dictionary<string, AudioPlan> plans, string header, List<ModuleDiagnostic> diagnostics, Func<string, string> placementOfBlock)
    {
        string indent = header.Length - header.TrimStart().Length is var n && n > 0 ? header[..n] : string.Empty;
        List<string> lines = [];
        foreach (AudioNode node in plan.Graph.Nodes)
        {
            // an instrument plays the notes of the instance: it takes the placement (at 2 for 3) the instance has
            // when the instance has none of its own, it plays where the notes it reads play: the placement of that clip
            // a Notes component called before the instrument is a midi-clip block (its notes are the call of the component) that the instrument reads, and it plays where the instrument does
            string placement = string.Empty;
            if (node.Inputs.Any(i => i.Port is "notes" or "~src"))
            {
                placement = PlacementOf(header);
                string notes = node.Inputs.First(i => i.Port is "notes" or "~src").Source;
                for (int hops = 0; notes.StartsWith("node:", StringComparison.Ordinal) && hops < 16; hops++)
                    notes = plan.Graph.Nodes.First(x => x.Id == notes[5..]).Inputs.FirstOrDefault(i => i.Port is "notes" or "~src").Source ?? string.Empty;
                if (placement.Length == 0 && notes.StartsWith("in:", StringComparison.Ordinal) && b.Froms.FirstOrDefault(f => !f.Disabled && f.Input == notes[3..]) is { } feeder)
                    placement = placementOfBlock(feeder.RawBlock);
            }

            List<string> connections = [];
            Dictionary<string, string> references = new(StringComparer.Ordinal);
            foreach ((string port, string source) in node.Inputs)
            {
                (string Block, string Port)? from = null;
                if (source.StartsWith("node:", StringComparison.Ordinal))
                {
                    AudioNode other = plan.Graph.Nodes.First(x => x.Id == source[5..]);
                    from = (NodeBlock(b.Name, other), other.OutputPort);
                }
                else
                {
                    FromLine? connection = b.Froms.FirstOrDefault(f => !f.Disabled && f.Input == source[3..]);
                    if (connection is not null) from = Source(connection, plans, b);
                    if (from is null && port is "in" or "notes" && !plan.Optional.Contains(source[3..])) diagnostics.Add(new ModuleDiagnostic("AMP2218", $"The input \"{source[3..]}\" of {b.Type} \"{b.Name}\" is not connected. Add: from <block>.out -> {source[3..]}", b.Line, b.Type));
                }

                if (from is { } f2)
                {
                    if (port.StartsWith('~')) references[port] = $"{f2.Block}.{f2.Port}";
                    else connections.Add($"{indent}  from {f2.Block}.{f2.Port} -> {port}");
                }
                else if (port.StartsWith('~')) references[port] = "emptyNotes()";
            }

            lines.Add($"{indent}{node.Template} {NodeBlock(b.Name, node)}{placement} {{");
            foreach ((string name, string value) in node.Parameters) lines.Add($"{indent}  {name} {references.Aggregate(value, (text, r) => text.Replace("{" + r.Key + "}", r.Value, StringComparison.Ordinal))}");
            lines.AddRange(connections);
            lines.Add(indent + "}");
            lines.Add(string.Empty);
        }

        if (lines.Count > 0) lines.RemoveAt(lines.Count - 1);
        return [.. lines];
    }

    /// <summary>The connections that read an output of an audio instance: they now read the output of the processor that gives it.</summary>
    private static Dictionary<int, string> AudioConsumers(AmpleDocument doc, Dictionary<string, AudioPlan> plans, Dictionary<int, string[]> replacement, List<ModuleDiagnostic> diagnostics)
    {
        Dictionary<int, string> result = [];
        if (plans.Count == 0) return result;
        foreach (BlockNode block in doc.Blocks.Where(x => !replacement.ContainsKey(x.Line)))
        {
            foreach (FromLine f in block.Froms.Where(f => !f.Disabled && plans.ContainsKey(f.Block)))
            {
                (string Block, string Port)? source = Source(f, plans, block);
                if (source is { } s) result[f.Line] = $"from {s.Block}.{s.Port} -> {f.RawInput}";
                else diagnostics.Add(new ModuleDiagnostic("AMP2231", $"\"{f.Block}\" has no Audio output \"{f.Output}\".", f.Line, block.Type));
            }
        }

        return result;
    }
}
