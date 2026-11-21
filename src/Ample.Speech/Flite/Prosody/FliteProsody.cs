using Ample.Speech.Flite.Core;
using Ample.Speech.Flite.Data;

namespace Ample.Speech.Flite.Prosody;

// Port of Flite (CMU, commit 6c9f20dc), src/synth/cst_synth.c (default_phrasing, default_pause_insertion, cart_intonation, cart_duration) and
// lang/usenglish/us_f0_model.c. Translated to C# and modified: the C text and the order of operations are kept; floats stay floats, doubles stay doubles.
// Copyright (c) 2000-2001 Carnegie Mellon University. All Rights Reserved. Author of the C code: Alan W Black. See FLITE-NOTICE.md.

/// <summary>The prosody modules of the kal16 voice: phrasing, pauses, accents and tones, segment durations and the F0 target model.</summary>
internal static class FliteProsody
{
    // model mean and stddev taken from f2b/kal_diphone (us_f0_model.c)
    private const double ModelMean = 170.0;
    private const int ModelStddev = 34;

    /// <summary><c>default_phrasing</c>: the Phrase relation; every word is a daughter of a phrase, a "BB" from the phrasing tree ends the phrase, the last phrase is named "BB" when it is not the first.</summary>
    /// <param name="u">The utterance with the Word relation (and the features <c>pos</c> and <c>gpos</c> the tree asks for).</param>
    public static Utterance Phrasing(Utterance u)
    {
        Relation r = u.RelationCreate("Phrase");
        Core.Cart? phrasingCart = FeatureSet.Present(u.Features, "phrasing_cart") ? CartOf(u, "phrasing_cart") : null;
        Item? p = null;
        Item? lp = null;
        for (Item? w = u.GetRelation("Word").Head; w is not null; w = Hrg.Next(w))
        {
            if (p is null)
            {
                p = r.Append(null);
                lp = p;
                Hrg.SetString(p, "name", "B");
            }

            Hrg.AddDaughter(p, w);
            if (phrasingCart is not null)
            {
                Value? v = phrasingCart.Interpret(w);
                if (string.Equals(Values.String(v), "BB", StringComparison.Ordinal))
                {
                    p = null;
                }
            }
        }

        if (lp is not null && Hrg.Prev(lp) is not null)
        {
            Hrg.SetString(lp, "name", "BB");
        }

        return u;
    }

    /// <summary><c>default_pause_insertion</c>: an initial silence, and a silence after the last segment of the last word with segments of every phrase.</summary>
    /// <param name="u">The utterance with the Phrase, SylStructure and Segment relations.</param>
    public static Utterance PauseInsertion(Utterance u)
    {
        string silence = Values.String(FeatureSet.Val(u.Features, "silence"));
        Item? s = u.GetRelation("Segment").Head;
        s = s is null ? u.GetRelation("Segment").Append(null) : Hrg.Prepend(s, null);
        Hrg.SetString(s, "name", silence);
        for (Item? p = u.GetRelation("Phrase").Head; p is not null; p = Hrg.Next(p))
        {
            for (Item? w = Hrg.LastDaughter(p); w is not null; w = Hrg.Prev(w))
            {
                Item? last = FeaturePath.PathToItem(w, "R:SylStructure.daughtern.daughtern.R:Segment");
                if (last is not null)
                {
                    Item added = Hrg.Append(last, null);
                    Hrg.SetString(added, "name", silence);
                    break;
                }
            }
        }

        return u;
    }

    /// <summary><c>cart_intonation</c>: the accent and the end tone of every syllable from the two trees ("NONE" leaves the feature unset).</summary>
    /// <param name="u">The utterance with the Syllable relation.</param>
    public static Utterance Intonation(Utterance u)
    {
        if (FeatureSet.Present(u.Features, "no_intonation_accent_model"))
        {
            return u;
        }

        Core.Cart accents = CartOf(u, "int_cart_accents");
        Core.Cart tones = CartOf(u, "int_cart_tones");
        for (Item? s = u.GetRelation("Syllable").Head; s is not null; s = Hrg.Next(s))
        {
            Value? v = accents.Interpret(s);
            if (!string.Equals("NONE", Values.String(v), StringComparison.Ordinal))
            {
                Hrg.SetString(s, "accent", Values.String(v));
            }

            v = tones.Interpret(s);
            if (!string.Equals("NONE", Values.String(v), StringComparison.Ordinal))
            {
                Hrg.SetString(s, "endtone", Values.String(v));
            }
        }

        return u;
    }

    /// <summary><c>cart_duration</c>: the end time of every segment from the z-score of the tree, the mean and the deviation of the phone and the stretch. All float arithmetic as in C.</summary>
    /// <param name="u">The utterance with the Segment relation (with silences) and the SylStructure relation.</param>
    public static Utterance Duration(Utterance u)
    {
        float end = 0;
        if (FeatureSet.Present(u.Features, "no_segment_duration_model"))
        {
            return u;
        }

        Core.Cart durTree = CartOf(u, "dur_cart");
        float durStretch = Values.Float(FeatureSet.Param(u.Features, "duration_stretch", new FloatValue(1.0f)));
        IReadOnlyList<FliteDurationStat> stats = (IReadOnlyList<FliteDurationStat>)((ObjectValue)FeatureSet.Val(u.Features, "dur_stats")!).Target;
        for (Item? s = u.GetRelation("Segment").Head; s is not null; s = Hrg.Next(s))
        {
            float zdur = Values.Float(durTree.Interpret(s));
            FliteDurationStat stat = PhoneDurationStat(stats, Hrg.Name(s));
            float localDurStretch = FeaturePath.FfeatureFloat(s, "R:SylStructure.parent.parent.R:Token.parent.local_duration_stretch");
            if (localDurStretch != 0)
            {
                localDurStretch *= durStretch;
            }
            else
            {
                localDurStretch = durStretch;
            }

            float scaled = (float)(zdur * stat.Stddev);
            float dur = (float)(localDurStretch * (float)(scaled + stat.Mean));
            end = (float)(end + dur);
            Hrg.SetFloat(s, "end", end);
        }

        return u;
    }

    /// <summary><c>us_f0_model</c>: three F0 targets per syllable from the linear regression terms (Black and Hunt, ICSLP96), and the targets at the start and end of the utterance.</summary>
    /// <param name="u">The utterance with the Syllable, SylStructure and Segment relations and the segment ends.</param>
    /// <param name="terms">The linear regression terms (<c>f0_lr_terms</c>, the first row holds the intercepts).</param>
    public static Utterance F0Model(Utterance u, IReadOnlyList<FliteF0Term> terms)
    {
        if (FeatureSet.Present(u.Features, "no_f0_target_model"))
        {
            return u;
        }

        Relation targ = u.RelationCreate("Target");
        float mean = Values.Float(FeatureSet.Param(u.Features, "int_f0_target_mean", new FloatValue(100.0f)));
        mean *= Values.Float(FeatureSet.Param(u.Features, "f0_shift", new FloatValue(1.0f)));
        float stddev = Values.Float(FeatureSet.Param(u.Features, "int_f0_target_stddev", new FloatValue(12.0f)));
        float lend = 0;
        for (Item? syl = u.GetRelation("Syllable").Head; syl is not null; syl = Hrg.Next(syl))
        {
            if (Hrg.Daughter(Hrg.ItemAs(syl, "SylStructure")) is null)
            {
                continue;
            }

            float localMean = FeaturePath.FfeatureFloat(syl, "R:SylStructure.parent.R:Token.parent.local_f0_shift");
            if (localMean != 0)
            {
                localMean *= mean;
            }
            else
            {
                localMean = mean;
            }

            float localStddev = FeaturePath.FfeatureFloat(syl, "R:SylStructure.parent.R:Token.parent.local_f0_range");
            if (localStddev == 0.0)
            {
                localStddev = stddev;
            }

            ApplyLrModel(syl, terms, out float start, out float mid, out float end);
            if (PostBreak(syl))
            {
                lend = (float)MapF0(start, localMean, localStddev);
            }

            float sum = start + lend;
            AddTargetPoint(targ, FeaturePath.FfeatureFloat(syl, "R:SylStructure.daughter.R:Segment.p.end"), (float)MapF0(sum / 2.0, localMean, localStddev));
            AddTargetPoint(targ, VowelMid(syl), (float)MapF0(mid, localMean, localStddev));
            lend = (float)MapF0(end, localMean, localStddev);
            if (PreBreak(syl))
            {
                AddTargetPoint(targ, FeaturePath.FfeatureFloat(syl, "R:SylStructure.daughtern.end"), (float)MapF0(end, localMean, localStddev));
            }
        }

        // Guarantee targets go from start to end of utterance
        Item? t = targ.Head;
        if (t is null)
        {
            AddTargetPoint(targ, 0, mean);
        }
        else if (Hrg.FeatFloat(t, "pos") > 0)
        {
            Item nt = Hrg.Prepend(t, null);
            Hrg.SetFloat(nt, "pos", 0.0f);
            Hrg.SetFloat(nt, "f0", Hrg.FeatFloat(t, "f0"));
        }

        t = targ.Tail;
        float segEnd = Hrg.FeatFloat(u.GetRelation("Segment").Tail, "end");
        if (Hrg.FeatFloat(t, "pos") < segEnd)
        {
            AddTargetPoint(targ, segEnd, Hrg.FeatFloat(t, "f0"));
        }

        return u;
    }

    /// <summary><c>phone_dur_stat</c>: the statistics of the phone; the first entry of the table when the phone is not in it.</summary>
    private static FliteDurationStat PhoneDurationStat(IReadOnlyList<FliteDurationStat> stats, string phone)
    {
        foreach (FliteDurationStat stat in stats)
        {
            if (string.Equals(phone, stat.Phone, StringComparison.Ordinal))
            {
                return stat;
            }
        }

        return stats[0];
    }

    private static Core.Cart CartOf(Utterance u, string name) =>
        FeatureSet.Val(u.Features, name) is ObjectValue { TypeCode: Values.TypeCart } v ? (Core.Cart)v.Target : throw new FliteException($"VAL: tried to access cart in feature {name}");

    /// <summary><c>apply_lr_model</c>: the intercepts plus the terms of the features (a feature with a type is 1 when it equals the type, else 0).</summary>
    private static void ApplyLrModel(Item s, IReadOnlyList<FliteF0Term> terms, out float start, out float mid, out float end)
    {
        Value? v = null;
        start = terms[0].Start;
        mid = terms[0].Mid;
        end = terms[0].End;
        for (int i = 1; i < terms.Count && terms[i].Feature is not null; i++)
        {
            if (!string.Equals(terms[i].Feature, terms[i - 1].Feature, StringComparison.Ordinal))
            {
                v = FeaturePath.Ffeature(s, terms[i].Feature!);
            }

            float fv;
            if (terms[i].Type is not null)
            {
                fv = string.Equals(Values.String(v), terms[i].Type, StringComparison.Ordinal) ? 1.0f : 0.0f;
            }
            else
            {
                fv = Values.Float(v);
            }

            start += (float)(fv * terms[i].Start);
            mid += (float)(fv * terms[i].Mid);
            end += (float)(fv * terms[i].End);
        }
    }

    /// <summary><c>add_target_point</c>: appends a target; an F0 above 500 or below 50 Hz is clamped.</summary>
    private static void AddTargetPoint(Relation targ, float pos, float f0)
    {
        Item t = targ.Append(null);
        Hrg.SetFloat(t, "pos", pos);
        if (f0 > 500.0)
        {
            Hrg.SetFloat(t, "f0", 500.0f);
        }
        else if (f0 < 50.0)
        {
            Hrg.SetFloat(t, "f0", 50.0f);
        }
        else
        {
            Hrg.SetFloat(t, "f0", f0);
        }
    }

    /// <summary>The <c>map_f0</c> macro: the value in the model scale to the scale of the voice. Doubles throughout, as the C constants are doubles.</summary>
    private static double MapF0(double v, float m, float s) => (((v - ModelMean) / ModelStddev) * s) + m;

    /// <summary><c>post_break</c>: the syllable is the first or follows a pause.</summary>
    private static bool PostBreak(Item syl) =>
        Hrg.Prev(syl) is null || string.Equals("pau", FeaturePath.FfeatureString(syl, "R:SylStructure.daughter.R:Segment.p.name"), StringComparison.Ordinal);

    /// <summary><c>pre_break</c>: the syllable is the last or is followed by a pause.</summary>
    private static bool PreBreak(Item syl) =>
        Hrg.Next(syl) is null || string.Equals("pau", FeaturePath.FfeatureString(syl, "R:SylStructure.daughtern.R:Segment.n.name"), StringComparison.Ordinal);

    /// <summary><c>vowel_mid</c>: the time half way through the vowel of the syllable (or of its first segment when it has no vowel).</summary>
    private static float VowelMid(Item syl)
    {
        Phoneset ps = Phoneset.OfItem(syl);
        Item? ts = Hrg.Daughter(Hrg.ItemAs(syl, "SylStructure"));
        for (Item? s = ts; s is not null; s = Hrg.Next(s))
        {
            if (string.Equals("+", ps.PhoneFeatureString(Hrg.FeatString(s, "name"), "vc"), StringComparison.Ordinal))
            {
                float sum = Hrg.FeatFloat(s, "end") + FeaturePath.FfeatureFloat(s, "R:Segment.p.end");
                return (float)(sum / 2.0);
            }
        }

        if (ts is null)
        {
            return 0;
        }

        float sumFirst = Hrg.FeatFloat(ts, "end") + FeaturePath.FfeatureFloat(ts, "R:Segment.p.end");
        return (float)(sumFirst / 2.0);
    }
}
