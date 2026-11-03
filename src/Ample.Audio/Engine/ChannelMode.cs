using Ample.Language;

namespace Ample.Audio.Engine;

/// <summary>
/// The channel mode of a block (A01): <c>auto</c> leaves the audio as the block made it (stereo when an input or the source is stereo, mono otherwise),
/// <c>mono</c> sums a stereo output to mono, <c>stereo</c> duplicates a mono output into two equal channels. A <c>master</c> block can set the default for the song.
/// </summary>
internal static class ChannelMode
{
    internal const string Auto = "auto", Mono = "mono", Stereo = "stereo";

    /// <summary>Gets the effective mode of a block: its own <c>channels</c> parameter, else the one of the master block, else <c>auto</c>.</summary>
    internal static string Of(BlockNode block, AmpleDocument doc)
    {
        static string? Read(BlockNode b) => b.Parameters.FirstOrDefault(p => p.Name == "channels")?.Value.Trim().Trim('"');
        string? own = Read(block);
        if (own is Mono or Stereo or Auto) return own;
        string? song = doc.Blocks.FirstOrDefault(b => b.Type == "master") is { } master ? Read(master) : null;
        return song is Mono or Stereo ? song : Auto;
    }

    /// <summary>Applies the mode to every WAV output of a render (the separate left and right outputs of a splitter stay mono).</summary>
    internal static IReadOnlyList<RenderOutput> Apply(IReadOnlyList<RenderOutput> outputs, string mode)
    {
        if (mode == Auto) return outputs;
        List<RenderOutput> result = new(outputs.Count);
        foreach (RenderOutput output in outputs)
        {
            bool wav = output.Data.Length >= 44 && output.Data[0] == 'R' && output.Data[1] == 'I' && output.Data[2] == 'F' && output.Data[3] == 'F';
            if (!wav || output.Role is "left" or "right") { result.Add(output); continue; }
            bool isStereo = ChannelAudio.IsStereo(output.Data);
            if (mode == Mono && isStereo)
            {
                (StereoBuffer both, int rate) = StereoWav.Decode(output.Data);
                short[] sum = new short[both.Left.Length];
                for (int i = 0; i < sum.Length; i++) sum[i] = (short)((both.Left[i] + both.Right[i]) / 2);
                result.Add(output with { Data = Wav.Encode(sum, rate) });
            }
            else if (mode == Stereo && !isStereo)
            {
                (short[] samples, int rate) = Wav.Decode(output.Data);
                result.Add(output with { Data = StereoWav.Encode(new StereoBuffer(samples, [.. samples]), rate) });
            }
            else
            {
                result.Add(output);
            }
        }

        return result;
    }
}
