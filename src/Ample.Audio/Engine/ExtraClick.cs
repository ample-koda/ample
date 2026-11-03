namespace Ample.Audio.Engine;

/// <summary>
/// The <c>click</c> block: a metronome. One short click per beat over the length of the block (a louder, higher one on the first beat of every bar when <c>accent</c> is on),
/// two or four clicks per beat with <c>per_beat</c>. The clicks are integer square bursts with a linear fade, so the same song gives the same samples on every machine.
/// </summary>
public static class ClickBlock
{
    /// <summary>Gets the renderer of the block.</summary>
    public static IReadOnlyList<IBlockRenderer> All { get; } = [new ClickRenderer()];

    private sealed class ClickRenderer : IBlockRenderer
    {
        public string BlockType => "click";

        public string Id => "builtin.click";

        public string Version => "1";

        public string Determinism => "machine-exact";

        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
        {
            bool accent = !ControlBlocks.Text(r, "accent").Trim().Equals("off", StringComparison.OrdinalIgnoreCase);
            int perBeat = int.TryParse(ControlBlocks.Text(r, "per_beat").Trim(), out int p) && p is 1 or 2 or 4 ? p : 1;
            int total = Math.Max(0, r.BarCount * r.SamplesPerBar);
            short[] samples = new short[total];
            double perBar = r.BeatsPerBar * perBeat;   // clicks in a bar: 4 in 4/4, 3 in 3/4, 3.5 in 7/8 (one every beat, the last one cut at the bar line)
            int step = Math.Max(1, (int)(r.SamplesPerBar / perBar));
            int inBar = (int)Math.Ceiling(perBar);
            for (int bar = 0; bar < r.BarCount; bar++)
            {
                for (int j = 0; j < inBar; j++)
                {
                    long at = ((long)bar * r.SamplesPerBar) + (long)(j * r.SamplesPerBar / perBar);
                    Burst(samples, at, r.SampleRate, accent && j == 0, step);
                }
            }

            return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", Wav.Encode(samples, r.SampleRate))]);
        }

        private static void Burst(short[] samples, long start, int sampleRate, bool accent, int step)
        {
            int length = Math.Min(Math.Max(1, sampleRate * 25 / 1000), step);
            int period = Math.Max(2, sampleRate / (accent ? 1500 : 1000));
            int peak = accent ? 16000 : 9000;
            for (int k = 0; k < length && start + k < samples.Length; k++)
            {
                int amplitude = (int)((long)peak * (length - k) / length);
                samples[start + k] = (short)((k / (period / 2)) % 2 == 0 ? amplitude : -amplitude);
            }
        }
    }
}
