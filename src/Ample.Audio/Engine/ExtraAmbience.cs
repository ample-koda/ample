using System.Globalization;
using Ample.Core.Abstractions;

namespace Ample.Audio.Engine;

/// <summary>A seamless ambience from a recording: long stretches of it are chained with equal-power crossfades, starting at random places, so a rain or a room tone fills any length without a loop that can be heard.</summary>
internal sealed class AmbienceRenderer : IBlockRenderer
{
    public string BlockType => "ambience";

    public string Id => "builtin.ambience";

    public string Version => "1";

    public string Determinism => "machine-exact";

    public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
    {
        string Text(string name) => r.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"') is { Length: > 0 } v ? v : r.Template.Parameters.GetValueOrDefault(name) ?? string.Empty;
        double Num(string name, double fallback) => double.TryParse(new string(Text(name).TakeWhile(c => char.IsDigit(c) || c is '.' or '-').ToArray()), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;
        int total = r.BarCount * r.SamplesPerBar;
        int startBar = r.Block.Parts.Count > 0 ? r.Block.Parts[0].At : 1;
        long offset = (long)(r.FirstBar - startBar) * r.SamplesPerBar;
        string value = Text("file");
        if (Text("enabled") == "off" || string.IsNullOrEmpty(value))
        {
            if (string.IsNullOrEmpty(value) && Text("enabled") != "off")
            {
                throw new AmpleException("AMP5350", "The ambience block has no file: choose a recording of the project.");
            }

            byte[] silence = Wav.Encode(new short[total], r.SampleRate);
            return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("audio", silence), new RenderOutput("out", silence)]);
        }

        string path = ImportAudioRenderer.FileOf(r.ProjectRoot, r.Block, r.AmpleRoot) ?? throw new AmpleException("AMP5350", $"The audio file '{value}' is not in the project.");
        byte[] bytes = AudioFile.ReadWav(path);
        short[] sl, sr;
        bool stereo = ChannelAudio.IsStereo(bytes) && BitConverter.ToInt32(bytes, 24) == r.SampleRate && BitConverter.ToInt16(bytes, 34) == 16;
        try
        {
            if (stereo)
            {
                StereoBuffer b = StereoWav.Decode(bytes).Buffer;
                sl = b.Left;
                sr = b.Right;
            }
            else
            {
                sl = SampleSet.ReadWav(bytes, r.SampleRate);
                sr = sl;
            }
        }
        catch (InvalidDataException ex)
        {
            throw new AmpleException("AMP5350", $"The audio file '{value}' cannot be used: {ex.Message}");
        }

        (int from, int to) = AudioCut.Range(sl.Length, r.SampleRate, Text("start"), Text("end"), null, null);
        int available = to - from;
        if (available < 256)
        {
            throw new AmpleException("AMP5350", "The ambience block needs a recording of at least a few seconds.");
        }

        int fade = Math.Clamp((int)(Num("crossfade", 3) * r.SampleRate), 64, available / 3);
        int segment = Math.Clamp((int)(Num("segment", 12) * r.SampleRate), fade * 2, available);
        double variation = Math.Clamp(Num("variation", 40), 0, 90) / 100;
        ulong state = r.Seed == 0 ? 0x2545F4914F6CDD1DUL : r.Seed;
        double Next()
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            return (state >> 11) / (double)(1UL << 53);
        }

        long need = offset + total;
        double[] l = new double[need];
        double[] rr = stereo ? new double[need] : l;
        long written = 0;
        bool first = true;
        while (written < need)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = Math.Clamp((int)(segment * (1 + ((Next() * 2) - 1) * variation)), fade * 2, available);
            int begin = from + (int)(Next() * Math.Max(0, available - length));
            long at = first ? 0 : written - fade;   // the new segment starts under the tail of the last one
            for (int i = 0; i < length && at + i < need; i++)
            {
                double gain = 1;
                if (!first && i < fade)
                {
                    gain = Math.Sin(Math.PI / 2 * i / fade);
                }

                double tail = length - i <= fade ? Math.Cos(Math.PI / 2 * (fade - (length - i)) / fade) : 1;
                double g = gain * tail;
                int src = begin + i;
                l[at + i] += sl[src] * g;
                if (stereo)
                {
                    rr[at + i] += sr[src] * g;
                }
            }

            written = at + length;
            first = false;
        }

        short[] ol = new short[total], or = stereo ? new short[total] : ol;
        for (int i = 0; i < total; i++)
        {
            ol[i] = FixedAudio.Clip((long)Math.Round(l[offset + i]));
            if (stereo)
            {
                or[i] = FixedAudio.Clip((long)Math.Round(rr[offset + i]));
            }
        }

        byte[] audio = stereo ? StereoWav.Encode(new StereoBuffer(ol, or), r.SampleRate) : Wav.Encode(ol, r.SampleRate);
        return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("audio", audio), new RenderOutput("out", audio)]);
    }
}
