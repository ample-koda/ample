using Ample.Speech.Flite.Data;

namespace Ample.Speech.Flite.Synth;

// Port of Flite (CMU, commit 6c9f20dc), src/wavesynth/cst_diphone.c, cst_units.c and cst_sigpr.c (the diphone synthesis of the voice cmu_us_kal16: join_type modified_lpc,
// resynth_type fixed, residual type ulaw). Translated to C# and modified: the data comes from FliteVoiceData instead of C structures; the units, the plan of the pitch marks, the
// join and the resynthesis are separate steps so that every stage can be compared with the C program; the loop of the pitch-mark plan has a limit (FlitePitchMarks.MaxMarks);
// cancellation is checked between units and pitch periods; with no pitch mark the result is empty (the C code reads times[-1]).
// Not ported: the other join and resynthesis types, the other residual codecs (g721, vuv) and the streaming callback, which this voice does not use.

/// <summary>What the synthesis half of the voice made of the segments and targets.</summary>
internal sealed class FliteSynthResult
{
    /// <summary>The waveform, 16-bit samples at <see cref="SampleRate"/>.</summary>
    public required short[] Samples { get; init; }

    /// <summary>The sample rate of the voice (16000).</summary>
    public required int SampleRate { get; init; }

    /// <summary>The Unit relation: two units for every pair of segments.</summary>
    public required IReadOnlyList<FliteUnit> Units { get; init; }

    /// <summary>The planned pitch marks that got a frame: their end in output samples.</summary>
    public required int[] PitchMarkTimes { get; init; }

    /// <summary>The frame of the voice that every pitch mark uses.</summary>
    public required int[] PitchMarkFrames { get; init; }

    /// <summary>The diphones that are not in the voice, in order of use; Flite writes them to its error output and uses entry 0.</summary>
    public required IReadOnlyList<string> MissingDiphones { get; init; }

    /// <summary>For every segment, the output sample index of its last pitch mark: the end of the segment in the waveform. The last segment ends with the waveform.</summary>
    public required int[] SegmentEnds { get; init; }
}

/// <summary>The synthesis of the voice from segments and pitch targets to samples (<c>diphone_synth</c> after the text analysis and the prosody).</summary>
internal static class FliteSynthesizer
{
    /// <summary>Selects the diphones, plans the pitch marks, joins the units and resynthesises the waveform.</summary>
    /// <param name="voice">The voice data.</param>
    /// <param name="segments">The Segment relation, with the end times of the duration model.</param>
    /// <param name="targets">The Target relation of the pitch model.</param>
    /// <param name="cancellation">Stops the synthesis between units and pitch periods.</param>
    internal static FliteSynthResult Synthesize(FliteVoiceData voice, IReadOnlyList<FliteSegmentInput> segments, IReadOnlyList<FliteTarget> targets, CancellationToken cancellation = default)
    {
        List<string> missing = [];
        List<FliteUnit> units = FliteDiphoneUnits.Select(voice, segments, missing);
        int[] times = FlitePitchMarks.Plan(targets, voice.SampleRate);
        FliteLpcRes lpcres = FliteJoin.Concat(voice, units, times, cancellation);
        short[] samples = FliteResynth.Fixedpoint(voice, lpcres, cancellation);

        int[] segmentEnds = new int[segments.Count];
        for (int k = 0; k < segmentEnds.Length; k++)
        {
            // the first half of the diphone that starts at segment k is the unit that ends where the segment ends
            segmentEnds[k] = k + 1 < segments.Count ? lpcres.UnitEnds[2 * k] : samples.Length;
        }

        return new FliteSynthResult
        {
            Samples = samples,
            SampleRate = voice.SampleRate,
            Units = units,
            PitchMarkTimes = lpcres.Times.AsSpan(0, lpcres.NumFrames).ToArray(),
            PitchMarkFrames = lpcres.Frames.AsSpan(0, lpcres.NumFrames).ToArray(),
            MissingDiphones = missing,
            SegmentEnds = segmentEnds,
        };
    }
}
