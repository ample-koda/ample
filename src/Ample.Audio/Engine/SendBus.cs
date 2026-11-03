namespace Ample.Audio.Engine;

/// <summary>
/// The send block: up to four sounds go in. <c>through</c> is their plain sum (what carries on to the mix) and <c>send</c> is the sum with a send level per input (what goes to a shared
/// effect such as one reverb). Put the effect after <c>send</c> and a mixer after <c>through</c> and the effect, and four tracks share one reverb with their own amount each.
/// </summary>
internal sealed class SendRenderer : IBlockRenderer
{
    public string BlockType => "send";

    public string Id => "builtin.send";

    public string Version => "1";

    public ValueTask<IReadOnlyList<RenderOutput>> RenderAsync(RenderRequest r, CancellationToken cancellationToken)
    {
        string Text(string name) => r.Block.Parameters.FirstOrDefault(p => p.Name == name)?.Value.Trim().Trim('"') is { Length: > 0 } v ? v : r.Template.Parameters.GetValueOrDefault(name) ?? string.Empty;
        string[] levels = Text("levels").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        int length = r.BarCount * r.SamplesPerBar;
        int[] sendLeft = new int[length], sendRight = new int[length], throughLeft = new int[length], throughRight = new int[length];
        bool stereo = false;
        int rate = r.SampleRate;
        for (int i = 1; i <= 4; i++)
        {
            if (!r.Inputs.TryGetValue("in" + i, out byte[]? input))
            {
                continue;
            }

            stereo |= ChannelAudio.IsStereo(input);
            (StereoBuffer buffer, rate) = ChannelAudio.Decode(input);
            int db = levels.Length >= i && int.TryParse(levels[i - 1].Replace("dB", string.Empty, StringComparison.Ordinal), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int parsed) ? Math.Clamp(parsed, -60, 12) : -12;
            int send = db <= -60 ? 0 : FixedAudio.GainFromDb(db);
            for (int n = 0; n < length && n < buffer.Left.Length; n++)
            {
                throughLeft[n] += buffer.Left[n];
                throughRight[n] += buffer.Right[n];
                sendLeft[n] += (int)((long)buffer.Left[n] * send >> 15);
                sendRight[n] += (int)((long)buffer.Right[n] * send >> 15);
            }
        }

        byte[] Encode(int[] left, int[] right) => stereo
            ? StereoWav.Encode(new StereoBuffer([.. left.Select(v => FixedAudio.Clip(v))], [.. right.Select(v => FixedAudio.Clip(v))]), rate)
            : Wav.Encode([.. left.Select(v => FixedAudio.Clip(v))], rate);
        return ValueTask.FromResult<IReadOnlyList<RenderOutput>>([new RenderOutput("through", Encode(throughLeft, throughRight)), new RenderOutput("send", Encode(sendLeft, sendRight))]);
    }
}
