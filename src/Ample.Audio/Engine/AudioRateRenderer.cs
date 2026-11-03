namespace Ample.Audio.Engine;

/// <summary>Normalizes effect inputs to the song's sample rate before DSP and timeline automation.</summary>
internal sealed class AudioRateRenderer(IBlockRenderer renderer) : IBlockRenderer
{
    public string BlockType => renderer.BlockType;
    public string Id => renderer.Id;
    public string Version => renderer.Version + "/audio-rate-1";
    public string Determinism => renderer.Determinism;
    public string ToolprintJson => renderer.ToolprintJson;

    public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        Dictionary<string, byte[]> inputs = new(request.Inputs, StringComparer.Ordinal);
        foreach (var port in request.Template.Inputs.Where(port => port.Type == "audio"))
        {
            if (!inputs.TryGetValue(port.Name, out byte[]? audio)) continue;
            (StereoBuffer channels, int rate) = ChannelAudio.Decode(audio);
            if (rate == request.SampleRate) continue;
            short[] left = Resample(channels.Left, rate, request.SampleRate, cancellationToken);
            inputs[port.Name] = ChannelAudio.IsStereo(audio)
                ? StereoWav.Encode(new StereoBuffer(left, Resample(channels.Right, rate, request.SampleRate, cancellationToken)), request.SampleRate)
                : Wav.Encode(left, request.SampleRate);
        }
        return renderer.RenderAsync(request with { Inputs = inputs }, cancellationToken);
    }

    private static short[] Resample(short[] samples, int sourceRate, int targetRate, CancellationToken cancellationToken)
    {
        short[] result = new short[checked((int)(((long)samples.Length * targetRate + sourceRate / 2) / sourceRate))];
        for (int i = 0; i < result.Length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            long position = (long)i * sourceRate;
            int index = (int)(position / targetRate);
            long fraction = position % targetRate;
            int first = samples[Math.Min(index, samples.Length - 1)];
            int second = samples[Math.Min(index + 1, samples.Length - 1)];
            result[i] = (short)((first * (targetRate - fraction) + second * fraction) / targetRate);
        }
        return result;
    }
}
