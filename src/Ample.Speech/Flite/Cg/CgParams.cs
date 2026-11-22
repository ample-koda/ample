using Ample.Speech.Flite.Core;
using Ample.Speech.Flite.Prosody;

namespace Ample.Speech.Flite.Cg;

// Port of Flite (CMU, commit 6c9f20dc), src/cg/cst_cg.c (cg_predict_params, cg_smooth_F0, cg_F0_interpolate_spline, catmull_rom_spline, voiced_frame, unpack_model_vector).
// Translated to C# and modified: the trees and the cluster vectors come from the converted data; the floats and doubles keep the types and the order of the C expressions.

/// <summary>The parameter tracks of an utterance before smoothing: one row for each 5 ms frame.</summary>
internal sealed class CgParamTrack
{
    /// <summary>Gets the number of columns of <see cref="Frames"/>: F0, an unused column (zero), then the mean and standard deviation pairs of the mel-cepstrum.</summary>
    public const int Columns = 102;

    /// <summary>Gets the number of mixed excitation strengths of a frame.</summary>
    public const int StrengthColumns = 5;

    /// <summary>Gets the rows: F0 in column 0, then (from column 2) the values of the cluster vector.</summary>
    public required float[][] Frames { get; init; }

    /// <summary>Gets the five mixed excitation strengths of each frame.</summary>
    public required float[][] Strengths { get; init; }

    /// <summary>Gets the time of each frame in seconds.</summary>
    public required float[] Times { get; init; }
}

/// <summary>The trees of a voice as interpretable trees, made once.</summary>
internal sealed class CgTrees
{
    public CgTrees(FliteCgVoiceData voice)
    {
        F0 = [.. voice.F0Trees.Select(FliteProsodyVoice.ToCart)];
        Mcep = [.. voice.McepTrees.Select(FliteProsodyVoice.ToCart)];
        Duration = FliteProsodyVoice.ToCart(voice.DurationCart);
    }

    public Cart[] F0 { get; }

    public Cart[] Mcep { get; }

    public Cart Duration { get; }
}

internal static class CgParams
{
    private const string TokenPath = "R:mcep_link.parent.R:segstate.parent.R:SylStructure.parent.parent.R:Token.parent.";

    /// <summary>Puts every voiced frame (an F0 above 0) on one pitch, kept from 50 to 700 Hz as <c>cg_smooth_F0</c> keeps it. Not in the C program: the way to a steady pitch
    /// for the voices whose F0 is written by <c>cst_spamf0</c> after the scaling (the voice rms), where <c>int_f0_target_mean</c> and <c>int_f0_target_stddev</c> have no effect.</summary>
    /// <param name="track">The parameter track after <c>cst_spamf0</c>.</param>
    /// <param name="hz">The pitch in Hz.</param>
    public static void PutOnSteadyPitch(CgParamTrack track, float hz)
    {
        ArgumentNullException.ThrowIfNull(track);
        float steady = Math.Min(Math.Max(hz, 50f), 700f);
        foreach (float[] row in track.Frames)
        {
            if (row[0] > 0f) row[0] = steady;
        }
    }

    /// <summary><c>cg_predict_params</c> followed by <c>cg_smooth_F0</c>: for each frame the F0, the cluster vector and the voicing,
    /// then the F0 is smoothed over the syllables and unvoiced frames are zero.</summary>
    public static CgParamTrack Predict(Utterance utterance, FliteCgVoiceData voice, CgTrees trees, CancellationToken cancellationToken)
    {
        // one model of each kind (the voices have no multi model), the deltas are in the model (do_mlpg): the rows are not doubled
        int frames = FeatureSet.Int(utterance.Features, "param_track_num_frames");
        int columns = (voice.NumChannels / 1) - (2 * 6);
        if (columns != CgParamTrack.Columns || !voice.MixedExcitation || !voice.DoMlpg || voice.MultiModel) throw new InvalidDataException("The clustergen voice has a shape that is not supported.");
        float[][] rows = new float[frames][], strengths = new float[frames][];
        float[] times = new float[frames];
        float[] vector = new float[voice.NumChannels];
        ReadOnlySpan<float> min = voice.ModelMin.Span, range = voice.ModelRange.Span;
        int i = 0;
        for (Item? frame = utterance.GetRelation("mcep").Head; frame is not null; i++, frame = Hrg.Next(frame))
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            string name = Hrg.Name(frame);
            float localGain = FeaturePath.FfeatureFloat(frame, TokenPath + "local_gain");
            if (localGain == 0.0) localGain = 1.0f;
            int p = 0;
            for (int t = 0; t < voice.Types.Count; t++)
            {
                if (voice.Types[t] == name)
                {
                    p = t;
                    break;
                }
            }

            float[] row = rows[i] = new float[columns];
            float[] strength = strengths[i] = new float[CgParamTrack.StrengthColumns];

            // F0 (one model: the sum is the one answer divided by one)
            float f0 = 0;
            float f0Bit = Values.Float(trees.F0[p].Interpret(frame));
            f0 += f0Bit;
            row[0] = f0 / 1;
            if (row[0] < 50.0) row[0] = 0.0f;

            // the spectral cluster
            float voicing = 0.0f;
            int cluster = Values.Int(trees.Mcep[p].Interpret(frame));
            Hrg.SetInt(frame, "clustergen_param_frame", cluster);
            for (int c = 0; c < vector.Length; c++)
            {
                // unpack_model_vector, shape BASE_MINRANGE: a 16 bit number scaled with the minimum and range of its channel
                vector[c] = min[c] + ((float)(voice.Parameter(cluster, c) / 65535.0) * range[c]);
            }

            for (int j = 2; j < columns; j++)
            {
                row[j] = 0.0f;
                row[j] += vector[j] / 1;
            }

            int o = columns;
            for (int j = 0; j < CgParamTrack.StrengthColumns; j++)
            {
                strength[j] = 0.0f;
                strength[j] += vector[o + (2 * j)] / 1;
            }

            // the last coefficient is the average voicing of the cluster
            voicing /= 1;
            voicing += vector[voice.NumChannels - 2] / 1;
            Hrg.SetFloat(frame, "voicing", voicing);
            row[2] *= localGain;
            times[i] = i * voice.FrameAdvance;
        }

        CgParamTrack track = new() { Frames = rows, Strengths = strengths, Times = times };
        SmoothF0(utterance, voice, track, cancellationToken);
        return track;
    }

    private static float CatmullRom(float p, float p0, float p1, float p2, float p3)
    {
        // the doubles of the C expression: 0.5, 2.0, 5.0, 4.0 and 3.0 are double literals, so everything that touches one is a double; p, p0..p3 are floats
        float q = (float)(0.5 *
            (((2.0 * p1) + (p * (-p0 + p2)) + ((p * p) * (((2.0 * p0) - (5.0 * p1)) + ((4.0 * p2) - p3)))) +
             ((p * p * p) * (-p0 + ((3.0 * p1) - (3.0 * p2)) + p3))));
        return q;
    }

    private static void InterpolateSpline(Utterance utterance, CgParamTrack track)
    {
        float startF0 = -1.0f, midF0 = -1.0f, endF0 = -1.0f;
        float[][] frames = track.Frames;
        for (Item? syl = utterance.GetRelation("Syllable").Head; syl is not null; syl = Hrg.Next(syl))
        {
            int startIndex = FeaturePath.FfeatureInt(syl, "R:SylStructure.daughter1.R:segstate.daughter1.R:mcep_link.daughter1.frame_number");
            int endIndex = FeaturePath.FfeatureInt(syl, "R:SylStructure.daughtern.R:segstate.daughtern.R:mcep_link.daughtern.frame_number");
            int midIndex = (int)((startIndex + endIndex) / 2.0);
            if (endIndex <= startIndex) continue;
            startF0 = frames[startIndex][0];
            if (endF0 > 0.0) startF0 = endF0;   // not first time through
            float prevMidF0 = midF0 < 0.0 ? startF0 : midF0;
            midF0 = frames[midIndex][0];
            bool last = Hrg.Next(syl) is null;
            endF0 = !last ? (float)((frames[endIndex - 1][0] + frames[endIndex][0]) / 2.0) : frames[endIndex - 1][0];
            float nextMidF0 = endF0;   // in case there is no next syllable
            if (!last)
            {
                int nsi = FeaturePath.FfeatureInt(syl, "n.R:SylStructure.daughter1.R:segstate.daughter1.R:mcep_link.daughter1.frame_number");
                int nei = FeaturePath.FfeatureInt(syl, "n.R:SylStructure.daughtern.R:segstate.daughtern.R:mcep_link.daughtern.frame_number");
                int nmi = (int)((nsi + nei) / 2.0);
                nextMidF0 = frames[nmi][0];
            }

            // start to the middle of the syllable
            float m = (float)(1.0 / (midIndex - startIndex));
            for (int i = 0; (startIndex + i) < midIndex; i++) frames[startIndex + i][0] = CatmullRom(i * m, prevMidF0, startF0, midF0, endF0);

            // the middle to the end
            m = (float)(1.0 / (endIndex - midIndex));
            for (int i = 0; (midIndex + i) < endIndex; i++) frames[midIndex + i][0] = CatmullRom(i * m, startF0, midF0, endF0, nextMidF0);
        }
    }

    private static bool VoicedFrame(Item frame)
    {
        string vc = FeaturePath.FfeatureString(frame, "R:mcep_link.parent.R:segstate.parent.ph_vc");
        string name = FeaturePath.FfeatureString(frame, "R:mcep_link.parent.R:segstate.parent.name");
        if (name == "pau") return false;   // unvoiced
        if (vc == "+") return true;        // voiced
        return FeatureItemFloat(frame, "voicing") > 0.5;   // the range is 0 to 10, and 0.5 is meant
    }

    private static float FeatureItemFloat(Item frame, string name) => Hrg.FeatFloat(frame, name);

    private static void SmoothF0(Utterance utterance, FliteCgVoiceData voice, CgParamTrack track, CancellationToken cancellationToken)
    {
        InterpolateSpline(utterance, track);
        float baseMean = Values.Float(FeatureSet.Param(utterance.Features, "int_f0_target_mean", new FloatValue(voice.F0Mean)));
        baseMean *= Values.Float(FeatureSet.Param(utterance.Features, "f0_shift", new FloatValue(1.0f)));
        float baseStddev = Values.Float(FeatureSet.Param(utterance.Features, "int_f0_target_stddev", new FloatValue(voice.F0Stddev)));
        int i = 0;
        for (Item? frame = utterance.GetRelation("mcep").Head; frame is not null; i++, frame = Hrg.Next(frame))
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            float[] row = track.Frames[i];
            if (VoicedFrame(frame))
            {
                float mean = baseMean;
                float stddev = baseStddev;
                float localMean = FeaturePath.FfeatureFloat(frame, TokenPath + "local_f0_mean");
                if (localMean != 0.0) mean = localMean;
                float localRange = FeaturePath.FfeatureFloat(frame, TokenPath + "local_f0_range");
                if (localRange > 0.0) stddev = localRange - 1.0f;   // 0 means 'not set', so the value is shifted by one

                // scale the F0, which does not change it at all when the values are those of the voice
                row[0] = (((row[0] - voice.F0Mean) / voice.F0Stddev) * stddev) + mean;
                if (row[0] < 50) row[0] = 50;
                if (row[0] > 700) row[0] = 700;
            }
            else
            {
                row[0] = 0.0f;   // unvoiced
            }
        }
    }
}
