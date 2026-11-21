using Ample.Speech.Flite.Data;

namespace Ample.Speech.Flite.Lexicon;

// Port of Flite (CMU, commit 6c9f20dc), src/lexicon/cst_lts.c and lang/cmulex/cmu_lts_rules.c. Translated to C# and modified: see Text/UsText.cs.

/// <summary>
/// The letter-to-sound rules of Flite (<c>lts_apply</c> in <c>cst_lts.c</c>), as the CMU lexicon uses them: the plain letter path (the C code sets <c>letter_table</c> to 0 for it),
/// a window of four letters on each side and one extra feature position. A literal translation: the word is a string of bytes, the padding is the character '0' and the
/// word boundary is '#', so a '#' inside a word ends the prediction early, as in C.
/// </summary>
internal static class FliteLts
{
    private const byte EndOfRules = 255;
    private const byte Hash = (byte)'#';

    /// <summary>The phones the rules predict for a word, in the order of the word (<c>lts_apply</c>).</summary>
    /// <param name="word">The bytes of the word (UTF-8); letters other than a to z are skipped, but they stay in the context of the next letter.</param>
    /// <param name="feats">The extra features of the C call (the lexicon passes an empty string).</param>
    public static string[] Apply(ReadOnlySpan<byte> word, ReadOnlySpan<byte> feats = default) => Apply(FliteLtsData.Instance, word, feats);

    /// <summary><c>lts_apply</c> with the given rule data.</summary>
    public static string[] Apply(FliteLtsData rules, ReadOnlySpan<byte> word, ReadOnlySpan<byte> feats = default)
    {
        word = CString(word);
        feats = CString(feats);
        int window = rules.ContextWindowSize;
        byte[] fullBuffer = new byte[(window * 2) + word.Length + 1];
        // "%.*s#%s#%.*s" with "00000000": window - 1 zeros, the word between two hashes
        int at = 0;
        for (int i = 0; i < window - 1; i++)
        {
            fullBuffer[at++] = (byte)'0';
        }

        fullBuffer[at++] = Hash;
        word.CopyTo(fullBuffer.AsSpan(at));
        at += word.Length;
        fullBuffer[at++] = Hash;
        for (int i = 0; i < window - 1; i++)
        {
            fullBuffer[at++] = (byte)'0';
        }

        // the buffer of C is (window * 2) + length + 1 bytes and holds (window - 1) * 2 + length + 2 characters
        byte[] valueBuffer = new byte[(window * 2) + rules.ContextExtraFeatures + feats.Length + 1];   // C allocates window * 2 + extra and overflows on longer features
        List<string[]> groups = [];
        for (int pos = window + word.Length - 1; fullBuffer[pos] != Hash; pos--)
        {
            // "%.*s%.*s%s": the window to the left, the window to the right and the extra features, cut at the first NUL like sprintf does
            Array.Clear(valueBuffer);
            int n = 0;
            n = CopyCString(fullBuffer.AsSpan(pos - window, window), valueBuffer, n);
            n = CopyCString(fullBuffer.AsSpan(pos + 1, Math.Min(window, fullBuffer.Length - pos - 1)), valueBuffer, n);
            CopyCString(feats, valueBuffer, n);
            byte letter = fullBuffer[pos];
            if (letter is < (byte)'a' or > (byte)'z')
            {
                continue;
            }

            int index = (letter - 'a') % 26;
            int phone = ApplyModel(rules, valueBuffer, rules.LetterIndex[index]);
            string name = rules.PhoneTable[phone];
            if (name == "epsilon")
            {
                continue;
            }

            int dash = name.IndexOf('-', StringComparison.Ordinal);
            if (dash >= 0)
            {
                groups.Add([name[..dash], name[(dash + 1)..]]);
            }
            else
            {
                groups.Add([name]);
            }
        }

        // C walks from the last letter to the first and conses every answer to the front of the list: the result reads in the order of the word
        List<string> result = [];
        for (int i = groups.Count - 1; i >= 0; i--)
        {
            result.AddRange(groups[i]);
        }

        return [.. result];
    }

    private static int ApplyModel(FliteLtsData rules, byte[] values, int start)
    {
        FliteLtsRule state = rules.Rule(start);
        while (state.Feature != EndOfRules)
        {
            ushort next = values[state.Feature] == state.Value ? state.True : state.False;
            state = rules.Rule(next);
        }

        return state.Value;
    }

    private static ReadOnlySpan<byte> CString(ReadOnlySpan<byte> text)
    {
        int nul = text.IndexOf((byte)0);
        return nul < 0 ? text : text[..nul];
    }

    private static int CopyCString(ReadOnlySpan<byte> source, byte[] target, int at)
    {
        foreach (byte b in source)
        {
            if (b == 0)
            {
                break;
            }

            target[at++] = b;
        }

        return at;
    }
}
