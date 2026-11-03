using System.Buffers.Binary;

namespace Ample.Audio.Engine;

/// <summary>Gain and channel control for audio and materialized render recipes.</summary>
internal sealed class VolumeRenderer : IBlockRenderer
{
    public string BlockType => "volume";
    public string Id => "builtin.volume";
    public string Version => "4";

    public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        byte[] input = request.Inputs.TryGetValue("in", out byte[]? connected) ? connected : Wav.Encode(new short[request.BarCount * request.SamplesPerBar], request.SampleRate);
        string Value(string name) => request.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"') ?? request.Template.Parameters.GetValueOrDefault(name) ?? string.Empty;
        (StereoBuffer channels, int rate) = ChannelAudio.Decode(input);
        bool bypass = Value("enabled") == "off";
        // the gain can be automated: auto(bar:dB, ...) gives one gain per sample
        int[]? curve = bypass || !Automation.Is(Value("gain")) ? null : Fades.Is(Value("gain")) ? Fades.Amount(Fades.Parse(Value("gain")), request.SampleRate, request.SamplesPerBar, request.FirstBar, channels.Length) : Automation.CurveAt(Automation.ParseSamples(Value("gain"), request.SamplesPerBar, request.SampleRate), request.FirstBar, request.SamplesPerBar, channels.Length, v => FixedAudio.GainFromDbWide(Math.Clamp(v, -60, FixedAudio.WideMaxDb)));
        int gain = bypass || curve is not null ? 32768 : FixedAudio.GainFromDbWide(AudioEffects.Read(Value("gain"), 0));
        // the master gain and the channel gain are multiplied once, not per sample
        int leftBase = bypass ? 32768 : FixedAudio.GainFromDbWide(AudioEffects.Read(Value("left_gain"), 0));
        int rightBase = bypass ? 32768 : FixedAudio.GainFromDbWide(AudioEffects.Read(Value("right_gain"), 0));
        long leftGain = (long)gain * leftBase;
        long rightGain = (long)gain * rightBase;
        bool mono = Value("mono") == "on", left = Value("left") != "off", right = Value("right") != "off";
        for (int i = 0; i < channels.Length && !bypass; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            int l = channels.Left[i], r = channels.Right[i];
            if (curve is not null)
            {
                leftGain = (long)curve[i] * leftBase;
                rightGain = (long)curve[i] * rightBase;
            }

            if (!bypass && mono) l = r = (l + r) / 2;
            channels.Left[i] = bypass || left ? FixedAudio.Clip((long)l * leftGain / (32768L * 32768)) : (short)0;
            channels.Right[i] = bypass || right ? FixedAudio.Clip((long)r * rightGain / (32768L * 32768)) : (short)0;
        }
        byte[] mixed = StereoWav.Encode(channels, rate);
        return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("mixed", mixed), new RenderOutput("left", Wav.Encode(channels.Left, rate)), new RenderOutput("right", Wav.Encode(channels.Right, rate)), new RenderOutput("out", mixed), new RenderOutput("audio", mixed)]);
    }
}

internal static class ChannelAudio
{
    internal static bool IsStereo(ReadOnlySpan<byte> audio) => audio.Length >= 44 && BinaryPrimitives.ReadInt16LittleEndian(audio[22..]) == 2;
    internal static (StereoBuffer Buffer, int Rate) Decode(byte[] audio)
    {
        if (IsStereo(audio)) return StereoWav.Decode(audio);
        (short[] samples, int rate) = Wav.Decode(audio);
        return (StereoBuffer.FromMono(samples), rate);
    }
}
