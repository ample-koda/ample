using System.Globalization;
using System.Text;
using Ample.Speech.Flite.Lexicon;

namespace Ample.Speech.Flite.Tests.Lexicon;

/// <summary>
/// The lexicon and the letter-to-sound rules against the pinned C program (plan/speech/flite, milestone M4): every entry of the lexicon and 100,000 pseudo random strings give the same phones.
/// The C answers are not stored: <c>reference/m4/dumplex.c</c> writes them and prints the FNV-1a (64 bit) digest of the file; the digest and the line count are in <c>Fixtures/lexicon-digests.txt</c>.
/// With the environment variable <c>FLITE_REF_DIR</c> set to a folder with the files <c>lex.txt</c> and <c>lts.txt</c> of the dumper, the first differing line is named.
/// </summary>
public class FliteLexiconSweepTests
{
    private const int RandomStrings = 100_000;

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static (string Digest, int Lines) Expected(string kind)
    {
        foreach (string line in File.ReadAllLines(Fixture("lexicon-digests.txt")))
        {
            string[] f = line.Split(' ');
            if (f[0] == kind)
            {
                return (f[1], int.Parse(f[2], CultureInfo.InvariantCulture));
            }
        }

        throw new InvalidOperationException(kind);
    }

    [Fact]
    public void Every_entry_of_the_lexicon_gives_the_phones_of_the_c_program()
    {
        FliteLexicon lexicon = FliteLexicon.Cmu;
        List<byte[]> lines = [];
        foreach ((int _, byte[] wp) in lexicon.Entries())
        {
            string[] phones = lexicon.Lookup(wp.AsSpan(1), (char)wp[0]);
            lines.Add(Line(wp, string.Join(' ', phones), null));
        }

        Assert.Equal(lexicon.NumEntries, lines.Count);
        AssertSameAsC("lex", lines);
    }

    [Fact]
    public void A_hundred_thousand_random_strings_give_the_letter_to_sound_and_lexicon_answers_of_the_c_program()
    {
        FliteLexicon lexicon = FliteLexicon.Cmu;
        List<byte[]> lines = [];
        ulong state = 20261120;
        ulong Next()
        {
            state = ((state * 1103515245UL) + 12345UL) & 0x7fffffffUL;
            return state >> 8;
        }

        byte[][] specials = [[(byte)'\''], [(byte)'-'], [(byte)'#'], [(byte)'.'], [(byte)'0'], [(byte)' '], [(byte)'9'], [0xc3, 0xa9], [(byte)'A'], [(byte)'Z']];
        for (int i = 0; i < RandomStrings; i++)
        {
            List<byte> word = [];
            int length = 1 + (int)(Next() % 12);
            for (int k = 0; k < length; k++)
            {
                ulong v = Next() % 100;
                if (v < 92)
                {
                    word.Add((byte)('a' + (int)(Next() % 26)));
                }
                else
                {
                    word.AddRange(specials[Next() % 10]);
                }
            }

            byte[] w = [.. word];
            string viaRules = string.Join(' ', FliteLts.Apply(w));
            string viaLexicon = string.Join(' ', lexicon.Lookup(w));
            lines.Add(Line(w, viaRules, viaLexicon));
        }

        AssertSameAsC("lts", lines);
    }

    private static byte[] Line(byte[] word, string first, string? second)
    {
        using MemoryStream stream = new();
        stream.Write(word);
        stream.WriteByte((byte)'\t');
        stream.Write(Encoding.ASCII.GetBytes(first));
        if (second is not null)
        {
            stream.WriteByte((byte)'\t');
            stream.Write(Encoding.ASCII.GetBytes(second));
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static void AssertSameAsC(string kind, List<byte[]> lines)
    {
        string? folder = Environment.GetEnvironmentVariable("FLITE_REF_DIR");
        if (!string.IsNullOrEmpty(folder) && File.Exists(Path.Combine(folder, kind + ".txt")))
        {
            string[] reference = File.ReadAllText(Path.Combine(folder, kind + ".txt"), Encoding.Latin1).Split('\n');
            for (int i = 0; i < lines.Count; i++)
            {
                string mine = Encoding.Latin1.GetString(lines[i]).TrimEnd('\n');
                Assert.True(mine == reference[i], $"line {i + 1}: C '{reference[i]}', C# '{mine}'");
            }
        }

        ulong hash = 1469598103934665603UL;
        foreach (byte[] line in lines)
        {
            foreach (byte b in line)
            {
                hash ^= b;
                hash *= 1099511628211UL;
            }
        }

        (string digest, int count) = Expected(kind);
        Assert.Equal(count, lines.Count);
        Assert.Equal(digest, hash.ToString("x16", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Known_words_come_from_the_addenda_the_lexicon_or_the_rules()
    {
        FliteLexicon lexicon = FliteLexicon.Cmu;
        Assert.Equal(["hh", "ax0", "l", "ow1"], lexicon.Lookup("hello"));   // the lexicon
        Assert.Equal(["z"], lexicon.Lookup("'s", 's'));                     // an addendum with a part of speech
        Assert.Empty(lexicon.Lookup(","));                                  // an addendum without phones: punctuation is not read
        Assert.Equal(["ax0"], lexicon.Lookup("a", 'd'));                    // the part of speech picks the entry
        Assert.NotEmpty(lexicon.Lookup("qwzxv"));                           // rules
        Assert.True(lexicon.InLex("hello"u8));
        Assert.False(lexicon.InLex("qwzxv"u8));
        Assert.True(lexicon.InLex("hello"));
    }

    [Fact]
    public void The_letter_to_sound_rules_skip_unknown_characters()
    {
        Assert.Equal(FliteLts.Apply("cat"u8), FliteLts.Apply("c-a.t"u8));
        Assert.Empty(FliteLts.Apply(""u8));
        Assert.Empty(FliteLts.Apply("1234"u8));
    }
}
