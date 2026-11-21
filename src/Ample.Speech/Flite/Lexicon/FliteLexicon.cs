using System.Text;
using Ample.Speech.Flite.Data;

namespace Ample.Speech.Flite.Lexicon;

// Port of Flite (CMU, commit 6c9f20dc), src/lexicon/cst_lexicon.c and lang/cmulex/cmu_lex.c. Translated to C# and modified: see Text/UsText.cs.

/// <summary>
/// The CMU lexicon of Flite (<c>cst_lexicon.c</c>: <c>lex_lookup</c>, <c>in_lex</c>), a literal translation.
/// The entries are one byte string with Huffman-coded words and phones; a lookup is a binary search over the
/// raw bytes (so over the byte positions, not over entries) followed by a walk to the entry that matches the part of speech. Words are strings of bytes (UTF-8) like in C. Immutable and thread-safe.
/// </summary>
internal sealed class FliteLexicon : Text.ILexicon
{
    /// <summary><c>WP_SIZE</c>: the room for an uncompressed word with its part-of-speech letter; longer entries are cut like in C.</summary>
    private const int WordPositionSize = 64;

    private const byte EntryEnd = 255;

    private static readonly Lazy<FliteLexicon> Shared = new(() => new FliteLexicon(FliteLexiconData.Instance, FliteLtsData.Instance), LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly FliteLexiconData data;
    private readonly FliteLtsData lts;
    private readonly byte[] bytes;
    private readonly byte[][] addendaKeys;
    private readonly string[][] addendaPhones;

    /// <summary>Opens the lexicon over the given data.</summary>
    public FliteLexicon(FliteLexiconData data, FliteLtsData lts)
    {
        this.data = data;
        this.lts = lts;
        bytes = data.Data.ToArray();
        addendaKeys = new byte[data.Addenda.Count][];
        addendaPhones = new string[data.Addenda.Count][];
        for (int i = 0; i < addendaKeys.Length; i++)
        {
            IReadOnlyList<string> entry = data.Addenda[i];
            addendaKeys[i] = Encoding.UTF8.GetBytes(entry[0]);
            addendaPhones[i] = [.. entry.Skip(1)];
        }
    }

    /// <summary>The lexicon of the voice (<c>cmu_lex_init</c>).</summary>
    public static FliteLexicon Cmu => Shared.Value;

    /// <summary>The number of entries (<c>cmu_lex_num_entries</c>).</summary>
    public int NumEntries => data.NumEntries;

    /// <summary><c>lex_lookup</c>: the phones of a word (with the stress digit of vowels), from the addenda, then the lexicon, then the letter-to-sound rules.</summary>
    /// <param name="word">The bytes of the word.</param>
    /// <param name="pos">The first character of the part of speech, or '0' when there is none (<c>pos ? pos[0] : '0'</c>).</param>
    public string[] Lookup(ReadOnlySpan<byte> word, char pos = '0')
    {
        byte[] wp = WordPosition(word, pos);
        if (FindInAddenda(wp, out string[] fromAddenda))
        {
            return fromAddenda;
        }

        int index = Bsearch(wp);
        if (index >= 0)
        {
            return PhonesAt(index);
        }

        return FliteLts.Apply(lts, CString(word));
    }

    /// <summary><c>lex_lookup</c> for a string; the word is taken as UTF-8.</summary>
    public string[] Lookup(string word, char pos = '0') => Lookup(Encoding.UTF8.GetBytes(word), pos);

    /// <summary><c>in_lex</c>: whether the addenda or the lexicon have the word (no letter-to-sound).</summary>
    public bool InLex(ReadOnlySpan<byte> word, char pos = '0')
    {
        byte[] wp = WordPosition(word, pos);
        for (int i = 0; i < addendaKeys.Length; i++)
        {
            byte[] key = addendaKeys[i];
            if ((wp[0] == (byte)'0' || wp[0] == key[0]) && Equal(wp.AsSpan(1), key.AsSpan(1)))
            {
                return true;
            }
        }

        return Bsearch(wp) >= 0;
    }

    /// <summary><c>in_lex(lex, word, NULL, NULL)</c> for the text analysis: the word is taken as UTF-8.</summary>
    public bool InLex(string word) => InLex(Encoding.UTF8.GetBytes(word));

    /// <summary>The words of all entries, each with its part-of-speech letter first, in the order of the data (a test and sweep helper: <c>lex_uncompress_word</c> at every byte
    /// after a 255).</summary>
    public IEnumerable<(int Index, byte[] WordPosition)> Entries()
    {
        byte[] buffer = new byte[WordPositionSize];
        for (int p = 1; p < data.NumBytes; p++)
        {
            if (bytes[p - 1] != EntryEnd)
            {
                continue;
            }

            int length = Uncompress(buffer, p);
            yield return (p, buffer.AsSpan(0, length).ToArray());
        }
    }

    /// <summary>The phones stored for the entry that starts at this index (the walk of <c>lex_lookup</c> backwards from the word).</summary>
    public string[] PhonesAt(int index)
    {
        // C: for (p = index - 2; data[p]; p--) for each char of the table string of the code: cons the phone; then val_reverse
        List<string> visited = [];
        for (int p = index - 2; bytes[p] != 0; p--)
        {
            byte[] chars = data.PhoneHuffman[bytes[p]] ?? [];
            foreach (byte c in chars)
            {
                visited.Add(data.PhoneTable[c]);
            }
        }

        return [.. visited];
    }

    private static byte[] WordPosition(ReadOnlySpan<byte> word, char pos)
    {
        ReadOnlySpan<byte> text = CString(word);
        byte[] wp = new byte[text.Length + 1];
        wp[0] = (byte)pos;
        text.CopyTo(wp.AsSpan(1));
        return wp;
    }

    private static ReadOnlySpan<byte> CString(ReadOnlySpan<byte> text)
    {
        int nul = text.IndexOf((byte)0);
        return nul < 0 ? text : text[..nul];
    }

    /// <summary>Whether two byte strings are equal, with a NUL ending a string as in C.</summary>
    private static bool Equal(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => Compare(a, b) == 0;

    /// <summary><c>strcmp</c> on bytes (unsigned); a NUL ends a string.</summary>
    private static int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        a = CString(a);
        b = CString(b);
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i])
            {
                return a[i] - b[i];
            }
        }

        return a.Length - b.Length;
    }

    /// <summary><c>lex_lookup_addenda</c>: the first addenda entry whose key matches (part of speech '0' on either side matches any).</summary>
    private bool FindInAddenda(byte[] wp, out string[] phones)
    {
        for (int i = 0; i < addendaKeys.Length; i++)
        {
            byte[] key = addendaKeys[i];
            if ((wp[0] == (byte)'0' || wp[0] == key[0] || key[0] == (byte)'0') && Equal(wp.AsSpan(1), key.AsSpan(1)))
            {
                phones = addendaPhones[i];
                return true;
            }
        }

        phones = [];
        return false;
    }

    /// <summary><c>lex_uncompress_word</c>: the word at byte p written to the buffer (cut so that it fits <see cref="WordPositionSize"/>); returns the length.</summary>
    private int Uncompress(byte[] buffer, int p)
    {
        int j = 0;
        for (int i = 0; bytes[p + i] != 0; i++)
        {
            byte[] piece = data.EntryHuffman[bytes[p + i]] ?? [];
            if (j + piece.Length + 1 < buffer.Length)
            {
                piece.CopyTo(buffer, j);
                j += piece.Length;
            }
            else
            {
                break;
            }
        }

        buffer[j] = 0;
        return j;
    }

    private int NextEntry(int p, int end)
    {
        for (p++; p < end; p++)
        {
            if (bytes[p - 1] == EntryEnd)
            {
                return p;
            }
        }

        return end;
    }

    private int PreviousEntry(int p, int start)
    {
        for (p--; p > start; p--)
        {
            if (bytes[p - 1] == EntryEnd)
            {
                return p;
            }
        }

        return start;
    }

    private int ClosestEntry(int p, int start, int end)
    {
        int d = 0;
        while (p - d > start && p + d < end)
        {
            if (bytes[p + d - 1] == EntryEnd)
            {
                return p + d;
            }

            if (bytes[p - d - 1] == EntryEnd)
            {
                return p - d;
            }

            d++;
        }

        return p - d;
    }

    /// <summary><c>lex_lookup_bsearch</c>: the index of the entry of the word, or -1.</summary>
    private int Bsearch(byte[] word)
    {
        int start = 0;
        int end = data.NumBytes;
        byte[] wordPosition = new byte[WordPositionSize];
        while (start < end)
        {
            int mid = (start + end) / 2;
            mid = ClosestEntry(mid, start, end);
            Uncompress(wordPosition, mid);
            int c = Compare(wordPosition.AsSpan(1), word.AsSpan(1));
            if (c == 0)
            {
                return FindFullMatch(mid, word);
            }

            if (c > 0)
            {
                end = mid;
            }
            else
            {
                start = NextEntry(mid + 1, end);
            }
        }

        return -1;
    }

    /// <summary><c>find_full_match</c>: from a hit, the entry with the same part of speech; else the earliest of the entries with that word.</summary>
    private int FindFullMatch(int i, byte[] word)
    {
        int match = i;
        byte[] wordPosition = new byte[WordPositionSize];
        for (int w = i; w > 0;)
        {
            Uncompress(wordPosition, w);
            if (!Equal(word.AsSpan(1), wordPosition.AsSpan(1)))
            {
                break;
            }

            if (Equal(word, wordPosition))
            {
                return w;
            }

            match = w;
            w = PreviousEntry(w, 0);
        }

        for (int w = i; w < data.NumBytes;)
        {
            Uncompress(wordPosition, w);
            if (!Equal(word.AsSpan(1), wordPosition.AsSpan(1)))
            {
                break;
            }

            if (Equal(word, wordPosition))
            {
                return w;
            }

            w = NextEntry(w, data.NumBytes);
        }

        return match;
    }
}
