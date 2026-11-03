namespace Ample.Audio.Instruments;

/// <summary>
/// The volume envelope of an SF2 note (generators 33 to 38) in the shape SF2 and FluidSynth give it: silence for the delay, a rise linear in amplitude for the attack,
/// full level for the hold, then a fall that is linear in decibels (the decay time is the time a fall of 96 dB would take) down to the sustain level; when the note is
/// let go it falls from wherever it is at the same rate for the release. A piano's tail dies away the way a real one does instead of in a straight line. Integer only.
/// </summary>
internal readonly struct Sf2VolEnvelope
{
    private const int FullScaleCb = 960;   // 96 dB: the fall a decay or release time stands for
    private readonly int delay, attack, hold, decay, release, sustainCb;

    public Sf2VolEnvelope(int delayTc, int attackTc, int holdTc, int decayTc, int sustainCb, int release, int rate)
    {
        delay = Sf2Program.Frames(delayTc, (uint)rate);
        attack = Math.Max(1, Sf2Program.Frames(attackTc, (uint)rate));
        hold = Sf2Program.Frames(holdTc, (uint)rate);
        decay = Math.Max(1, Sf2Program.Frames(decayTc, (uint)rate));
        this.release = Math.Max(1, release);
        this.sustainCb = Math.Clamp(sustainCb, 0, 1440);
    }

    private Sf2VolEnvelope(int delay, int attack, int hold, int decay, int release, int sustainCb, bool frames)
    {
        _ = frames;
        this.delay = delay; this.attack = attack; this.hold = hold; this.decay = decay; this.release = release; this.sustainCb = sustainCb;
    }

    /// <summary>
    /// The envelope after its settings changed while the note sounds (a controller modulator): the phases that have begun keep their length, the ones still to come take
    /// the new one, and the sustain level follows at once; once the note is let go nothing changes (its release has begun).
    /// </summary>
    public Sf2VolEnvelope Following(Sf2VolEnvelope next, int frame, int noteOff)
    {
        if (frame >= noteOff) return this;
        int newAttack = attack, newHold = hold, newDecay = decay;
        if (frame < delay) { newAttack = next.attack; newHold = next.hold; newDecay = next.decay; }
        else if (frame < delay + attack) { newHold = next.hold; newDecay = next.decay; }
        else if (frame < delay + attack + hold) newDecay = next.decay;
        return new Sf2VolEnvelope(delay, newAttack, newHold, newDecay, release, next.sustainCb, true);
    }

    /// <summary>The gain (Q15) at a frame of a note that is let go at <paramref name="noteOff"/> frames.</summary>
    public int Gain(int frame, int noteOff)
    {
        if (frame < noteOff) return Held(frame);
        int from = Held(noteOff);
        long fallCb = (long)(frame - noteOff) * FullScaleCb / release;
        return fallCb >= FullScaleCb ? 0 : (int)((long)from * GainOfCb(fallCb) >> 15);
    }

    private int Held(int frame)
    {
        if (frame < delay) return 0;
        frame -= delay;
        if (frame < attack) return (int)((long)frame * 32768 / attack);
        frame -= attack;
        if (frame < hold) return 32768;
        frame -= hold;
        long fallCb = Math.Min(sustainCb, (long)frame * FullScaleCb / decay);
        return GainOfCb(fallCb);
    }

    /// <summary>The gain (Q15) of an attenuation in centibels.</summary>
    public static int GainOfCb(long centibels) => centibels >= 1440 ? 0 : (int)(InstrumentMath.CentsToRatioQ16(-(centibels * 199315 / 10000)) >> 1);
}
