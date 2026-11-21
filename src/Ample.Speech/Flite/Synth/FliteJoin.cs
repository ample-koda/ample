using Ample.Speech.Flite.Data;

namespace Ample.Speech.Flite.Synth;

// Port of Flite (CMU, commit 6c9f20dc), src/wavesynth/cst_units.c: concat_units, nearest_pm, add_residual; include/cst_sts.h: cst_lpcres. Translated to C# and modified: see FliteSynthesizer.cs.

/// <summary>The joined units (<c>cst_lpcres</c>): per pitch mark the frame of the voice that gives its LPC coefficients, and the residual bytes laid out one after the other.</summary>
internal sealed class FliteLpcRes
{
    /// <summary>The number of pitch marks that got a frame (<c>num_frames</c> after the join).</summary>
    public int NumFrames { get; init; }

    /// <summary>The end of every pitch mark in output samples (<c>times</c>).</summary>
    public required int[] Times { get; init; }

    /// <summary>The length of every pitch period in samples (<c>sizes</c>).</summary>
    public required int[] Sizes { get; init; }

    /// <summary>The number of the voice frame whose LPC coefficients the pitch mark uses (the C code holds a pointer into the table).</summary>
    public required int[] Frames { get; init; }

    /// <summary>The residual bytes of all pitch periods (µ-law); what a period does not fill stays 255, which is a zero.</summary>
    public required byte[] Residual { get; init; }

    /// <summary>The size of <see cref="Residual"/> (<c>num_samples</c>), the time of the last planned pitch mark.</summary>
    public int NumSamples { get; init; }

    /// <summary>For every unit, the end of the last pitch mark that was given to it or to an earlier unit, in output samples: the place where the unit ends in the output.</summary>
    public required int[] UnitEnds { get; init; }
}

/// <summary>The join of the units on the planned pitch marks (<c>concat_units</c>).</summary>
internal static class FliteJoin
{
    /// <summary>Gives every pitch mark the frame of the unit it falls in that is closest in time, and copies the residual of that frame into the period (cut or centred).</summary>
    /// <param name="voice">The voice data.</param>
    /// <param name="units">The units in order.</param>
    /// <param name="times">The planned pitch marks.</param>
    /// <param name="cancellation">Stops the join between two units.</param>
    internal static FliteLpcRes Concat(FliteVoiceData voice, IReadOnlyList<FliteUnit> units, int[] times, CancellationToken cancellation = default)
    {
        int count = times.Length;
        int[] unitEnds = new int[units.Count];
        if (count == 0)
        {
            // the C code reads times[-1] here; with no pitch mark there is nothing to say
            return new FliteLpcRes { NumFrames = 0, Times = [], Sizes = [], Frames = [], Residual = [], NumSamples = 0, UnitEnds = unitEnds };
        }

        int numSamples = times[count - 1];
        byte[] residual = new byte[numSamples];
        Array.Fill(residual, (byte)255);   // mulaw for 0 is 255
        int[] sizes = new int[count];
        int[] frames = new int[count];
        ReadOnlySpan<uint> offsets = voice.ResidualOffsets.Span;
        ReadOnlySpan<byte> voiceResidual = voice.Residual.Span;

        int targetStart = 0, rpos = 0, pmI = 0;
        for (int unitNumber = 0; unitNumber < units.Count; unitNumber++)
        {
            cancellation.ThrowIfCancellationRequested();
            FliteUnit u = units[unitNumber];
            int unitStart = u.UnitStart, unitEnd = u.UnitEnd;
            int unitSize = UnitSize(offsets, unitStart, unitEnd);
            int targetEnd = u.TargetEnd;

            float uIndex = 0;
            float m = (float)unitSize / (float)(targetEnd - targetStart);
            for (; pmI < count && times[pmI] <= targetEnd; pmI++)
            {
                int nearest = NearestPm(offsets, unitStart, unitEnd, uIndex);
                frames[pmI] = nearest;
                sizes[pmI] = times[pmI] - (pmI > 0 ? times[pmI - 1] : 0);
                int frameSize = FrameSize(offsets, nearest);
                ReadOnlySpan<byte> frameResidual = voiceResidual.Slice((int)offsets[nearest], frameSize);
                AddResidual(sizes[pmI], residual.AsSpan(rpos), frameSize, frameResidual);
                rpos += sizes[pmI];
                float step = (float)sizes[pmI] * m;
                uIndex = uIndex + step;
            }

            unitEnds[unitNumber] = pmI > 0 ? times[pmI - 1] : 0;
            targetStart = targetEnd;
        }

        return new FliteLpcRes { NumFrames = pmI, Times = times, Sizes = sizes, Frames = frames, Residual = residual, NumSamples = numSamples, UnitEnds = unitEnds };
    }

    /// <summary>The size in samples of the frames [start, end) (<c>get_unit_size</c>).</summary>
    private static int UnitSize(ReadOnlySpan<uint> offsets, int start, int end)
    {
        int size = 0;
        for (int i = start; i < end; i++) size += FrameSize(offsets, i);
        return size;
    }

    private static int FrameSize(ReadOnlySpan<uint> offsets, int frame) => (int)(offsets[frame + 1] - offsets[frame]);

    /// <summary>The frame of the unit whose start is closest to the index <paramref name="uIndex"/> (a place in samples within the unit) (<c>nearest_pm</c>).</summary>
    private static int NearestPm(ReadOnlySpan<uint> offsets, int start, int end, float uIndex)
    {
        int iSize = 0;
        for (int i = start; i < end; i++)
        {
            int nSize = iSize + FrameSize(offsets, i);
            if (Math.Abs((double)(uIndex - (float)iSize)) < Math.Abs((double)(uIndex - (float)nSize))) return i;
            iSize = nSize;
        }

        return end - 1;
    }

    /// <summary>Puts the residual of a frame in a pitch period: centred when the frame is shorter, the middle part when it is longer (<c>add_residual</c>).</summary>
    private static void AddResidual(int targSize, Span<byte> targResidual, int unitSize, ReadOnlySpan<byte> unitResidual)
    {
        if (unitSize < targSize) unitResidual[..unitSize].CopyTo(targResidual[((targSize - unitSize) / 2)..]);
        else unitResidual.Slice((unitSize - targSize) / 2, targSize).CopyTo(targResidual);
    }
}
