using System.Globalization;
using System.Text;

namespace Ample.Audio.Engine;

/// <summary>Passes the sound on unchanged and reports its peak, RMS, integrated loudness, crest factor and clipped samples as text (the <c>report</c> output).</summary>
internal sealed class MeterRenderer : ExtraEffect
{
    public override string BlockType => "meter";

    public override string Version => "2";

    public override string Determinism => "exact";

    protected override IReadOnlyList<RenderOutput> Process(RenderRequest r, CancellationToken cancellationToken)
    {
        byte[] audio = r.Inputs.TryGetValue("in", out byte[]? input) ? input : Wav.Encode(new short[r.BarCount * r.SamplesPerBar], r.SampleRate);
        (short[] left, short[] right, int rate, bool stereo) = Input(r);
        short[] all = stereo ? [.. left, .. right] : left;
        double peak = AudioMeter.PeakDbfs(all);
        double rms = AudioMeter.RmsDbfs(all);
        double? lufs = Loudness.IntegratedLufs(stereo ? [left, right] : [left], rate);
        int clipped = all.Count(v => v is short.MaxValue or short.MinValue);
        string crest = double.IsNegativeInfinity(peak) || double.IsNegativeInfinity(rms) ? "n/a" : (peak - rms).ToString("F1", CultureInfo.InvariantCulture) + " dB";
        string text = $"peak {AudioMeter.Format(peak)} | rms {AudioMeter.Format(rms)} | loudness {(lufs is null ? "silence" : lufs.Value.ToString("F1", CultureInfo.InvariantCulture) + " LUFS")} | crest {crest} | clipped {clipped.ToString(CultureInfo.InvariantCulture)} | {(all.Length / (double)rate / (stereo ? 2 : 1)).ToString("F1", CultureInfo.InvariantCulture)} s{Channels(left, right, stereo)}";
        return [new RenderOutput("out", audio), new RenderOutput("audio", audio), new RenderOutput("report", Encoding.UTF8.GetBytes(text))];
    }

    /// <summary>The channel part of the report: " | mono", or " | stereo L -4.0 R -4.3 dBFS | correlation 0.62" (1 is mono-compatible, below 0 the channels fight each other).</summary>
    private static string Channels(short[] left, short[] right, bool stereo)
    {
        if (!stereo) return " | mono";
        double sumL = 0, sumR = 0, cross = 0;
        for (int i = 0; i < left.Length && i < right.Length; i++)
        {
            sumL += (double)left[i] * left[i];
            sumR += (double)right[i] * right[i];
            cross += (double)left[i] * right[i];
        }

        double correlation = sumL > 0 && sumR > 0 ? cross / Math.Sqrt(sumL * sumR) : 1;
        return $" | stereo L {AudioMeter.Format(AudioMeter.PeakDbfs(left))} R {AudioMeter.Format(AudioMeter.PeakDbfs(right))} | correlation {correlation.ToString("F2", CultureInfo.InvariantCulture)}";
    }
}
