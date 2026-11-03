namespace Ample.Audio.Engine;

/// <summary>
/// The vocoder: a voice (the modulator) makes another sound (the carrier, a chord or a pad) speak. Both are split into the same frequency bands by a bank of band-pass filters; the loudness
/// of the voice in every band is followed (attack and release) and sets the loudness of the carrier in that band. Consonants that have no pitch (s, f, t) are made from noise that follows the
/// highest band of the voice (sibilance), so the words stay clear. The carrier bands can be moved by semitones (formant shift: a bigger or smaller robot). The level of the result is matched to
/// the level of the carrier. Doubles and a seeded noise: the same sounds give the same bytes on the same machine.
/// </summary>
public static class Vocoder
{
    /// <summary>The settings of one vocoder.</summary>
    /// <param name="Bands">Number of bands, 4 to 40.</param>
    /// <param name="LowHz">Lowest band centre in Hz.</param>
    /// <param name="HighHz">Highest band centre in Hz.</param>
    /// <param name="AttackMs">How fast a band follows the voice getting louder.</param>
    /// <param name="ReleaseMs">How fast a band lets go when the voice gets quieter.</param>
    /// <param name="Width">Width of a band relative to the space between bands (1 is touching, 2 overlaps a lot).</param>
    /// <param name="Sibilance">How much noise follows the highest band of the voice (0 to 1).</param>
    /// <param name="ShiftSemitones">Moves the carrier bands against those of the voice.</param>
    /// <param name="Mix">0 is the carrier as it is, 1 is the vocoded sound (0 to 1).</param>
    /// <param name="GainDb">The level relative to the carrier.</param>
    public sealed record Settings(int Bands = 20, double LowHz = 120, double HighHz = 7500, double AttackMs = 4, double ReleaseMs = 70, double Width = 1.4, double Sibilance = 0.35, double ShiftSemitones = 0, double Mix = 1, double GainDb = 0);

    /// <summary>Vocodes <paramref name="carrier"/> channels with the voice in <paramref name="modulator"/> (mono): returns channels as long as the longest of them.</summary>
    public static double[][] Process(double[] modulator, double[][] carrier, int rate, Settings s, ulong seed, CancellationToken cancellationToken = default)
    {
        int bands = Math.Clamp(s.Bands, 4, 40), length = Math.Max(modulator.Length, carrier.Max(c => c.Length));
        double low = Math.Clamp(s.LowHz, 40, rate * 0.2), high = Math.Clamp(s.HighHz, low * 1.5, rate * 0.45), spacing = Math.Log2(high / low) / Math.Max(1, bands - 1);
        double octaves = Math.Max(0.1, spacing * Math.Clamp(s.Width, 0.5, 3)), q = 1 / (Math.Pow(2, octaves / 2) - Math.Pow(2, -octaves / 2));
        double[] centre = new double[bands];
        for (int b = 0; b < bands; b++)
        {
            centre[b] = low * Math.Pow(2, spacing * b);
        }

        double attack = 1 - Math.Exp(-1 / (rate * Math.Max(0.1, s.AttackMs) / 1000)), release = 1 - Math.Exp(-1 / (rate * Math.Max(1, s.ReleaseMs) / 1000));
        double shift = Math.Pow(2, Math.Clamp(s.ShiftSemitones, -24, 24) / 12), mix = Math.Clamp(s.Mix, 0, 1), sibilance = Math.Clamp(s.Sibilance, 0, 1);
        double[][] result = new double[carrier.Length][];
        double[] envelope = new double[bands];
        for (int c = 0; c < carrier.Length; c++)
        {
            result[c] = new double[length];
        }

        // the filter banks: two cascaded band-passes per band (steeper), one set for the voice and one for every carrier channel (its bands moved by the shift)
        double[][] voiceBank = Enumerable.Range(0, bands).Select(b => Coefficients(centre[b], q, rate)).Select(k => new double[] { k.B0, k.A1, k.A2, 0, 0, 0, 0, 0, 0, 0, 0 }).ToArray();
        double[][][] carrierBank = [.. carrier.Select(_ => Enumerable.Range(0, bands).Select(b => Coefficients(centre[b] * shift, q, rate)).Select(k => new double[] { k.B0, k.A1, k.A2, 0, 0, 0, 0, 0, 0, 0, 0 }).ToArray())];
        double[][] voiceSecond = Enumerable.Range(0, bands).Select(_ => new double[4]).ToArray();
        double[][][] carrierSecond = [.. carrier.Select(_ => Enumerable.Range(0, bands).Select(_ => new double[4]).ToArray())];
        // the highest band of the voice also drives the noise of the consonants
        double sibilantEnvelope = 0, highPassState = 0, noiseState = 0;
        uint rng = (uint)(seed ^ (seed >> 32)) | 1;
        double Noise() { rng = (rng * 1103515245u) + 12345u; return (((rng >> 16) & 0x7FFF) / 16384.0) - 1; }
        double hpCoefficient = Math.Exp(-2 * Math.PI * 5000 / rate);
        double[] energyOut = new double[carrier.Length], energyCarrier = new double[carrier.Length];
        for (int i = 0; i < length; i++)
        {
            if ((i & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            double m = i < modulator.Length ? modulator[i] : 0;
            // the voice into bands: the loudness of every band
            for (int b = 0; b < bands; b++)
            {
                double y = Biquad(voiceBank[b], m);
                y = Biquad2(voiceBank[b], voiceSecond[b], y);
                double level = Math.Abs(y);
                envelope[b] += (level > envelope[b] ? attack : release) * (level - envelope[b]);
            }

            // the part of the voice above 5 kHz (s, f, t) follows its own loudness and makes noise
            highPassState = (hpCoefficient * highPassState) + ((1 - hpCoefficient) * m);
            double hf = m - highPassState;
            sibilantEnvelope += ((Math.Abs(hf) > sibilantEnvelope ? attack : release) * (Math.Abs(hf) - sibilantEnvelope));
            noiseState = (hpCoefficient * noiseState) + ((1 - hpCoefficient) * Noise());
            double hiss = (Noise() - noiseState) * sibilantEnvelope * 4 * sibilance;
            for (int c = 0; c < carrier.Length; c++)
            {
                double x = i < carrier[c].Length ? carrier[c][i] : 0, sum = 0;
                for (int b = 0; b < bands; b++)
                {
                    double y = Biquad(carrierBank[c][b], x);
                    y = Biquad2(carrierBank[c][b], carrierSecond[c][b], y);
                    sum += y * envelope[b];
                }

                result[c][i] = sum + hiss;
                energyOut[c] += result[c][i] * result[c][i];
                energyCarrier[c] += x * x;
            }
        }

        // the level of the result is the level of the carrier (times the gain), mixed with the carrier itself
        double gain = Math.Pow(10, Math.Clamp(s.GainDb, -36, 12) / 20);
        for (int c = 0; c < carrier.Length; c++)
        {
            double scale = energyOut[c] > 1e-12 ? Math.Sqrt(energyCarrier[c] / energyOut[c]) * gain : 0;
            for (int i = 0; i < length; i++)
            {
                double dry = i < carrier[c].Length ? carrier[c][i] : 0;
                result[c][i] = (dry * (1 - mix)) + (result[c][i] * scale * mix);
            }
        }

        return result;
    }

    private static (double B0, double A1, double A2) Coefficients(double centre, double q, double rate)
    {
        double w = 2 * Math.PI * Math.Min(centre, rate * 0.45) / rate, alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
        return (alpha / a0, -2 * Math.Cos(w) / a0, (1 - alpha) / a0);
    }

    // stage one of a band: the state is kept in the coefficient array (indexes 3 to 6: x1, x2, y1, y2)
    private static double Biquad(double[] k, double x)
    {
        double y = (k[0] * x) - (k[0] * k[4]) - (k[1] * k[5]) - (k[2] * k[6]);
        k[4] = k[3]; k[3] = x; k[6] = k[5]; k[5] = y;
        return y;
    }

    // stage two: the same coefficients, its own state
    private static double Biquad2(double[] k, double[] state, double x)
    {
        double y = (k[0] * x) - (k[0] * state[1]) - (k[1] * state[2]) - (k[2] * state[3]);
        state[1] = state[0]; state[0] = x; state[3] = state[2]; state[2] = y;
        return y;
    }
}

/// <summary>The vocoder block: the voice on its modulator input makes the sound on its carrier input speak.</summary>
internal sealed class VocoderRenderer : ExtraEffect
{
    public override string BlockType => "vocoder";

    public override string Version => "1";

    protected override IReadOnlyList<RenderOutput> Process(RenderRequest r, CancellationToken cancellationToken)
    {
        if (!r.Inputs.TryGetValue("modulator", out byte[]? voiceBytes) || !r.Inputs.TryGetValue("carrier", out byte[]? carrierBytes))
        {
            throw new Ample.Core.Abstractions.AmpleException("AMP5398", "A vocoder needs a voice on its modulator input and a sound on its carrier input.");
        }

        (StereoBuffer voice, int rate) = ChannelAudio.Decode(voiceBytes);
        (StereoBuffer carrier, int carrierRate) = ChannelAudio.Decode(carrierBytes);
        if (carrierRate != rate)
        {
            throw new Ample.Core.Abstractions.AmpleException("AMP5398", "The voice and the carrier of a vocoder must have the same sample rate.");
        }

        bool stereo = ChannelAudio.IsStereo(carrierBytes);
        double[] mono = new double[voice.Left.Length];
        bool voiceStereo = ChannelAudio.IsStereo(voiceBytes);
        for (int i = 0; i < mono.Length; i++)
        {
            mono[i] = voiceStereo ? (voice.Left[i] + (double)voice.Right[i]) / 2 : voice.Left[i];
        }

        double[][] channels = stereo ? [[.. carrier.Left.Select(v => (double)v)], [.. carrier.Right.Select(v => (double)v)]] : [[.. carrier.Left.Select(v => (double)v)]];
        int Number(string name, int fallback) => (int)Math.Round(Num(r, name, fallback));
        Vocoder.Settings settings = new(
            Bands: Number("bands", 20), LowHz: Num(r, "low", 120), HighHz: Num(r, "high", 7500), AttackMs: Num(r, "attack", 4), ReleaseMs: Num(r, "release", 70), Width: Num(r, "width", 140) / 100,
            Sibilance: Num(r, "sibilance", 35) / 100, ShiftSemitones: Num(r, "shift", 0), Mix: Num(r, "mix", 100) / 100, GainDb: Num(r, "gain", 0));
        double[][] made = Vocoder.Process(mono, channels, rate, settings, r.Seed, cancellationToken);
        short[][] clipped = [.. made.Select(c => c.Select(v => (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue)).ToArray())];
        return Out(Encode(clipped[0], stereo ? clipped[1] : clipped[0], rate, stereo));
    }
}
