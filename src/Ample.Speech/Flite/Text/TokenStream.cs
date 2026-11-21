namespace Ample.Speech.Flite.Text;

// Port of Flite (CMU), src/utils/cst_tokenstream.c (the string stream only). Translated to C# and modified: see UsText.cs.

/// <summary>One token as the stream gives it: the text and what surrounds it.</summary>
/// <param name="Name">The token without its punctuation (may be empty at the end of the text).</param>
/// <param name="Whitespace">The white space before the token.</param>
/// <param name="Prepunctuation">The punctuation before the token.</param>
/// <param name="Postpunctuation">The punctuation after the token.</param>
/// <param name="FilePos">The position of the stream after the token (<c>ts-&gt;file_pos</c>).</param>
/// <param name="LineNumber">The number of line feeds read so far.</param>
internal readonly record struct TokenRead(string Name, string Whitespace, string Prepunctuation, string Postpunctuation, int FilePos, int LineNumber);

/// <summary>
/// The Flite token stream over a string (<c>ts_open_string</c>, <c>ts_get</c>, <c>ts_eof</c>), with the character classes of the US English voice.
/// The string is a byte string (<see cref="ByteText"/>).
/// </summary>
internal sealed class TokenStream
{
    private const int White = 1;
    private const int SingleChar = 2;
    private const int PrePunct = 4;
    private const int PostPunct = 8;

    /// <summary><c>us_english_whitespace</c>.</summary>
    public const string UsWhitespace = " \t\n\r";

    /// <summary><c>us_english_singlecharsymbols</c> (none).</summary>
    public const string UsSingleCharSymbols = "";

    /// <summary><c>us_english_prepunctuation</c>.</summary>
    public const string UsPrepunctuation = "\"'`({[";

    /// <summary><c>us_english_punctuation</c> (the post punctuation).</summary>
    public const string UsPostpunctuation = "\"'`.,:;!?(){}[]";

    private readonly string text;
    private readonly byte[] charClass = new byte[256];
    private int current;
    private int filePos;
    private int lineNumber;
    private bool eof;

    /// <summary>Opens a stream over a byte string with the classes of the US English voice.</summary>
    /// <param name="text">The byte string.</param>
    public TokenStream(string text)
        : this(text, UsWhitespace, UsSingleCharSymbols, UsPrepunctuation, UsPostpunctuation)
    {
    }

    /// <summary>Opens a stream over a byte string with the given classes (<c>set_charclasses</c>).</summary>
    /// <param name="text">The byte string.</param>
    /// <param name="whitespace">The white space characters.</param>
    /// <param name="singleChars">The characters that are tokens of their own.</param>
    /// <param name="prepunctuation">The characters that start a token and are not part of it.</param>
    /// <param name="postpunctuation">The characters that end a token and are not part of it.</param>
    public TokenStream(string text, string whitespace, string singleChars, string prepunctuation, string postpunctuation)
    {
        this.text = text;
        foreach (char c in whitespace)
        {
            charClass[(byte)c] |= White;
        }

        foreach (char c in singleChars)
        {
            charClass[(byte)c] |= SingleChar;
        }

        foreach (char c in prepunctuation)
        {
            charClass[(byte)c] |= PrePunct;
        }

        foreach (char c in postpunctuation)
        {
            charClass[(byte)c] |= PostPunct;
        }

        postpunctuationIsSet = postpunctuation.Length > 0;
        Getc();
    }

    private readonly bool postpunctuationIsSet;

    /// <summary><c>ts_eof</c>.</summary>
    public bool Eof => eof;

    private bool IsClass(int c, int cls) => (charClass[(byte)c] & cls) != 0;

    private void Getc()
    {
        // internal_ts_getc for a string buffer
        if (filePos >= text.Length)
        {
            eof = true;
            current = 0;
        }
        else
        {
            current = text[filePos];
        }

        if (!eof)
        {
            filePos++;
        }

        if (current == '\n')
        {
            lineNumber++;
        }
    }

    private string SubPart(int cls)
    {
        // get_token_sub_part: while the characters are of the class and not single characters
        List<char> buffer = [];
        while (!eof && IsClass(current, cls) && !IsClass(current, SingleChar))
        {
            buffer.Add((char)current);
            Getc();
        }

        return new string([.. buffer]);
    }

    private string TokenCore()
    {
        // get_token_sub_part_2: until white space (the end class) or a single character
        List<char> buffer = [];
        while (!eof && !IsClass(current, White) && !IsClass(current, SingleChar))
        {
            buffer.Add((char)current);
            Getc();
        }

        return new string([.. buffer]);
    }

    /// <summary><c>ts_get</c>: reads the next token.</summary>
    /// <returns>The token and its surroundings.</returns>
    public TokenRead Get()
    {
        string whitespace = SubPart(White);
        string prepunctuation = string.Empty;
        if (!eof && IsClass(current, PrePunct))
        {
            prepunctuation = SubPart(PrePunct);
        }

        string token;
        if (!eof && IsClass(current, SingleChar))
        {
            token = ((char)current).ToString();
            Getc();
        }
        else
        {
            token = TokenCore();
        }

        string postpunctuation = string.Empty;
        if (postpunctuationIsSet)
        {
            // get_token_postpunctuation: the token never loses its first character
            int t = token.Length;
            int p = t;
            while (p > 0 && (p == t || IsClass(token[p], PostPunct)))
            {
                p--;
            }

            if (t != p)
            {
                postpunctuation = token[(p + 1)..];
                token = token[..(p + 1)];
            }
        }

        return new TokenRead(token, whitespace, prepunctuation, postpunctuation, filePos, lineNumber);
    }
}
