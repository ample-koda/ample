namespace Ample.Audio.Engine;

/// <summary>The <c>silence</c> block: blank audio, as long as the block it is in (<c>auto</c>) or as long as it is told (<c>10 s</c>), for the branch of a component that has no sound.</summary>
public static class SilenceBlock
{
    /// <summary>Gets the renderer of the block.</summary>
    public static IReadOnlyList<IBlockRenderer> All { get; } = [new SilenceRenderer()];

    private sealed class SilenceRenderer : IBlockRenderer
    {
        public string BlockType => "silence";

        public string Id => "builtin.silence";

        public string Version => "1";

        public string Determinism => "machine-exact";

        public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
        {
            string length = ControlBlocks.Text(r, "length").Trim();
            int samples = r.BarCount * r.SamplesPerBar;
            if (length.Length > 0 && !length.Equals("auto", StringComparison.OrdinalIgnoreCase)) samples = (int)Math.Min(int.MaxValue / 4, (long)ControlBlocks.Milliseconds(length, 0) * r.SampleRate / 1000);
            byte[] audio = Wav.Encode(new short[Math.Max(0, samples)], r.SampleRate);
            return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("out", audio)]);
        }
    }
}
