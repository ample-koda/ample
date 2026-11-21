using Ample.Speech.Flite.Core;
using Ample.Speech.Flite.Data;

namespace Ample.Speech.Flite.Text;

// Port of Flite (CMU), lang/usenglish/us_text.c (us_tokentowords and the helpers). Translated to C# and modified:
// the C string functions work on byte strings (see ByteText), the lists of words are List<ExpWord>, the lexicon is asked through ILexicon,
// and the features of the SSML interface (phones, ssml_alias, ssml_comment) are not read because nothing in this port sets them. The branches keep the order of the C code.
//
// Flite is Copyright (c) 1999-2017 Language Technologies Institute, Carnegie Mellon University. See LICENSES/flite.txt.

/// <summary>The part of the lexicon the text analysis asks: whether a word is in it (<c>in_lex</c>).</summary>
internal interface ILexicon
{
    /// <summary><c>in_lex(lex, word, NULL, NULL)</c>: whether the word (as written, a byte string) is in the addenda or the dictionary.</summary>
    /// <param name="word">The word.</param>
    /// <returns>True when it is there.</returns>
    bool InLex(string word);

    /// <summary>Whether the block asks to read this word (lower case) letter by letter; the lexicon of the voice never does.</summary>
    /// <param name="name">The word.</param>
    /// <returns>True when the word is spelled.</returns>
    bool Spells(string name) => false;

    /// <summary>Whether the block gives this word (lower case) its own phones. C asks only the addenda of the voice when it decides whether a word can be said (<c>in_lex</c>), so a list of
    /// letters that its rules cannot pronounce ("gpu", "usb") is read letter by letter and never reaches a run-time entry; here such a word of the block is said with its phones.</summary>
    /// <param name="name">The word.</param>
    /// <returns>True when the block has an entry for it.</returns>
    bool Has(string name) => false;
}

/// <summary>US English text analysis: the words that read a token (<c>us_tokentowords</c>).</summary>
/// <param name="data">The front-end data (the word list of the machines).</param>
/// <param name="lexicon">The lexicon, for the question whether a word can be pronounced from its spelling.</param>
/// <param name="numsCart">The tree that decides how a string of digits is read (<c>us_nums_cart</c>).</param>
internal sealed class UsText(FliteEnglishData data, ILexicon lexicon, Cart numsCart)
{
    private static readonly (string Abbrev, string Word)[] WeightsAndMeasuresWords =
    [
        ("LB", "pounds"), ("LBS", "pounds"), ("lb", "pounds"), ("lbs", "pounds"), ("ft", "feet"), ("FT", "feet"),
        ("kg", "kilograms"), ("km", "kilometers"), ("cm", "centimeters"), ("mm", "millimeters"), ("ml", "milliliters"),
        ("oz", "ounces"), ("hz", "hertz"), ("Hz", "hertz"), ("HZ", "hertz"), ("KHz", "kilohertz"), ("MHz", "megahertz"),
        ("GHz", "gigahertz"), ("KB", "kilobytes"), ("GB", "gigabytes"), ("MB", "megabytes"), ("TB", "terabytes"),
    ];

    private static readonly string[] EedWords =
        ["to", "can", "can't", "cannot", "cant", "could", "couldn't", "couldnt", "will", "shall"];

    private static readonly string[] KingNames =
    [
        "louis", "henry", "charles", "philip", "george", "edward", "pius", "william", "richard", "ptolemy", "john", "paul", "peter", "nicholas",
        "frederick", "james", "alfonso", "ivan", "napolean", "leo", "gregory", "catherine", "alexandria", "pierre", "elizabeth", "mary",
    ];

    private static readonly string[] KingTitles =
    [
        "king", "queen", "pope", "duke", "tsar", "emperor", "shah", "ceasar", "duchess", "tsarina", "empress", "baron", "baroness", "sultan", "count", "countess",
    ];

    private static readonly string[] SectionNames =
        ["section", "chapter", "part", "phrase", "verse", "scene", "act", "book", "volume", "chap", "war", "apollo", "trek", "fortran"];

    private static readonly string[] PossessiveEndings = ["'s", "'ll", "'ve", "'d"];

    private const string Letters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Digits = "0123456789";

    /// <summary><c>us_tokentowords</c>: the words for a token of the Token relation.</summary>
    /// <param name="token">The token item.</param>
    /// <returns>The words, in order.</returns>
    public List<ExpWord> TokenToWords(Item token)
    {
        string name = Hrg.FeatString(token, "name");

        // a word that the block asks to read letter by letter goes the way of an unpronounceable list of alphas (the letters of the voice, one word each)
        if (name.Length > 0 && Match(UsRegexes.Alpha, name) && lexicon.Spells(ByteText.Downcase(name)))
        {
            return UsExpand.Letters(name);
        }

        return One(token, name);
    }

    private static string Ffs(Item token, string path) => FeaturePath.FfeatureString(token, path);

    private static bool Match(System.Text.RegularExpressions.Regex rx, string s) => UsRegexes.Match(rx, s);

    /// <summary>The character at an index, or NUL after the end (the C string terminator).</summary>
    private static char At(string s, int i) => i < s.Length ? s[i] : '\0';

    /// <summary><c>strchr(set, c) != NULL</c>: the terminator counts as a member of the set, as in C.</summary>
    private static bool InSet(string set, char c) => c == '\0' || set.Contains(c, StringComparison.Ordinal);

    private static bool IsOneOf(string s, string[] list) => list.Contains(s, StringComparer.Ordinal);

    private static List<ExpWord> AddBreak(List<ExpWord> l)
    {
        // add feature (break 1) to the last item of this list
        if (l.Count > 0)
        {
            l[^1].Break = true;
        }

        return l;
    }

    private static bool RexLike(Item t)
    {
        // returns true if this is in a king like context
        string pn = ByteText.Downcase(Ffs(t, "p.name"));
        string ppn = ByteText.Downcase(Ffs(t, "p.p.name"));
        return IsOneOf(pn, KingNames) || IsOneOf(ppn, KingTitles);
    }

    private static bool SectionLike(Item t) => IsOneOf(ByteText.Downcase(Ffs(t, "p.name")), SectionNames);

    private static bool TextSplitable(string s, int i)
    {
        // should the token be split after this
        if (InSet(Letters, At(s, i)) && InSet(Letters, At(s, i + 1)))
        {
            return false;
        }

        if (InSet(Digits, At(s, i)) && InSet(Digits, At(s, i + 1)))
        {
            return false;
        }

        return true;
    }

    /// <summary>The deepest nesting of <c>us_tokentowords_one</c> that the port accepts. A token splits into smaller tokens that are expanded the same way (one split per hyphen, slash or change
    /// of kind of character), so a token of thousands of parts nests thousands of calls deep: the C program with its 8 MiB stack gets there before it crashes, a .NET thread with 1 MiB does not,
    /// and a stack overflow cannot be caught. No text that is spoken has a token nested more than a few dozen levels deep.</summary>
    internal const int MaxNesting = 300;

    private int nesting;

    private List<ExpWord> One(Item token, string name)
    {
        if (++nesting > MaxNesting)
        {
            nesting = 0;
            throw new FliteException($"us_tokentowords: the token is split more than {MaxNesting} levels deep (the C code would run out of stack)");
        }

        try
        {
            return OneCore(token, name);
        }
        finally
        {
            if (nesting > 0)
            {
                nesting--;
            }
        }
    }

    private List<ExpWord> OneCore(Item token, string name)
    {
        // Return the list of words that expand token/name
        string nsw = Hrg.FeatPresent(token, "nsw") ? Hrg.FeatString(token, "nsw") : string.Empty;
        List<ExpWord> r;

        if ((name == "a" || name == "A") && (Hrg.Next(token) is null || name != Hrg.Name(token) || Ffs(token, "punc") != string.Empty))
        {
            // if A is a sub part of a token, then it is "ey" not "ah"
            r = WordLists.Of("_a");
        }
        else if (name.Length == 0)
        {
            r = [];
        }
        else if (Match(UsRegexes.DottedAbbrevs, name))
        {
            // X.X.X
            r = UsExpand.Letters(name.Replace(".", string.Empty, StringComparison.Ordinal));
        }
        else if (Match(UsRegexes.CommaInt, name))
        {
            // 99,999,999
            r = UsExpand.Real(name.Replace(",", string.Empty, StringComparison.Ordinal));
        }
        else if (Match(UsRegexes.SevenPhoneNumber, name))
        {
            // 234-3434 telephone numbers
            int p = name.IndexOf('-', StringComparison.Ordinal);
            r = WordLists.Append(AddBreak(UsExpand.Digits(name[..p])), UsExpand.Digits(name[(p + 1)..]));
        }
        else if (IsPartOfPhoneNumber(token, name))
        {
            // part of a telephone number
            if (Ffs(token, "punc") == string.Empty)
            {
                Hrg.SetString(token, "punc", ",");
            }

            r = AddBreak(UsExpand.Digits(name));
        }
        else if (Match(UsRegexes.NumberTime, name))
        {
            int p = name.IndexOf(':', StringComparison.Ordinal);
            string bbb = name[(p + 1)..];
            r = UsExpand.Number(name[..p]);
            if (bbb != "00")
            {
                r = WordLists.Append(r, UsExpand.Id(bbb));
            }
        }
        else if (Match(UsRegexes.NumberTimeXm, name))
        {
            int p = name.IndexOf(':', StringComparison.Ordinal);
            if (p < 0)
            {
                p = name.IndexOf('.', StringComparison.Ordinal);
            }

            string bbb = name.Substring(p + 1, 2);
            string ccc = name[(p + 3)..];
            r = UsExpand.Number(name[..p]);
            if (bbb != "00")
            {
                r = WordLists.Append(r, UsExpand.Id(bbb));
            }

            r = WordLists.Append(r, UsExpand.Letters(ccc));
        }
        else if (Match(UsRegexes.Digits2Dash, name))
        {
            r = DigitsWithDashes(token, name);
        }
        else if (Match(UsRegexes.LeadingZeroDigits, name))
        {
            // a leading zero and digits
            r = UsExpand.Digits(name);
        }
        else if (Match(UsRegexes.Digits, name))
        {
            r = DigitString(token, name, nsw);
        }
        else if (Match(UsRegexes.RomanNums, name))
        {
            // roman numerals
            if (Ffs(token, "p.punc") == string.Empty)
            {
                // no preceding punctuation
                string n = UsExpand.Roman(name).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (RexLike(token))
                {
                    r = WordLists.Cons("the", UsExpand.Ordinal(n));
                }
                else if (SectionLike(token))
                {
                    r = UsExpand.Number(n);
                }
                else
                {
                    r = UsExpand.Letters(name);
                }
            }
            else
            {
                r = UsExpand.Letters(name);
            }
        }
        else if (Match(UsRegexes.DrSt, name))
        {
            r = DoctorOrStreet(token, name);
        }
        else if (name == "Mr")
        {
            Hrg.SetString(token, "punc", string.Empty);
            r = WordLists.Of("mister");
        }
        else if (name == "Mrs")
        {
            Hrg.SetString(token, "punc", string.Empty);
            r = WordLists.Of("missus");
        }
        else if (name == "read" || name == "lead")
        {
            // checking WSJ examples, this seems a quick and easy way to get many of these correct
            string pname = Ffs(token, "p.name");
            if (IsOneOf(pname, EedWords))
            {
                r = WordLists.Of(name[0] == 'r' ? "reed" : "leed");
            }
            else
            {
                r = WordLists.Of(name[0] == 'r' ? "red" : "led");
            }
        }
        else if (name == "am" || name == "AM")
        {
            if (name != Hrg.Name(token))
            {
                r = UsExpand.Letters(name);
            }
            else if (Hrg.Prev(token) is not null && (Match(UsRegexes.NumberTime, Ffs(token, "p.name")) || Match(UsRegexes.Digits, Ffs(token, "p.name"))))
            {
                r = UsExpand.Letters(name);
            }
            else
            {
                r = WordLists.Of(name);
            }
        }
        else if (name.Length == 1 && name[0] is >= 'A' and <= 'Z' && Ffs(token, "n.whitespace") == " "
                 && At(Ffs(token, "n.name"), 0) is >= 'A' and <= 'Z')
        {
            Hrg.SetString(token, "punc", string.Empty);
            string aaa = ByteText.Downcase(name);
            r = WordLists.Of(aaa == "a" ? "_a" : aaa);
        }
        else if (Match(UsRegexes.Double, name))
        {
            // real numbers
            r = UsExpand.Real(name);
        }
        else if (Match(UsRegexes.OrdinalNumber, name))
        {
            // explicit ordinals
            r = UsExpand.Ordinal(name[..^2]);
        }
        else if (Match(UsRegexes.Illion, name) && Match(UsRegexes.UsMoney, Ffs(token, "p.name")))
        {
            r = WordLists.Of(name, "dollars");
        }
        else if (Match(UsRegexes.UsMoney, name))
        {
            r = UsMoney(token, name);
        }
        else if (name[^1] == '%')
        {
            r = WordLists.Append(One(token, name[..^1]), WordLists.Of("per", "cent"));
        }
        else if (Match(UsRegexes.NumEss, name))
        {
            // 60s and 7s and 9s
            r = WordLists.Append(One(token, name[..^1]), WordLists.Of("'s"));
        }
        else if (name.Contains("â\u0080\u0099", StringComparison.Ordinal))
        {
            // a single quote is sometimes rendered as the unicode character U+2019 (the bytes E2 80 99) so it is mapped back to an ascii single quote
            return One(token, name.Replace("â\u0080\u0099", "'", StringComparison.Ordinal));
        }
        else if (name.LastIndexOf('\'') is var q and >= 0)
        {
            r = Apostrophe(token, name, q);
        }
        else if (Match(UsRegexes.DigitsSlashDigits, name) && name == Hrg.Name(token))
        {
            r = Fraction(token, name);
        }
        else if (name.IndexOf('-', StringComparison.Ordinal) is var dash and >= 0)
        {
            // aaa-bbb
            string aaa = name[..dash], bbb = name[(dash + 1)..];
            if (Match(UsRegexes.Digits, aaa) && Match(UsRegexes.Digits, bbb))
            {
                string ccc = name;
                Hrg.SetString(token, "name", bbb);
                List<ExpWord> second = One(token, bbb);
                Hrg.SetString(token, "name", aaa);
                r = WordLists.Append(One(token, aaa), WordLists.Cons("to", second));
                Hrg.SetString(token, "name", ccc);
            }
            else
            {
                r = WordLists.Append(One(token, aaa), One(token, bbb));
            }
        }
        else if (Match(UsRegexes.WeightsAndMeasures, name))
        {
            r = WeightsAndMeasures(token, name);
        }
        else if (name.Length > 1 && !Match(UsRegexes.Alpha, name))
        {
            // it's not just alphas
            int i = 0;
            while (i < name.Length && !TextSplitable(name, i))
            {
                i++;
            }

            if (i >= name.Length)
            {
                throw new FliteException("us_tokentowords: no place to split the token (the C code writes outside its buffer)");
            }

            string aaa = name[..(i + 1)], bbb = name[(i + 1)..];
            Hrg.SetString(token, "nsw", "nide");
            r = WordLists.Append(One(token, aaa), One(token, bbb));
        }
        else if (StateName(name, token) is { } state)
        {
            r = state;
        }
        else if (name.Length > 1 && Match(UsRegexes.Alpha, name) && !lexicon.InLex(name) && !UsAswd.IsWord(name, data) && !lexicon.Has(ByteText.Downcase(name)))
        {
            // unpronounceable list of alphas
            r = UsExpand.Letters(name);
        }
        else
        {
            // just a word
            r = WordLists.Of(ByteText.Downcase(name));
        }

        return r;
    }

    private static bool IsPartOfPhoneNumber(Item token, string name)
    {
        bool threeDigitsInPhone = Match(UsRegexes.ThreeDigits, name) &&
            ((!Match(UsRegexes.Digits, Ffs(token, "p.name")) && Match(UsRegexes.ThreeDigits, Ffs(token, "n.name")) && Match(UsRegexes.FourDigits, Ffs(token, "n.n.name"))) ||
             Match(UsRegexes.SevenPhoneNumber, Ffs(token, "n.name")) ||
             (!Match(UsRegexes.Digits, Ffs(token, "p.p.name")) && Match(UsRegexes.ThreeDigits, Ffs(token, "p.name")) && Match(UsRegexes.FourDigits, Ffs(token, "n.name"))));
        if (threeDigitsInPhone)
        {
            return true;
        }

        return Match(UsRegexes.FourDigits, name) &&
            !Match(UsRegexes.Digits, Ffs(token, "n.name")) && Match(UsRegexes.ThreeDigits, Ffs(token, "p.name")) && Match(UsRegexes.ThreeDigits, Ffs(token, "p.p.name"));
    }

    private List<ExpWord> DigitsWithDashes(Item token, string name)
    {
        // 999-999-999 etc: the parts, in reverse order as the C code builds them
        List<string> ss = [];
        string rest = name;
        int pos = 0;
        int start = 0;
        while (pos < rest.Length)
        {
            if (rest[pos] == '-')
            {
                ss.Insert(0, rest[start..pos]);
                while (pos < rest.Length && rest[pos] == '-')
                {
                    pos++;
                }

                start = pos;
            }
            else
            {
                pos++;
            }
        }

        ss.Insert(0, rest[start..]);   // note the order is now reversed
        if (ss.Count == 2 && ByteText.Atoi(ss[1]) < ByteText.Atoi(ss[0]) && Math.Abs(ss[1].Length - ss[0].Length) < 2)
        {
            // should get 22-23 November, or 1998-1999 right: small to large, not too different in length
            return WordLists.Append(One(token, ss[1]), WordLists.Cons("to", One(token, ss[0])));
        }

        // it's just a bunch of ids
        List<ExpWord> r = [];
        foreach (string part in ss)
        {
            r = WordLists.Append(AddBreak(UsExpand.Digits(part)), r);
        }

        return r;
    }

    private List<ExpWord> DigitString(Item token, string name, string nsw)
    {
        // string of digits (use the tree to disambiguate)
        if (nsw == "nide")
        {
            return UsExpand.Id(name);
        }

        string rname = Hrg.FeatString(token, "name");
        Value? tv;
        if (name == rname)
        {
            tv = numsCart.Interpret(token);
        }
        else
        {
            // in a recursive call
            Hrg.SetString(token, "name", name);
            tv = numsCart.Interpret(token);
            Hrg.SetString(token, "name", rname);
        }

        string ts = Values.String(tv);
        return ts switch
        {
            "ordinal" => UsExpand.Ordinal(name),
            "digits" => UsExpand.Digits(name),
            "year" => UsExpand.Id(name),
            _ => UsExpand.Number(name),
        };
    }

    private static List<ExpWord> DoctorOrStreet(Item token, string name)
    {
        // St Andrew's St, Dr King Dr
        string street, saint;
        if (name[0] is 's' or 'S')
        {
            street = "street";
            saint = "saint";
        }
        else
        {
            street = "drive";
            saint = "doctor";
        }

        List<ExpWord> r;
        if (Hrg.Next(token) is null || Hrg.FeatString(token, "punc").Contains(',', StringComparison.Ordinal))
        {
            r = WordLists.Of(street);
        }
        else if (Ffs(token, "punc").Contains(',', StringComparison.Ordinal))
        {
            r = WordLists.Of(saint);
        }
        else
        {
            string pname = Ffs(token, "p.name");
            string nname = Ffs(token, "n.name");
            char p0 = At(pname, 0), n0 = At(nname, 0);
            if (p0 is >= 'A' and <= 'Z' && n0 is >= 'a' and <= 'z')
            {
                r = WordLists.Of(street);
            }
            else if (p0 is >= '0' and <= '9' && n0 is >= 'a' and <= 'z')
            {
                r = WordLists.Of(street);
            }
            else if (p0 is >= 'a' and <= 'z' && n0 is >= 'A' and <= 'Z')
            {
                r = WordLists.Of(saint);
            }
            else if (Ffs(token, "n.whitespace") == " ")
            {
                r = WordLists.Of(saint);
            }
            else
            {
                r = WordLists.Of(street);
            }
        }

        if (Hrg.FeatString(token, "punc") == ".")
        {
            Hrg.SetString(token, "punc", string.Empty);
        }

        return r;
    }

    private List<ExpWord> UsMoney(Item token, string name)
    {
        // US money
        int p = name.IndexOf('.', StringComparison.Ordinal);
        if (Match(UsRegexes.Illion, Ffs(token, "n.name")))
        {
            // carl sagan's billions and billions
            return UsExpand.Real(name[1..]);
        }

        if (p < 0)
        {
            string aaa = name[1..];
            List<ExpWord> unit = WordLists.Of(aaa == "1" ? "dollar" : "dollars");
            return WordLists.Append(One(token, aaa), unit);
        }

        int fractionLength = name.Length - p;   // strlen(p) of the C code: the point and what follows
        if (fractionLength == 1 || fractionLength > 3)
        {
            // simply read as mumble point mumble
            return WordLists.Append(UsExpand.Real(name[1..]), WordLists.Of("dollars"));
        }

        string dollars = name[..p].Replace(",", string.Empty, StringComparison.Ordinal);
        string cents = name[(p + 1)..];
        List<ExpWord> r;
        if (cents == "00")
        {
            r = [];
        }
        else if (cents == "01")
        {
            r = WordLists.Append(UsExpand.Number(cents), WordLists.Of("cent"));
        }
        else
        {
            r = WordLists.Append(UsExpand.Number(cents), WordLists.Of("cents"));
        }

        r = dollars[1..] == "1" ? WordLists.Cons("dollar", r) : WordLists.Cons("dollars", r);
        return WordLists.Append(UsExpand.Number(dollars[1..]), r);
    }

    private List<ExpWord> Apostrophe(Item token, string name, int q)
    {
        string p = name[q..];   // from the last apostrophe to the end
        string bbb = ByteText.Downcase(p);
        if (IsOneOf(bbb, PossessiveEndings))
        {
            return WordLists.Append(One(token, name[..q]), WordLists.Of(bbb));
        }

        if (p == "'tve")
        {
            // admittedly rare and weird
            return WordLists.Append(One(token, name[..(q + 2)]), WordLists.Of("'ve"));
        }

        return One(token, name[..q] + p[1..]);
    }

    private static List<ExpWord> Fraction(Item token, string name)
    {
        // might be a fraction, or not
        int slash = name.IndexOf('/', StringComparison.Ordinal);
        string aaa = name[..slash], bbb = name[(slash + 1)..];
        List<ExpWord> r;
        if (aaa == "1" && bbb == "2")
        {
            r = WordLists.Of("a", "half");
        }
        else if (ByteText.Atoi(aaa) < ByteText.Atoi(bbb))
        {
            r = WordLists.Append(UsExpand.Number(aaa), UsExpand.Ordinal(bbb));
            if (ByteText.Atoi(aaa) > 1)
            {
                r = WordLists.Append(r, WordLists.Of("'s"));
            }
        }
        else
        {
            r = WordLists.Append(UsExpand.Number(aaa), WordLists.Cons("slash", UsExpand.Number(bbb)));
        }

        if (Match(UsRegexes.Digits, Ffs(token, "p.name")) && Hrg.Prev(token) is not null)
        {
            // don't mistake "0" as a number
            r = WordLists.Cons("and", r);
        }

        return r;
    }

    private List<ExpWord> WeightsAndMeasures(Item token, string name)
    {
        int j;
        for (j = name.Length - 1; j > 0; j--)
        {
            if (Digits.Contains(name[j], StringComparison.Ordinal))
            {
                break;
            }
        }

        j += 1;
        int i = Array.FindIndex(WeightsAndMeasuresWords, w => w.Abbrev == name[j..]);

        // remove any commas, in the way of the C loop (it skips one comma at a time)
        char[] aaa = [.. name[..j], '\0'];
        int k = 0, l = 0;
        while (aaa[l] != '\0')
        {
            if (aaa[l] == ',')
            {
                l++;
            }

            aaa[k] = aaa[l];
            k++;
            l++;
        }

        aaa[k] = '\0';
        string number = new(aaa, 0, k);
        return i < 0
            ? WordLists.Append(UsExpand.Number(number), One(token, name[j..]))
            : WordLists.Append(UsExpand.Number(number), WordLists.Of(WeightsAndMeasuresWords[i].Word));
    }

    private static readonly (string Name, bool Ambiguous, string[] Words)[] States = BuildStates();

    private static (string, bool, string[])[] BuildStates()
    {
        (string, string, string, string?)[] rows =
        [
            ("AL", "ambiguous", "alabama", null), ("Al", "ambiguous", "alabama", null), ("Ala", "", "alabama", null), ("AK", "", "alaska", null),
            ("Ak", "", "alaska", null), ("AZ", "", "arizona", null), ("Az", "", "arizona", null), ("CA", "", "california", null),
            ("Ca", "", "california", null), ("Cal", "ambiguous", "california", null), ("Calif", "", "california", null), ("CO", "ambiguous", "colorado", null),
            ("Co", "ambiguous", "colorado", null), ("Colo", "", "colorado", null), ("DC", "", "d", "c"), ("DE", "", "delaware", null),
            ("De", "ambiguous", "delaware", null), ("Del", "ambiguous", "delaware", null), ("FL", "", "florida", null), ("Fl", "ambiguous", "florida", null),
            ("Fla", "", "florida", null), ("GA", "", "georgia", null), ("Ga", "", "georgia", null), ("HI", "", "hawaii", null),
            ("Hi", "ambiguous", "hawaii", null), ("IA", "", "iowa", null), ("Ia", "ambiguous", "iowa", null), ("Ind", "ambiguous", "indiana", null),
            ("ID", "ambiguous", "idaho", null), ("IL", "ambiguous", "illinois", null), ("Il", "ambiguous", "illinois", null), ("ILL", "ambiguous", "illinois", null),
            ("KS", "", "kansas", null), ("Ks", "", "kansas", null), ("Kans", "", "kansas", null), ("KY", "ambiguous", "kentucky", null),
            ("Ky", "ambiguous", "kentucky", null), ("LA", "ambiguous", "louisiana", null), ("La", "ambiguous", "louisiana", null), ("Lou", "ambiguous", "louisiana", null),
            ("Lous", "ambiguous", "louisiana", null), ("MA", "ambiguous", "massachusetts", null), ("Mass", "ambiguous", "massachusetts", null),
            ("Ma", "ambiguous", "massachusetts", null), ("MD", "ambiguous", "maryland", null), ("Md", "ambiguous", "maryland", null), ("ME", "ambiguous", "maine", null),
            ("Me", "ambiguous", "maine", null), ("MI", "", "michigan", null), ("Mi", "ambiguous", "michigan", null), ("Mich", "ambiguous", "michigan", null),
            ("MN", "ambiguous", "minnestota", null), ("Minn", "ambiguous", "minnestota", null), ("MS", "ambiguous", "mississippi", null),
            ("Miss", "ambiguous", "mississippi", null), ("MT", "ambiguous", "montanna", null), ("Mt", "ambiguous", "montanna", null), ("MO", "ambiguous", "missouri", null),
            ("Mo", "ambiguous", "missouri", null), ("NC", "ambiguous", "north", "carolina"), ("ND", "ambiguous", "north", "dakota"), ("NE", "ambiguous", "nebraska", null),
            ("Ne", "ambiguous", "nebraska", null), ("Neb", "ambiguous", "nebraska", null), ("NH", "ambiguous", "new", "hampshire"), ("NV", "", "nevada", null),
            ("Nev", "", "nevada", null), ("NY", "", "new", "york"), ("OH", "ambiguous", "ohio", null), ("OK", "ambiguous", "oklahoma", null), ("Okla", "", "oklahoma", null),
            ("OR", "ambiguous", "oregon", null), ("Or", "ambiguous", "oregon", null), ("Ore", "ambiguous", "oregon", null), ("PA", "ambiguous", "pennsylvania", null),
            ("Pa", "ambiguous", "pennsylvania", null), ("Penn", "ambiguous", "pennsylvania", null), ("RI", "ambiguous", "rhode", "island"),
            ("SC", "ambiguous", "south", "carlolina"), ("SD", "ambiguous", "south", "dakota"), ("TN", "ambiguous", "tennesee", null), ("Tn", "ambiguous", "tennesee", null),
            ("Tenn", "ambiguous", "tennesee", null), ("TX", "ambiguous", "texas", null), ("Tx", "ambiguous", "texas", null), ("Tex", "ambiguous", "texas", null),
            ("UT", "ambiguous", "utah", null), ("VA", "ambiguous", "virginia", null), ("WA", "ambiguous", "washington", null), ("Wa", "ambiguous", "washington", null),
            ("Wash", "ambiguous", "washington", null), ("WI", "ambiguous", "wisconsin", null), ("Wi", "ambiguous", "wisconsin", null), ("WV", "ambiguous", "west", "virginia"),
            ("WY", "ambiguous", "wyoming", null), ("Wy", "ambiguous", "wyoming", null), ("Wyo", "", "wyoming", null), ("PR", "ambiguous", "puerto", "rico"),
        ];

        return [.. rows.Select(r => (r.Item1, r.Item2 == "ambiguous", r.Item4 is null ? new[] { r.Item3 } : [r.Item3, r.Item4]))];
    }

    private static List<ExpWord>? StateName(string name, Item t)
    {
        foreach ((string stateName, bool ambiguous, string[] words) in States)
        {
            if (stateName != name)
            {
                continue;
            }

            bool doIt = true;
            if (ambiguous)
            {
                string pname = Ffs(t, "p.name");
                string nname = Ffs(t, "n.name");

                // the previous name is capitalized
                doIt = InSet("ABCDEFGHIJKLMNOPQRSTUVWXYZ", At(pname, 0)) && pname.Length > 2 && Match(UsRegexes.Alpha, pname) &&
                    (InSet("abcdefghijklmnopqrstuvwxyz", At(nname, 0)) || Hrg.Next(t) is null || Hrg.FeatString(t, "punc") == "." ||
                     ((nname.Length == 5 || nname.Length == 10) && Match(UsRegexes.Digits, nname)));
            }

            if (doIt)
            {
                return WordLists.Of(words);
            }
        }

        return null;
    }
}
