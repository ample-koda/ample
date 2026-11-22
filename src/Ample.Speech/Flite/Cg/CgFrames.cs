using Ample.Speech.Flite.Core;
using Ample.Speech.Flite.Data;
using Ample.Speech.Flite.Prosody;

namespace Ample.Speech.Flite.Cg;

/// <summary>The utterance needs more frames than the budget of the render allows.</summary>
internal sealed class FliteCgBudgetException(string message) : InvalidOperationException(message);

// Port of Flite (CMU, commit 6c9f20dc), src/cg/cst_cg.c. Translated to C# and modified.
// Copyright and licence: LICENSES/flite.txt, including the original authors.
internal static class CgFrames
{
    /// <summary>Plan state durations and 5 ms parameter frames; this does not synthesize audio.</summary>
    public static int Make(Utterance utterance, FliteCgVoiceData voice, float durationStretch, int maxFrames,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrames);
        if (!float.IsFinite(durationStretch) || durationStretch <= 0) throw new ArgumentOutOfRangeException(nameof(durationStretch));
        if (!float.IsFinite(voice.FrameAdvance) || voice.FrameAdvance <= 0 || voice.DurationStats.Count == 0)
            throw new InvalidDataException("The clustergen duration data is invalid.");
        cancellationToken.ThrowIfCancellationRequested();
        Cart tree = FliteProsodyVoice.ToCart(voice.DurationCart);
        Relation frames = utterance.RelationCreate("mcep");
        Relation links = utterance.RelationCreate("mcep_link");
        float end = 0;
        int count = 0;
        for (Item? state = utterance.GetRelation("HMMstate").Head; state is not null; state = Hrg.Next(state))
        {
            cancellationToken.ThrowIfCancellationRequested();
            float score = Values.Float(tree.Interpret(state));
            FliteDurationStat stat = voice.DurationStats.FirstOrDefault(s => s.Phone == Hrg.Name(state)) ?? voice.DurationStats[0];
            float duration = (score * stat.Stddev) + stat.Mean;
            float local = FeaturePath.FfeatureFloat(state,
                "R:segstate.parent.R:SylStructure.parent.parent.R:Token.parent.local_duration_stretch");
            if (local == 0) local = 1;
            float adjusted = local * durationStretch * duration;
            if (!float.IsFinite(adjusted)) throw new InvalidDataException("The clustergen state duration is not finite.");
            end += adjusted < voice.FrameAdvance ? voice.FrameAdvance : adjusted;
            Hrg.SetFloat(state, "end", end);
            Item parent = links.Append(state);
            while (count * voice.FrameAdvance <= end)
            {
                if (count >= maxFrames) throw new FliteCgBudgetException("The clustergen frame budget was exceeded.");
                if ((count & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                Item frame = frames.Append(null);
                Hrg.AddDaughter(parent, frame);
                Hrg.SetInt(frame, "frame_number", count++);
                Hrg.Set(frame, "name", Hrg.Feat(parent, "name"));
            }
        }

        for (Item? segment = utterance.GetRelation("Segment").Head; segment is not null; segment = Hrg.Next(segment))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Hrg.Set(segment, "end", FeaturePath.Ffeature(segment, "R:segstate.daughtern.end"));
        }

        utterance.Features.SetInt("param_track_num_frames", count);
        return count;
    }
}
