using System.Text.Json.Nodes;
using Ample.Core.Abstractions;

namespace Ample.Audio.Engine;

/// <summary>
/// The sampler block: one WAV file of the project played on the notes it receives, moved up or down in pitch from a root note (the note at which the file sounds as recorded).
/// A <c>oneshot</c> sample plays to its end whatever the note length; a <c>hold</c> sample is cut with a short fade when the note ends.
/// </summary>
internal sealed class OneShotSamplerRenderer : IBlockRenderer
{
    public string BlockType => "sampler";

    public string Id => "builtin.sampler";

    public string Version => "2";

    public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
    {
        // a stereo file is played once per channel and the two renders are joined (A02)
        if (r.Probe is null && r.SampleChannel == 0) return SampledInstruments.Stereo(r, pass => RenderAsync(pass, cancellationToken));
        string Text(string name) => r.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"') is { Length: > 0 } v ? v : r.Template.Parameters.GetValueOrDefault(name) ?? string.Empty;
        int Number(string name, int fallback) => AudioEffects.Read(Text(name), fallback);
        short[] buffer = new short[r.BarCount * r.SamplesPerBar];
        if (Text("enabled") == "off")
        {
            return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("audio", Wav.Encode(buffer, r.SampleRate))]);
        }

        string value = Text("file");
        string path = ImportAudioRenderer.FileOf(r.ProjectRoot, r.Block) ?? throw new AmpleException("AMP5350", string.IsNullOrEmpty(value) ? "The sampler block has no file: choose a WAV file of the project." : $"The audio file '{value}' is not in the project.");
        short[] sample;
        try
        {
            byte[] wav = AudioFile.ReadWav(path);
            if (ChannelAudio.IsStereo(wav) && r.Probe is { } probe) probe.Seen = true;
            sample = SampleSet.ReadWav(wav, r.SampleRate, r.SampleChannel);
        }
        catch (InvalidDataException ex)
        {
            throw new AmpleException("AMP5350", $"The audio file '{value}' cannot be used: {ex.Message}");
        }

        int root = Math.Clamp(Number("root", 60), 0, 127);
        bool hold = Text("mode") == "hold";
        int fade = Math.Max(1, Math.Clamp(Number("release", 30), 1, 2000) * r.SampleRate / 1000);
        int gain = FixedAudio.GainFromDbWide(Math.Clamp(Number("gain", 0), -36, FixedAudio.WideMaxDb));
        Dictionary<int, short[]> shifted = [];
        JsonObject notes = r.Inputs.TryGetValue("notes", out byte[]? bytes) && JsonNode.Parse(bytes) is JsonObject parsed ? parsed : [];
        foreach (JsonObject note in (notes["notes"] as JsonArray ?? []).OfType<JsonObject>())
        {
            int bar = note["bar"]?.GetValue<int>() ?? 1;
            if (bar < r.FirstBar || bar >= r.FirstBar + r.BarCount)
            {
                continue;
            }

            int semitones = Math.Clamp((note["pitch"]?.GetValue<int>() ?? root) - root, -36, 36);
            int velocity = Math.Clamp(note["velocity"]?.GetValue<int>() ?? 96, 1, 127);
            decimal beat = note["beat"] is JsonValue b && b.TryGetValue(out decimal bd) ? bd : 0m;
            decimal beats = note["beats"] is JsonValue l && l.TryGetValue(out decimal ld) ? ld : 1m;
            long start = ((long)(bar - r.FirstBar) * r.SamplesPerBar) + (long)(beat * r.SamplesPerBar / r.BeatsPerBarDecimal);
            if (!shifted.TryGetValue(semitones, out short[]? pitched))
            {
                shifted[semitones] = pitched = semitones == 0 ? sample : SampleSet.Shift(sample, semitones);
            }

            int length = hold ? Math.Min(pitched.Length, Math.Max(1, (int)(beats * r.SamplesPerBar / r.BeatsPerBarDecimal))) : pitched.Length;
            long noteGain = (long)velocity * gain / 127;
            for (int i = 0; i < length && start + i < buffer.Length; i++)
            {
                if (start + i < 0)
                {
                    continue;
                }

                long v = (long)pitched[i] * noteGain >> 15;
                if (hold && i > length - fade)
                {
                    v = v * (length - i) / fade;
                }

                buffer[start + i] = FixedAudio.Clip(buffer[start + i] + v);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("audio", Wav.Encode(buffer, r.SampleRate))]);
    }
}
