using System.Globalization;
using System.Text;

namespace Ample.Audio.Engine;

/// <summary>
/// The <c>trim</c> block: finds where the sound of a recording really is (the stretches above a level, separated by silences of at least a given length) and cuts the silence before and after it,
/// so a recording that starts late or ends in a long tail begins at its sound and ends with it. When there are several stretches (two cars in one recording) it says so and uses
/// what <c>sections</c> asks for: <c>warn</c> (from the first to the end of the last, with a warning), <c>all</c> (the same, no warning), <c>first</c> or <c>last</c>.
/// </summary>
public static class TrimBlock
{
    /// <summary>Gets the renderer of the block.</summary>
    public static IReadOnlyList<IBlockRenderer> All { get; } = [new TrimRenderer()];

    private sealed class TrimRenderer : IBlockRenderer
    {
        public string BlockType => "trim";

        public string Id => "builtin.trim";

        public string Version => "1";

        public string Determinism => "machine-exact";

        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
        {
            byte[] input = r.Inputs.TryGetValue("in", out byte[]? given) ? given : Wav.Encode(new short[r.BarCount * r.SamplesPerBar], r.SampleRate);
            if (ControlBlocks.Text(r, "enabled") == "off") return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", input), new RenderOutput("audio", input), new RenderOutput("report", Encoding.UTF8.GetBytes("trim is off"))]);
            bool stereo = ChannelAudio.IsStereo(input);
            short[] left, right;
            int rate;
            if (stereo)
            {
                (StereoBuffer both, rate) = ChannelAudio.Decode(input);
                (left, right) = (both.Left, both.Right);
            }
            else
            {
                (short[] samples, int monoRate) = Wav.Decode(input);
                (left, right, rate) = (samples, samples, monoRate);
            }

            double thresholdDb = ControlBlocks.Fixed(ControlBlocks.Text(r, "threshold"), -45000) / 1000.0;
            int amplitude = (int)Math.Clamp(32768 * Math.Pow(10, thresholdDb / 20), 1, 32767);
            int gapMs = Math.Max(1, ControlBlocks.Milliseconds(ControlBlocks.Text(r, "gap"), 400));
            int paddingMs = Math.Max(0, ControlBlocks.Milliseconds(ControlBlocks.Text(r, "padding"), 50));
            string mode = ControlBlocks.Text(r, "sections");

            // one flag per millisecond: is there sound above the level in it
            int perMs = Math.Max(1, rate / 1000);
            int count = (left.Length + perMs - 1) / perMs;
            bool[] active = new bool[count];
            for (int i = 0; i < left.Length; i++)
            {
                if (!active[i / perMs] && (Math.Abs((int)left[i]) > amplitude || Math.Abs((int)right[i]) > amplitude)) active[i / perMs] = true;
            }

            // the stretches of sound: a silence of gap milliseconds or more ends one
            List<(int Start, int End)> sections = [];
            int start = -1, lastActive = -1;
            for (int k = 0; k < count; k++)
            {
                if (active[k])
                {
                    if (start < 0) start = k;
                    lastActive = k;
                }
                else if (start >= 0 && k - lastActive >= gapMs)
                {
                    sections.Add((start, lastActive));
                    start = -1;
                }
            }

            if (start >= 0) sections.Add((start, lastActive));
            string Seconds(int ms) => (ms / 1000.0).ToString("0.00", CultureInfo.InvariantCulture);
            if (sections.Count == 0)
            {
                string none = $"trim: no sound above {thresholdDb.ToString("0.#", CultureInfo.InvariantCulture)} dB in this recording, nothing was cut.";
                return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", input), new RenderOutput("audio", input), new RenderOutput("report", Encoding.UTF8.GetBytes(none))]);
            }

            (int first, int last) = mode switch { "first" => sections[0], "last" => sections[^1], _ => (sections[0].Start, sections[^1].End) };
            int from = Math.Max(0, (first * perMs) - (paddingMs * perMs));
            int to = Math.Min(left.Length, ((last + 1) * perMs) + (paddingMs * perMs));
            short[] cutLeft = left[from..to], cutRight = right[from..to];
            Fade(cutLeft, stereo ? cutRight : null, rate, FadeLength(ControlBlocks.Text(r, "fadein"), cutLeft.Length, rate), FadeLength(ControlBlocks.Text(r, "fadeout"), cutLeft.Length, rate));

            StringBuilder report = new();
            report.Append(string.Create(CultureInfo.InvariantCulture, $"trim: sound from {Seconds(first)} s to {Seconds(last + 1)} s of {Seconds(count)} s"));
            report.Append(sections.Count == 1 ? " (1 section)." : $" ({sections.Count} sections: {string.Join(", ", sections.Select(s => Seconds(s.Start) + "-" + Seconds(s.End + 1)))}).");
            if (sections.Count > 1 && mode == "warn")
            {
                report.Append(" WARNING: this recording holds several stretches of sound; the cut runs from the start of the first to the end of the last. Set sections to all (that is what you want), first or last to choose, and this warning goes away.");
            }

            byte[] output = stereo ? StereoWav.Encode(new StereoBuffer(cutLeft, cutRight), rate) : Wav.Encode(cutLeft, rate);
            return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", output), new RenderOutput("audio", output), new RenderOutput("report", Encoding.UTF8.GetBytes(report.ToString()))]);
        }

        /// <summary>A fade as a number of samples: milliseconds (250 ms), or a share of what is kept (33%, so a short car and a long car fade alike).</summary>
        private static int FadeLength(string text, int length, int rate)
        {
            string t = text.Trim();
            if (t.EndsWith('%') && double.TryParse(t[..^1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double percent)) return (int)Math.Clamp(length * Math.Clamp(percent, 0, 100) / 100, 0, length);
            return (int)Math.Min(length, (long)ControlBlocks.Milliseconds(t, 0) * rate / 1000);
        }

        private static void Fade(short[] left, short[]? right, int rate, int inLength, int outLength)
        {
            inLength = Math.Min(left.Length, inLength);
            outLength = Math.Min(left.Length, outLength);
            for (int i = 0; i < inLength; i++)
            {
                long gain = (long)i * 65536 / inLength;
                left[i] = (short)(left[i] * gain >> 16);
                if (right is not null) right[i] = (short)(right[i] * gain >> 16);
            }

            for (int i = 0; i < outLength; i++)
            {
                int at = left.Length - 1 - i;
                long gain = (long)i * 65536 / outLength;
                left[at] = (short)(left[at] * gain >> 16);
                if (right is not null) right[at] = (short)(right[at] * gain >> 16);
            }
        }
    }
}
