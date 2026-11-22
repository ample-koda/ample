using Ample.Speech.Flite.Core;

namespace Ample.Speech.Flite;

// The inline markup of a text is AMPLE's own; the features it sets on the tokens are the ones that Flite reads (cst_synth.c: R:Token.parent.local_duration_stretch in the duration
// model, lang/usenglish/us_f0_model.c: local_f0_shift and local_f0_range in the F0 model). The sound of a text without markup is untouched.

public sealed partial class FliteEngine
{
    /// <summary>Where every token of the utterance starts in the text, in bytes: the tokens come in order, each one as prepunctuation, name and punctuation after it,
    /// with white space between.</summary>
    internal static Dictionary<Item, int> TokenStarts(Utterance u, string byteText, int cursorStart, out int cursorEnd)
    {
        Dictionary<Item, int> starts = new(ReferenceEqualityComparer.Instance);
        int cursor = cursorStart;
        for (Item? t = u.GetRelation("Token").Head; t is not null; t = Hrg.Next(t))
        {
            string piece = Hrg.FeatString(t, "prepunctuation") + Hrg.Name(t) + Hrg.FeatString(t, "punc");
            int at = byteText.IndexOf(piece, cursor, StringComparison.Ordinal);
            if (at >= 0)
            {
                starts[t] = at + Hrg.FeatString(t, "prepunctuation").Length;
                cursor = at + piece.Length;
            }
        }

        cursorEnd = cursor;
        return starts;
    }

    /// <summary>Sets <c>local_duration_stretch</c>, <c>local_f0_shift</c> and <c>local_f0_range</c> on the tokens that a tag covers.</summary>
    private static void ApplyMarkup(Utterance u, string byteText, int cursorStart, FliteMarkup markup)
    {
        foreach ((Item token, int at) in TokenStarts(u, byteText, cursorStart, out _))
        {
            if (!markup.TokenFeatures(at, out float stretch, out float shift, out float range)) continue;
            if (stretch != 0) Hrg.SetFloat(token, "local_duration_stretch", stretch);
            if (shift != 0) Hrg.SetFloat(token, "local_f0_shift", shift);
            if (range != 0) Hrg.SetFloat(token, "local_f0_range", range);
        }
    }
}
