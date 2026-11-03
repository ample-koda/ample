namespace Ample.Audio.Engine;

/// <summary>Plays a sustain loop from integer sample positions. Playback runs from the start to the loop end, then wraps to the loop start. In the last <c>crossfade</c> samples before the loop end the signal fades linearly into the material just before the loop start, so the wrap point is continuous.</summary>
public sealed class LoopPlayer
{
    private readonly short[] _source;

    /// <summary>Creates a loop over [loopStart, loopEnd); the crossfade is clamped to what the loop and the lead-in allow (0 means a hard wrap).</summary>
    public LoopPlayer(short[] source, int loopStart, int loopEnd, int crossfade)
    {
        ArgumentNullException.ThrowIfNull(source);
        LoopEnd = Math.Clamp(loopEnd, 1, source.Length);
        LoopStart = Math.Clamp(loopStart, 0, LoopEnd - 1);
        int length = LoopEnd - LoopStart;
        Crossfade = Math.Clamp(crossfade, 0, Math.Min(LoopStart, length / 2));
        _source = source;
    }

    /// <summary>Gets the loop start.</summary>
    public int LoopStart { get; }

    /// <summary>Gets the loop end (exclusive).</summary>
    public int LoopEnd { get; }

    /// <summary>Gets the effective crossfade length.</summary>
    public int Crossfade { get; }

    /// <summary>Gets the sample at a playback position (any non-negative position; the loop repeats forever).</summary>
    public short At(long position)
    {
        if (position < 0)
        {
            return 0;
        }

        int length = LoopEnd - LoopStart;
        long mapped = position < LoopEnd ? position : LoopStart + ((position - LoopStart) % length);
        int m = (int)mapped;
        int k = m - (LoopEnd - Crossfade);
        if (Crossfade > 0 && k >= 0)
        {
            int lead = _source[LoopStart - Crossfade + k];
            long mixed = ((long)_source[m] * (Crossfade - k)) + ((long)lead * k);
            return (short)(mixed / Crossfade);
        }

        return _source[m];
    }

    /// <summary>Fills a buffer with playback from a start position.</summary>
    public void Fill(Span<short> destination, long start)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = At(start + i);
        }
    }
}
