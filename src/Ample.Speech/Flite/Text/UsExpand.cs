using Ample.Speech.Flite.Core;

namespace Ample.Speech.Flite.Text;

// Port of Flite (CMU), lang/usenglish/us_expand.c. Translated to C# and modified: see UsText.cs.

/// <summary>One word of an expansion: its name and the feature <c>break</c> that <c>add_break</c> puts on the last word of a part.</summary>
internal sealed class ExpWord(string name)
{
    /// <summary>The name of the word.</summary>
    public string Name { get; } = name;

    /// <summary>Whether the word has the feature <c>break</c> = "1".</summary>
    public bool Break { get; set; }
}

/// <summary>The list operations the text expanders use (<c>cons_val</c>, <c>val_append</c>, <c>val_reverse</c>) on lists of <see cref="ExpWord"/>.</summary>
internal static class WordLists
{
    /// <summary>A list of the given words.</summary>
    /// <param name="names">The names.</param>
    /// <returns>The list.</returns>
    public static List<ExpWord> Of(params string[] names) => [.. names.Select(n => new ExpWord(n))];

    /// <summary><c>cons_val(string_val(name), list)</c>: the word in front of the list.</summary>
    /// <param name="name">The name.</param>
    /// <param name="tail">The rest of the list (consumed).</param>
    /// <returns>The new list.</returns>
    public static List<ExpWord> Cons(string name, List<ExpWord>? tail)
    {
        List<ExpWord> r = [new ExpWord(name)];
        if (tail is not null)
        {
            r.AddRange(tail);
        }

        return r;
    }

    /// <summary><c>val_append</c>.</summary>
    /// <param name="a">The first list.</param>
    /// <param name="b">The second list.</param>
    /// <returns>The words of both, in order.</returns>
    public static List<ExpWord> Append(List<ExpWord>? a, List<ExpWord>? b)
    {
        List<ExpWord> r = [];
        if (a is not null)
        {
            r.AddRange(a);
        }

        if (b is not null)
        {
            r.AddRange(b);
        }

        return r;
    }

    /// <summary><c>val_reverse</c>.</summary>
    /// <param name="a">The list.</param>
    /// <returns>The words in reverse order.</returns>
    public static List<ExpWord> Reverse(List<ExpWord>? a)
    {
        List<ExpWord> r = a is null ? [] : [.. a];
        r.Reverse();
        return r;
    }
}

/// <summary>The English text expanders: numbers, ordinals, ids (years), real numbers, digits, letters and roman numerals.</summary>
internal static class UsExpand
{
    private static readonly string[] Digit2Num = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];

    private static readonly string[] Digit2Teen =
        ["ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];

    private static readonly string[] Digit2Enty =
        ["zero", "ten", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    private static readonly string[] Ord2Num =
        ["zeroth", "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth"];

    private static readonly string[] Ord2Teen =
        ["tenth", "eleventh", "twelfth", "thirteenth", "fourteenth", "fifteenth", "sixteenth", "seventeenth", "eighteenth", "nineteenth"];

    private static readonly string[] Ord2Enty =
        ["zeroth", "tenth", "twentieth", "thirtieth", "fortieth", "fiftieth", "sixtieth", "seventieth", "eightieth", "ninetieth"];

    /// <summary>The C code indexes its tables with <c>c - '0'</c> and reads outside them for other characters; the port stops there.</summary>
    private static string Pick(string[] table, char c)
    {
        int i = c - '0';
        return (uint)i < (uint)table.Length ? table[i] : throw new FliteException($"en_exp: the character {(int)c} is not a digit (the C code reads outside its table)");
    }

    /// <summary><c>en_exp_number</c>: the words that read a digit string as a number.</summary>
    /// <param name="numString">The digits.</param>
    /// <returns>The words.</returns>
    public static List<ExpWord> Number(string numString)
    {
        int numDigits = numString.Length;
        if (numDigits == 0)
        {
            return [];
        }

        if (numDigits == 1)
        {
            return Digits(numString);
        }

        if (numDigits == 2)
        {
            if (numString[0] == '0')
            {
                return numString[1] == '0' ? [] : WordLists.Of(Pick(Digit2Num, numString[1]));
            }

            if (numString[1] == '0')
            {
                return WordLists.Of(Pick(Digit2Enty, numString[0]));
            }

            if (numString[0] == '1')
            {
                return WordLists.Of(Pick(Digit2Teen, numString[1]));
            }

            return WordLists.Cons(Pick(Digit2Enty, numString[0]), Digits(numString[1..]));
        }

        if (numDigits == 3)
        {
            if (numString[0] == '0')
            {
                return Number(numString[1..]);
            }

            return WordLists.Cons(Pick(Digit2Num, numString[0]), WordLists.Cons("hundred", Number(numString[1..])));
        }

        if (numDigits < 7)
        {
            return Scaled(numString, numDigits - 3, "thousand");
        }

        if (numDigits < 10)
        {
            return Scaled(numString, numDigits - 6, "million");
        }

        if (numDigits < 13)
        {
            // pedantic brits: 10^9 is a billion
            return Scaled(numString, numDigits - 9, "billion");
        }

        // way too many digits to be a number
        return Digits(numString);
    }

    private static List<ExpWord> Scaled(string numString, int split, string scale)
    {
        List<ExpWord> p = Number(numString[..split]);
        return p.Count == 0 ? Number(numString[split..]) : WordLists.Append(p, WordLists.Cons(scale, Number(numString[split..])));
    }

    /// <summary><c>en_exp_ordinal</c>: the words that read a digit string as an ordinal (commas are removed first).</summary>
    /// <param name="rawNumString">The digits, possibly with commas.</param>
    /// <returns>The words.</returns>
    public static List<ExpWord> Ordinal(string rawNumString)
    {
        string numString = rawNumString.Replace(",", string.Empty, StringComparison.Ordinal);
        List<ExpWord> card = WordLists.Reverse(Number(numString));
        if (card.Count == 0)
        {
            card = WordLists.Of("zero");
        }

        string l = card[0].Name;
        string? ord = null;
        for (int i = 0; i < 10; i++)
        {
            if (l == Digit2Num[i])
            {
                ord = Ord2Num[i];
            }
        }

        if (ord is null)
        {
            for (int i = 0; i < 10; i++)
            {
                if (l == Digit2Teen[i])
                {
                    ord = Ord2Teen[i];
                }
            }
        }

        if (ord is null)
        {
            for (int i = 0; i < 10; i++)
            {
                if (l == Digit2Enty[i])
                {
                    ord = Ord2Enty[i];
                }
            }
        }

        if (l == "hundred")
        {
            ord = "hundredth";
        }

        if (l == "thousand")
        {
            ord = "thousandth";
        }

        if (l == "billion")
        {
            ord = "billtionth";   // sic, as in Flite
        }

        if (ord is null)
        {
            return card;   // dunno, so don't convert anything (and the list stays reversed, as in C)
        }

        List<ExpWord> o = [new ExpWord(ord)];
        for (int t = 1; t < card.Count; t++)
        {
            o.Insert(0, card[t]);
        }

        return o;
    }

    /// <summary><c>en_exp_id</c>: the words that read a digit string in pairs, as years and ids.</summary>
    /// <param name="numString">The digits.</param>
    /// <returns>The words.</returns>
    public static List<ExpWord> Id(string numString)
    {
        if (numString.Length == 4 && numString[2] == '0' && numString[3] == '0')
        {
            if (numString[1] == '0')
            {
                return Number(numString);   // 2000, 3000
            }

            return WordLists.Append(Number(numString[..2]), WordLists.Of("hundred"));
        }

        if (numString.Length == 3 && numString[0] != '0' && numString[1] == '0' && numString[2] == '0')
        {
            return WordLists.Cons(Pick(Digit2Num, numString[0]), WordLists.Of("hundred"));
        }

        if (numString.Length == 2 && numString[0] == '0' && numString[1] == '0')
        {
            return WordLists.Of("zero", "zero");
        }

        if (numString.Length == 2 && numString[0] == '0')
        {
            return WordLists.Cons("oh", Digits(numString[1..]));
        }

        if ((numString.Length == 4 && numString[1] == '0' && numString[2] == '0') || numString.Length < 3)
        {
            return Number(numString);
        }

        if (numString.Length % 2 == 1)
        {
            return WordLists.Cons(Pick(Digit2Num, numString[0]), Id(numString[1..]));
        }

        return WordLists.Append(Number(numString[..2]), Id(numString[2..]));
    }

    /// <summary><c>en_exp_real</c>: the words that read a real number (sign, fraction, exponent).</summary>
    /// <param name="numString">The number.</param>
    /// <returns>The words.</returns>
    public static List<ExpWord> Real(string numString)
    {
        if (numString.Length > 0 && numString[0] == '-')
        {
            return WordLists.Cons("minus", Real(numString[1..]));
        }

        if (numString.Length > 0 && numString[0] == '+')
        {
            return WordLists.Cons("plus", Real(numString[1..]));
        }

        int p = numString.IndexOf('e');
        if (p < 0)
        {
            p = numString.IndexOf('E');
        }

        if (p >= 0)
        {
            return WordLists.Append(Real(numString[..p]), WordLists.Cons("e", Real(numString[(p + 1)..])));
        }

        p = numString.IndexOf('.');
        if (p >= 0)
        {
            return WordLists.Append(Number(numString[..p]), WordLists.Cons("point", Digits(numString[(p + 1)..])));
        }

        return Number(numString);   // I don't think you can get here
    }

    /// <summary><c>en_exp_digits</c>: the words that read a string digit by digit (another character is "umpty").</summary>
    /// <param name="numString">The string.</param>
    /// <returns>The words.</returns>
    public static List<ExpWord> Digits(string numString)
    {
        List<ExpWord> d = [];
        foreach (char c in numString)
        {
            d.Add(new ExpWord(c is >= '0' and <= '9' ? Digit2Num[c - '0'] : "umpty"));
        }

        return d;
    }

    /// <summary><c>en_exp_letters</c>: the letters of a string as single words (upper case becomes lower case, digits are read, "a" is "_a").</summary>
    /// <param name="lets">The string.</param>
    /// <returns>The words.</returns>
    public static List<ExpWord> Letters(string lets)
    {
        List<ExpWord> r = [];
        foreach (char original in lets)
        {
            char c = original is >= 'A' and <= 'Z' ? (char)(original + ('a' - 'A')) : original;
            if (c is >= '0' and <= '9')
            {
                r.Add(new ExpWord(Digit2Num[c - '0']));
            }
            else if (c == 'a')
            {
                r.Add(new ExpWord("_a"));
            }
            else
            {
                r.Add(new ExpWord(c.ToString()));
            }
        }

        return r;
    }

    /// <summary><c>en_exp_roman</c>: the value of a roman numeral (X, V and I only).</summary>
    /// <param name="roman">The numeral.</param>
    /// <returns>The value.</returns>
    public static int Roman(string roman)
    {
        int val = 0;
        for (int p = 0; p < roman.Length; p++)
        {
            char c = roman[p];
            if (c == 'X')
            {
                val += 10;
            }
            else if (c == 'V')
            {
                val += 5;
            }
            else if (c == 'I')
            {
                char next = p + 1 < roman.Length ? roman[p + 1] : '\0';
                if (next == 'V')
                {
                    val += 4;
                    p++;
                }
                else if (next == 'X')
                {
                    val += 9;
                    p++;
                }
                else
                {
                    val += 1;
                }
            }
        }

        return val;
    }
}
