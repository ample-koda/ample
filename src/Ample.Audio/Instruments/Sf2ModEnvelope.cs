namespace Ample.Audio.Instruments;

/// <summary>
/// The modulation envelope of an SF2 note (generators 25 to 30): delay, a linear attack to the peak, hold, a linear decay to the sustain level, and a linear release
/// from wherever it is when the note is let go. Levels are Q15 (32768 is the peak); integer only.
/// </summary>
internal readonly struct Sf2ModEnvelope
{
    private readonly int delay, attack, hold, decay, release, sustain;

    /// <param name="settings">Delay, attack, hold, decay and release in timecents; sustain in 0.1 % below the peak.</param>
    /// <param name="rate">The output sample rate.</param>
    public Sf2ModEnvelope((int Delay, int Attack, int Hold, int Decay, int Sustain, int Release) settings, int rate)
    {
        delay = Sf2Program.Frames(settings.Delay, (uint)rate);
        attack = Math.Max(1, Sf2Program.Frames(settings.Attack, (uint)rate));
        hold = Sf2Program.Frames(settings.Hold, (uint)rate);
        decay = Math.Max(1, Sf2Program.Frames(settings.Decay, (uint)rate));
        release = Math.Max(1, Sf2Program.Frames(settings.Release, (uint)rate));
        sustain = 32768 - (Math.Clamp(settings.Sustain, 0, 1000) * 32768 / 1000);
    }

    private Sf2ModEnvelope(int delay, int attack, int hold, int decay, int release, int sustain)
    {
        this.delay = delay; this.attack = attack; this.hold = hold; this.decay = decay; this.release = release; this.sustain = sustain;
    }

    /// <summary>The envelope after its settings changed while the note sounds: begun phases keep their length, later ones and the sustain level take the new settings; not after the note is let go.</summary>
    public Sf2ModEnvelope Following(Sf2ModEnvelope next, int frame, int noteOff)
    {
        if (frame >= noteOff) return this;
        int newAttack = attack, newHold = hold, newDecay = decay;
        if (frame < delay) { newAttack = next.attack; newHold = next.hold; newDecay = next.decay; }
        else if (frame < delay + attack) { newHold = next.hold; newDecay = next.decay; }
        else if (frame < delay + attack + hold) newDecay = next.decay;
        return new Sf2ModEnvelope(delay, newAttack, newHold, newDecay, next.release, next.sustain);
    }

    /// <summary>The level at a frame of the note; the note is let go at <paramref name="noteOff"/> (frames from its start).</summary>
    public int Level(int frame, int noteOff)
    {
        if (frame < noteOff) return Held(frame);
        int from = Held(noteOff);
        long fall = (long)(frame - noteOff) * 32768 / release;
        return (int)Math.Max(0, from - fall);
    }

    private int Held(int frame)
    {
        if (frame < delay) return 0;
        frame -= delay;
        if (frame < attack) return (int)((long)frame * 32768 / attack);
        frame -= attack;
        if (frame < hold) return 32768;
        frame -= hold;
        long down = (long)frame * 32768 / decay;   // the decay time is the time from the peak all the way down; it stops at the sustain level
        return (int)Math.Max(sustain, 32768 - down);
    }
}
