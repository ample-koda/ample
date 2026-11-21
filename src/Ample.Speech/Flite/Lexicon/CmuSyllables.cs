using Ample.Speech.Flite.Core;

namespace Ample.Speech.Flite.Lexicon;

// Port of Flite (CMU, commit 6c9f20dc), lang/cmulex/cmu_lex.c (cmu_syl_boundary_mo and its helpers). Translated to C# and modified: see Text/UsText.cs.

/// <summary>
/// The syllable boundary rules of the CMU lexicon (<c>cmu_syl_boundary_mo</c> and its helpers in <c>cmu_lex.c</c>): maximal onset, with the vowel test of the US English phone set
/// ("this happens to work": a phone is a vowel when its first letter is one of "aeiou"). A literal translation, including the way <c>strchr</c> treats an empty phone name.
/// </summary>
internal static class CmuSyllables
{
    private static readonly string[] OnsetTrigrams = ["str", "spy", "spr", "spl", "sky", "skw", "skr", "skl"];

    private static readonly string[] OnsetBigrams =
    [
        "zw", "zl",
        "vy", "vr", "vl",
        "thw", "thr",
        "ty", "tw",
        "tr",
        "shw", "shr", "shn", "shm", "shl",
        "sw", "sv", "st", "sr", "sp", "sn", "sm", "sl", "sk", "sf",
        "py", "pw", "pr", "pl",
        "ny",
        "my", "mr",
        "ly",
        "ky", "kw", "kr", "kl",
        "hhy", "hhw", "hhr", "hhl",
        "gy", "gw", "gr", "gl",
        "fy", "fr", "fl",
        "dy", "dw", "dr",
        "by", "bw", "br", "bl",
    ];

    /// <summary><c>cmu_is_vowel</c>: the first character is one of "aeiou" (<c>strchr</c> also finds the terminating NUL of an empty name).</summary>
    public static bool IsVowel(string phone) => StrChr("aeiou", phone);

    /// <summary><c>cmu_is_silence</c>.</summary>
    public static bool IsSilence(string phone) => string.Equals(phone, "pau", StringComparison.Ordinal);

    /// <summary><c>cmu_syl_boundary_mo</c>: whether the segment ends a syllable, given the phones that are left after it.</summary>
    public static bool BoundaryMaximalOnset(Item segment, ReadOnlySpan<string> rest)
    {
        if (rest.IsEmpty)
        {
            return true;
        }

        if (IsSilence(rest[0]))
        {
            return true;
        }

        if (!HasVowelInList(rest))
        {
            return false;
        }

        if (!HasVowelInSyllable(segment))
        {
            return false;
        }

        if (IsVowel(rest[0]))
        {
            return true;
        }

        if (string.Equals(rest[0], "ng", StringComparison.Ordinal))
        {
            return false;
        }

        int d2v = DistanceToVowel(rest);
        if (d2v < 2)
        {
            return true;
        }

        if (d2v > 3)
        {
            return false;
        }

        return d2v == 2 ? OnsetBigram(rest) : OnsetTrigram(rest);
    }

    private static bool StrChr(string set, string phone)
    {
        // strchr(set, p[0]) is non-NULL for the NUL of an empty name
        return phone.Length == 0 || set.Contains(phone[0], StringComparison.Ordinal);
    }

    private static bool HasVowelInList(ReadOnlySpan<string> rest)
    {
        foreach (string phone in rest)
        {
            if (IsVowel(phone))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasVowelInSyllable(Item segment)
    {
        // the segments before this one in the syllable (item_prev along the daughters of the syllable)
        for (Item? n = segment; n is not null; n = Hrg.Prev(n))
        {
            if (IsVowel(Hrg.FeatString(n, "name")))
            {
                return true;
            }
        }

        return false;
    }

    private static int DistanceToVowel(ReadOnlySpan<string> rest)
    {
        int d = 0;
        while (!rest.IsEmpty && !IsVowel(rest[0]))
        {
            d++;
            rest = rest[1..];
        }

        return d;   // an exhausted list ends the recursion of C with 0 ("shouldn't get here")
    }

    private static bool OnsetBigram(ReadOnlySpan<string> rest) => Array.IndexOf(OnsetBigrams, rest[0] + rest[1]) >= 0;

    private static bool OnsetTrigram(ReadOnlySpan<string> rest) => Array.IndexOf(OnsetTrigrams, rest[0] + rest[1] + rest[2]) >= 0;
}
