namespace Ample.Language;

/// <summary>One parameter of an audio processor, as the effect block has it.</summary>
/// <param name="Name">The parameter name (the argument name in a call).</param>
/// <param name="Unit">The unit hint of the block parameter (<c>ms</c>, <c>%</c>, <c>dB</c>, <c>Hz</c>), or empty.</param>
/// <param name="Type">The type a value must have: <c>Duration</c>, <c>Percent</c>, <c>Gain</c>, <c>Frequency</c> or <c>Value</c> (written as it is).</param>
/// <param name="Default">The default of the block.</param>
public sealed record AudioProcessorParameter(string Name, string Unit, string Type, string Default);

/// <summary>An audio processor: an effect block that has an audio in and an audio out, seen as a function <c>Audio |&gt; name(arguments) -&gt; Audio</c>. The block and the call share this one definition (the template).</summary>
/// <param name="Name">The name in a pipeline (<c>delay</c>; <c>gain</c> for the volume block).</param>
/// <param name="Template">The block type it makes.</param>
/// <param name="Class">The update class written in the block header.</param>
/// <param name="Version">The template version written in the block header.</param>
/// <param name="Parameters">The parameters.</param>
/// <param name="Positional">The parameter a first argument without a name goes to, or null.</param>
/// <param name="ClientPreview">Whether the browser can preview it.</param>
/// <param name="LatencyMs">The latency it adds (a lookahead), 0 when it adds none.</param>
/// <param name="ExtraInputs">The audio inputs besides <c>in</c> (a sidechain <c>key</c>).</param>
public sealed record AudioProcessor(string Name, string Template, string Class, string Version, IReadOnlyList<AudioProcessorParameter> Parameters, string? Positional, bool ClientPreview, double LatencyMs, IReadOnlyList<string> ExtraInputs)
{
    /// <summary>Gets whether the browser can run it in real time while a knob moves (the template says so).</summary>
    public bool LivePreview { get; init; }

    /// <summary>Gets the output the stage hands on: <c>out</c> for sound, the control output (<c>level</c>) for a stage that turns a sound into a control (follow).</summary>
    public string OutputPort { get; init; } = "out";

    /// <summary>Gets whether the stage starts a pipeline instead of taking a value (<c>curve</c> makes a control out of nothing): <c>emit curve(value="auto(1:0, 4:1000)") -> sweep</c>.</summary>
    public bool Source { get; init; }

    /// <summary>Gets whether the stage hands on notes (<c>fire</c> turns a control into notes that an instrument can play).</summary>
    public bool Notes { get; init; }

    /// <summary>Gets a parameter by name.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The parameter, or null.</returns>
    public AudioProcessorParameter? Parameter(string name) => Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
}

/// <summary>The shared catalog of audio processors, derived from the block templates (one definition, two surfaces: a block and a call in a pipeline).</summary>
public static class AudioProcessors
{
    private static readonly HashSet<string> NotProcessors = new(StringComparer.Ordinal) { "group", "bus", "mixer", "join", "master", "send", "splitter", "crossfade", "variants", "import-audio", "ambience", "ace-music", "ai-voice", "voice-clone", "vocoder", "convolution", "delay-llama" };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal) { ["gain"] = "volume" };

    private static readonly Dictionary<string, string> PositionalParameter = new(StringComparer.Ordinal) { ["volume"] = "gain", ["pan"] = "pan", ["silence"] = "length" };

    private static string TypeOf(string unit) => unit switch
    {
        "ms" or "s" => "Duration",
        "%" => "Percent",
        "dB" or "dBFS" or "LUFS" => "Gain",
        "Hz" => "Frequency",
        _ => "Value",
    };

    private static double Latency(TemplateSpec t) => t.LatencyMs;

    /// <summary>The instruments: the blocks that turn Notes into Audio (a <c>notes</c> input of the midi stream and an <c>audio</c> output: piano, bass, guitar, synth, gm, sampler, delay-llama). In the process of a component they stand where a Notes input becomes sound: <c>notes |> piano() |> reverb()</c>.</summary>
    /// <param name="spec">The language data.</param>
    /// <returns>The instruments by name.</returns>
    public static IReadOnlyDictionary<string, AudioProcessor> Instruments(SpecData spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        Dictionary<string, AudioProcessor> result = new(StringComparer.Ordinal);
        foreach (TemplateSpec t in spec.Templates.Values.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            if (t.Component || !t.Inputs.Any(i => i.Name == "notes" && i.Type == "midi") || !t.Outputs.Any(o => o.Name == "audio" && o.Type == "audio")) continue;
            List<AudioProcessorParameter> parameters = [];
            foreach (TemplateParameterSpec p in t.ParameterSpecs.Values.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                string unit = p.Unit ?? string.Empty;
                parameters.Add(new AudioProcessorParameter(p.Name, unit, TypeOf(unit), p.Default ?? string.Empty));
            }

            result[t.Id] = new AudioProcessor(t.Id, t.Id, t.Class, t.Version, parameters, null, t.ClientPreview, 0, []) { LivePreview = t.LivePreview };
        }

        return result;
    }

    /// <summary>Builds the catalog from the templates of a spec.</summary>
    /// <param name="spec">The language data.</param>
    /// <returns>The processors by the name they have in a pipeline.</returns>
    public static IReadOnlyDictionary<string, AudioProcessor> Catalog(SpecData spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        Dictionary<string, AudioProcessor> result = new(StringComparer.Ordinal);
        foreach (TemplateSpec t in spec.Templates.Values.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            if (t.Component || NotProcessors.Contains(t.Id)) continue;
            PortSpec? controlOut = t.Outputs.FirstOrDefault(o => ControlTypes.Is(o.Type));
            bool source = t.Inputs.Count == 0 && (controlOut is not null || t.Id == "silence");   // curve: a control from nothing
            PortSpec? notesOut = t.Inputs.Any(i => i.Name == "in" && ControlTypes.Is(i.Type)) ? t.Outputs.FirstOrDefault(o => o.Type == "midi") : null;   // fire: notes from a control
            if (!source && notesOut is null && (!t.Inputs.Any(i => i.Name == "in" && (i.Type == "audio" || ControlTypes.Is(i.Type))) || (!t.Outputs.Any(o => o.Name == "out" && o.Type == "audio") && controlOut is null))) continue;
            List<AudioProcessorParameter> parameters = [];
            foreach (TemplateParameterSpec p in t.ParameterSpecs.Values.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                string unit = p.Unit ?? string.Empty;
                parameters.Add(new AudioProcessorParameter(p.Name, unit, TypeOf(unit), p.Default ?? string.Empty));
            }

            string name = Aliases.FirstOrDefault(a => a.Value == t.Id).Key ?? t.Id;
            string[] extra = [.. t.Inputs.Where(i => i.Type == "audio" && i.Name != "in").Select(i => i.Name)];
            result[name] = new AudioProcessor(name, t.Id, t.Class, t.Version, parameters, PositionalParameter.GetValueOrDefault(t.Id), t.ClientPreview, Latency(t), extra) { LivePreview = t.LivePreview, OutputPort = notesOut is not null ? notesOut.Name : controlOut is not null && !t.Outputs.Any(o => o.Name == "out") ? controlOut.Name : "out", Source = source, Notes = notesOut is not null };
        }

        return result;
    }
}
