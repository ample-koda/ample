using System.Text.Json.Nodes;

namespace Ample.Audio.Engine;

/// <summary>
/// The pitch bend of notes: the curve of a midi-clip (<c>bend auto(1:0, 2:100)</c>, percent of <c>bend_range</c> semitones, 2 when not given) and the bend of single notes
/// (<c>;bend=200</c> on a note: a glide from its own pitch at the start to 2 semitones higher at its end) bend the sound of the block that plays the notes. The audio of that block
/// is moved by the curve with the spectral phase vocoder of the autotune (the colour moves along, like a pitch wheel on a sampler). Every sound at that moment bends: on a single
/// line the bend of a note is exactly that note; under a chord the whole chord bends.
/// </summary>
public static class PitchBend
{
    /// <summary>The bend in semitones per sample of the notes that reach a block, or null when nothing bends.</summary>
    /// <param name="notes">The notes JSON of the midi input.</param>
    /// <param name="firstBar">The first bar of the block.</param>
    /// <param name="samplesPerBar">Samples in one bar at <paramref name="rate"/>.</param>
    /// <param name="beatsPerBar">Quarter notes in one bar (4 in 4/4).</param>
    /// <param name="rate">Samples per second of the audio.</param>
    /// <param name="length">Samples of the audio.</param>
    public static double[]? Curve(byte[] notes, int firstBar, int samplesPerBar, int rate, int length, decimal beatsPerBar = 4m)
    {
        if (length <= 0 || JsonNode.Parse(notes) is not JsonObject root)
        {
            return null;
        }

        double[] curve = new double[length];
        bool any = false;
        if (root["bend"] is JsonValue bendValue && bendValue.TryGetValue(out string? text) && Automation.Is(text))
        {
            int range = root["bend_range"] is JsonValue r && r.TryGetValue(out int given) ? Math.Clamp(given, 1, 48) : 2;
            int[] cents = Automation.CurveAt(Automation.ParseSamples(text, samplesPerBar, rate), firstBar, samplesPerBar, length, value => Math.Clamp(value, -100, 100) * range);
            for (int i = 0; i < length; i++)
            {
                curve[i] = cents[i] / 100.0;
                any |= cents[i] != 0;
            }
        }

        foreach (JsonObject note in (root["notes"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (note["bend"] is not JsonValue b || !b.TryGetValue(out int bend) || bend == 0)
            {
                continue;
            }

            int bar = note["bar"]?.GetValue<int>() ?? 1;
            decimal beat = note["beat"] is JsonValue bv && bv.TryGetValue(out decimal bd) ? bd : 0m;
            decimal beats = note["beats"] is JsonValue lv && lv.TryGetValue(out decimal ld) ? ld : 1m;
            long from = ((long)(bar - firstBar) * samplesPerBar) + (long)(beat * samplesPerBar / beatsPerBar);
            long to = from + Math.Max(1, (long)(beats * samplesPerBar / beatsPerBar));
            for (long i = Math.Max(0, from); i < Math.Min(length, to); i++)
            {
                curve[i] += Math.Clamp(bend, -2400, 2400) / 100.0 * (i - from) / Math.Max(1, to - from);
            }

            any = true;
        }

        return any ? curve : null;
    }

    /// <summary>Bends the audio outputs of a block whose midi input carries a bend (the other outputs stay as they are).</summary>
    public static IReadOnlyList<RenderOutput> Apply(IReadOnlyList<RenderOutput> outputs, RenderRequest r, CancellationToken cancellationToken)
    {
        string? input = r.Template.Inputs.FirstOrDefault(p => p.Type == "midi" && r.Inputs.ContainsKey(p.Name))?.Name;
        if (input is null || r.Block.Type == "delay-llama")
        {
            return outputs;
        }

        byte[] notes = r.Inputs[input];
        if (notes.AsSpan().IndexOf("\"bend\""u8) < 0)
        {
            return outputs;   // the usual case: no bend anywhere, the audio is not even read
        }

        HashSet<string> audioRoles = [.. r.Template.Outputs.Where(p => p.Type == "audio").Select(p => p.Name)];
        List<RenderOutput> result = [];
        bool changed = false;
        foreach (RenderOutput output in outputs)
        {
            if (!audioRoles.Contains(output.Role))
            {
                result.Add(output);
                continue;
            }

            bool stereo = ChannelAudio.IsStereo(output.Data);
            (StereoBuffer channels, int rate) = ChannelAudio.Decode(output.Data);
            int samplesPerBar = (int)((long)r.SamplesPerBar * rate / Math.Max(1, r.SampleRate));
            double[]? curve = Curve(notes, r.FirstBar, samplesPerBar, rate, channels.Left.Length);
            if (curve is null)
            {
                return outputs;   // nothing bends: the very same bytes
            }

            short[][] source = stereo ? [channels.Left, channels.Right] : [channels.Left];
            double[][] bent = Autotune.Spectral(source, rate, position => curve[(int)Math.Clamp(position, 0, curve.Length - 1)], false, cancellationToken);
            short[][] clipped = [.. bent.Select(channel => channel.Select(v => (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue)).ToArray())];
            result.Add(output with { Data = stereo ? StereoWav.Encode(new StereoBuffer(clipped[0], clipped[1]), rate) : Wav.Encode(clipped[0], rate) });
            changed = true;
        }

        return changed ? result : outputs;
    }

    /// <summary>Bends one channel buffer of the sequencer by the bends of its notes: (start, length in samples, bend in cents).</summary>
    internal static short[] Notes(short[] buffer, int rate, IReadOnlyList<(int Start, int Length, int Bend)> bends, CancellationToken cancellationToken)
    {
        if (bends.Count == 0)
        {
            return buffer;
        }

        double[] curve = new double[buffer.Length];
        foreach ((int start, int length, int bend) in bends)
        {
            for (int i = Math.Max(0, start); i < Math.Min(buffer.Length, start + length); i++)
            {
                curve[i] += Math.Clamp(bend, -2400, 2400) / 100.0 * (i - start) / Math.Max(1, length);
            }
        }

        double[] bent = Autotune.Spectral([buffer], rate, position => curve[(int)Math.Clamp(position, 0, curve.Length - 1)], false, cancellationToken)[0];
        return [.. bent.Select(v => (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue))];
    }
}
