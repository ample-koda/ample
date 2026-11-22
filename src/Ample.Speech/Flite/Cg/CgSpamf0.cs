using Ample.Speech.Flite.Core;
using Ample.Speech.Flite.Prosody;

namespace Ample.Speech.Flite.Cg;

// Port of Flite (CMU, commit 6c9f20dc), src/cg/cst_spamf0.c (cst_spamf0, cst_synthtilt). Translated to C# and modified: the trees and the accent vectors come from the converted data,
// exp is CgMath.Exp (never the maths library of the platform), and every float expression keeps the types and the order of the C expression.

/// <summary>The accent and phrase F0 model of a clustergen voice (<c>spamf0</c>, only <c>rms</c> uses it): the F0 of every frame is a level from the phrase tree plus the tilt events of the accents
/// (rise and fall, in the log domain), and it replaces the F0 that the cluster trees predicted.</summary>
internal static class CgSpamf0
{
    /// <summary><c>cst_spamf0</c>: writes the F0 of the model into column 0 of the parameter track, for every frame.</summary>
    /// <param name="utterance">The utterance with the Segment and Syllable relations and the frames.</param>
    /// <param name="voice">The voice (with the accent tree, the phrase tree and the accent vectors).</param>
    /// <param name="track">The parameter track whose F0 column is replaced.</param>
    /// <param name="cancellationToken">Stops the work between two syllables.</param>
    public static void Apply(Utterance utterance, FliteCgVoiceData voice, CgParamTrack track, CancellationToken cancellationToken)
    {
        if (voice.Spamf0AccentTree is null || voice.Spamf0PhraseTree is null || voice.Spamf0AccentVectors is null) throw new InvalidDataException("The voice has no spamf0 data.");
        Cart accentTree = FliteProsodyVoice.ToCart(voice.Spamf0AccentTree), phraseTree = FliteProsodyVoice.ToCart(voice.Spamf0PhraseTree);
        int frames = FeatureSet.Int(utterance.Features, "param_track_num_frames");
        float[] f0 = new float[frames];
        int numFrames = 0;
        for (Item? s = utterance.GetRelation("Segment").Head; s is not null; s = Hrg.Next(s))
        {
            float end = FeaturePath.FfeatureFloat(s, "end");
            float f0Value = FeaturePath.FfeatureString(s, "name") == "pau" ? 0 : Values.Float(phraseTree.Interpret(s));
            for (; (numFrames * voice.FrameAdvance) <= end && numFrames < frames; numFrames++) f0[numFrames] = f0Value;
        }

        for (Item? s = utterance.GetRelation("Syllable").Head; s is not null; s = Hrg.Next(s))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int accent = Values.Int(accentTree.Interpret(s));
            float syllableDuration = FeaturePath.FfeatureFloat(s, "R:SylStructure.daughtern.R:Segment.end") - FeaturePath.FfeatureFloat(s, "R:SylStructure.daughter1.R:Segment.p.end");
            float[] vector = voice.Spamf0AccentVectors[accent];
            SynthTilt(voice, FeaturePath.FfeatureFloat(s, "R:SylStructure.daughter1.R:Segment.p.end"), vector[0], vector[2], syllableDuration, vector[6], f0);
        }

        for (int i = 0; i < frames; i++) track.Frames[i][0] = f0[i];
    }

    /// <summary><c>cst_synthtilt</c>: the rise and the fall of one tilt event added to the frames (in the log domain) and made linear with exp.</summary>
    private static void SynthTilt(FliteCgVoiceData voice, float start, float peak, float tiltAmplitude, float tiltDuration, float tiltTilt, float[] f0)
    {
        float frameAdvance = voice.FrameAdvance;
        float arise = tiltAmplitude * (1 + tiltTilt) / 2;
        float afall = tiltAmplitude * (1 - tiltTilt) / 2;
        float drise = tiltDuration * (1 + tiltTilt) / 2;
        float dfall = tiltDuration * (1 - tiltTilt) / 2;
        int numFrames = (int)Math.Ceiling((double)(start / frameAdvance));
        float i;

        // the rise event: the first half
        for (i = frameAdvance; (numFrames * frameAdvance) < (start + (drise / 2)); numFrames++, i += frameAdvance)
        {
            if (numFrames >= f0.Length) continue;   // the C program writes behind the track here (undefined); the values are not used
            f0[numFrames] += peak - arise + (2 * arise * (i / drise) * (i / drise));
            f0[numFrames] = (float)CgMath.Exp(f0[numFrames]);
        }

        // the second half
        for (; (numFrames * frameAdvance) < (start + drise); numFrames++, i += frameAdvance)
        {
            if (numFrames >= f0.Length) continue;
            f0[numFrames] += peak - (2 * arise * (1 - (i / drise)) * (1 - (i / drise)));
            f0[numFrames] = (float)CgMath.Exp(f0[numFrames]);
        }

        // the fall event: the first half
        for (i = frameAdvance; (numFrames * frameAdvance) < (start + drise + (dfall / 2)); numFrames++, i += frameAdvance)
        {
            if (numFrames >= f0.Length) continue;
            f0[numFrames] += peak + afall - (2 * afall * (i / dfall) * (i / dfall)) - afall;
            f0[numFrames] = (float)CgMath.Exp(f0[numFrames]);
        }

        // the second half
        for (; (numFrames * frameAdvance) < (start + drise + dfall); numFrames++, i += frameAdvance)
        {
            if (numFrames >= f0.Length) continue;
            f0[numFrames] += peak + (2 * afall * (1 - (i / dfall)) * (1 - (i / dfall))) - afall;
            f0[numFrames] = (float)CgMath.Exp(f0[numFrames]);
        }
    }
}
