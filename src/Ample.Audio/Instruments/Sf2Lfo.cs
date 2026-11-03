namespace Ample.Audio.Instruments;

/// <summary>
/// An SF2 low-frequency oscillator (the modulation LFO, generators 21 and 22, or the vibrato LFO, 23 and 24): after its delay a triangle that starts at 0 and rises
/// first, -1 to 1 (Q15), at a frequency given in absolute cents (8.176 Hz at 0). Integer only.
/// </summary>
internal readonly struct Sf2Lfo
{
    private readonly int delay;
    private readonly long increment;   // phase per frame, Q32
    private readonly int anchorFrame;   // after a change of frequency: the frame from which the new increment counts, and the phase there
    private readonly long anchorPhase;

    public Sf2Lfo(int delayTc, int frequencyCents, int rate)
    {
        delay = Sf2Program.Frames(delayTc, (uint)rate);
        long hzQ16 = 440 * InstrumentMath.CentsToRatioQ16(Math.Clamp(frequencyCents, -16000, 4500) - 6900);
        increment = Math.Max(1, (hzQ16 << 16) / rate);
    }

    private Sf2Lfo(int delay, long increment, int anchorFrame, long anchorPhase)
    {
        this.delay = delay; this.increment = increment; this.anchorFrame = anchorFrame; this.anchorPhase = anchorPhase;
    }

    /// <summary>The LFO after its delay or frequency changed while the note sounds: still waiting, it takes both; running, it keeps its phase and goes on at the new frequency.</summary>
    public Sf2Lfo Retuned(Sf2Lfo next, int frame) => frame < delay ? next : new Sf2Lfo(delay, next.increment, frame, Phase(frame));

    private long Phase(int frame) => (anchorPhase + ((long)(frame - Math.Max(delay, anchorFrame)) * increment)) & 0xFFFFFFFFL;

    /// <summary>The value (Q15, -32768..32768) at a frame of the note.</summary>
    public int At(int frame)
    {
        if (frame < delay) return 0;
        long phase = Phase(frame);   // 0 .. 2^32
        long quarter = 1L << 30;
        return (int)(phase < quarter ? phase * 32768 / quarter
            : phase < 3 * quarter ? 65536 - (phase * 32768 / quarter)
            : (phase * 32768 / quarter) - 131072);
    }
}
