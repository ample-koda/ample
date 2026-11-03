namespace Ample.Audio.Engine;

/// <summary>Integer ADSR envelope in whole samples (ticks) with Q15 gains (32768 = 1.0); linear segments, no floating point. The release starts at the note end and runs from the level reached at that moment.</summary>
/// <param name="Attack">Samples to ramp from silence to full level.</param>
/// <param name="Decay">Samples to ease from full level to <paramref name="SustainQ15"/> (after any <paramref name="Hold"/>).</param>
/// <param name="SustainQ15">The held level once attack/hold/decay are done, 0 to <see cref="One"/>.</param>
/// <param name="Release">Samples to fade from wherever the envelope was to silence once the note ends.</param>
/// <param name="Hold">A plateau at full level (Q15 <see cref="One"/>) between the attack ramp and the decay, in whole samples; 0 (the default) goes straight from attack into decay, unchanged from before this existed (SFZ <c>ampeg_hold</c>).</param>
public readonly record struct IntegerEnvelope(int Attack, int Decay, int SustainQ15, int Release, int Hold = 0)
{
    /// <summary>Unity gain in Q15.</summary>
    public const int One = 32768;

    /// <summary>Gets the gain (Q15) of the held part at a sample position, ignoring the release.</summary>
    public int HeldGain(int pos)
    {
        if (pos < 0)
        {
            return 0;
        }

        int sustain = Math.Clamp(SustainQ15, 0, One);
        if (pos < Attack)
        {
            return (int)((long)One * pos / Attack);
        }

        int afterAttack = pos - Attack;
        if (afterAttack < Hold)
        {
            return One;
        }

        int p = afterAttack - Hold;
        if (p < Decay)
        {
            return One - (int)((long)(One - sustain) * p / Decay);
        }

        return sustain;
    }

    /// <summary>Gets the gain (Q15) at a position for a note that is released at <paramref name="noteLength"/> samples.</summary>
    public int Gain(int pos, int noteLength)
    {
        if (pos < noteLength)
        {
            return HeldGain(pos);
        }

        int start = HeldGain(noteLength);
        int r = pos - noteLength;
        if (Release <= 0 || r >= Release)
        {
            return 0;
        }

        return (int)((long)start * (Release - r) / Release);
    }

    /// <summary>Gets the total length in samples including the release tail.</summary>
    public int TotalLength(int noteLength) => noteLength + Math.Max(0, Release);
}
