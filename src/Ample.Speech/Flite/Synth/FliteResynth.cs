using Ample.Speech.Flite.Data;

namespace Ample.Speech.Flite.Synth;

// Port of Flite (CMU, commit 6c9f20dc), src/wavesynth/cst_sigpr.c: lpc_resynth_fixedpoint. Translated to C# and modified: see FliteSynthesizer.cs.

/// <summary>The resynthesis of the joined units with fixed-point arithmetic only (<c>lpc_resynth_fixedpoint</c>): a filter of 16 coefficients over the µ-law residual.</summary>
internal static class FliteResynth
{
    /// <summary>The samples of the waveform at the sample rate of the voice.</summary>
    /// <param name="voice">The voice data (LPC frames, coefficient range, channels).</param>
    /// <param name="lpcres">The joined units.</param>
    /// <param name="cancellation">Stops the resynthesis between two pitch periods.</param>
    internal static short[] Fixedpoint(FliteVoiceData voice, FliteLpcRes lpcres, CancellationToken cancellation = default)
    {
        int channels = voice.NumChannels;
        ReadOnlySpan<ushort> lpc = voice.Lpc.Span;
        int total = 0;
        for (int i = 0; i < lpcres.NumFrames; i++) total += lpcres.Sizes[i];
        short[] samples = new short[total];

        // outbuf is a circular buffer with the past relevant samples in it
        int[] outbuf = new int[1 + channels];
        int[] lpccoefs = new int[channels];
        int ilpcMin = (int)(voice.CoeffMin * 32768.0);
        // assume the range is never more than 16 in absolute value
        int ilpcRange = (int)(voice.CoeffRange * 2048.0);

        unchecked
        {
            int r = 0, o = channels;
            for (int i = 0; i < lpcres.NumFrames; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                int pmSizeSamps = lpcres.Sizes[i];
                int frameBase = lpcres.Frames[i] * channels;

                // unpack the LPC coefficients
                for (int k = 0; k < channels; k++) lpccoefs[k] = ((lpc[frameBase + k] / 2 * ilpcRange) / 2048 + ilpcMin) / 2;

                // resynthesise the signal
                for (int j = 0; j < pmSizeSamps; j++, r++)
                {
                    outbuf[o] = FliteUlaw.ToShort[lpcres.Residual[r]];
                    outbuf[o] *= 16384;
                    int cr = o == 0 ? channels : o - 1;
                    for (int ci = 0; ci < channels; ci++)
                    {
                        outbuf[o] += lpccoefs[ci] * outbuf[cr];
                        cr = cr == 0 ? channels : cr - 1;
                    }

                    outbuf[o] /= 16384;
                    samples[r] = (short)outbuf[o];
                    o = o == channels ? 0 : o + 1;
                }
            }
        }

        return samples;
    }
}
