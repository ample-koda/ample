using Ample.Speech.Flite.Core;
using Ample.Speech.Flite.Data;
using Ample.Speech.Flite.Prosody;

namespace Ample.Speech.Flite.Text;

// Port of Flite (CMU), src/synth/cst_synth.c (default_tokenization, default_textanalysis, default_pos_tagger) and lang/usenglish/usenglish.c.
// Translated to C# and modified: see UsText.cs.

/// <summary>The models of the US English front end in the form of the core model: the trees, the phone set and the word classes, made from the embedded data.</summary>
internal sealed class UsEnglishModels
{
    private static readonly Lazy<UsEnglishModels> Shared = new(() => new UsEnglishModels(FliteEnglishData.Instance));

    private UsEnglishModels(FliteEnglishData data)
    {
        Data = data;
        NumsCart = ToCart(data.Cart("us_nums_cart"));
        PosCart = ToCart(data.Cart("us_pos_cart"));
        PhrasingCart = ToCart(data.Cart("us_phrasing_cart"));
        Phoneset = FliteProsodyVoice.ToPhoneset(data.Phoneset);

        // us_gpos: the first member of a list is the name of the class, the others are its words
        Gpos = [.. data.WordLists.Select(list => new GposClass(list.Words[0], [.. list.Words.Skip(1)]))];
    }

    /// <summary>The shared instance.</summary>
    public static UsEnglishModels Instance => Shared.Value;

    /// <summary>The embedded data.</summary>
    public FliteEnglishData Data { get; }

    /// <summary><c>us_nums_cart</c>: how a string of digits is read.</summary>
    public Cart NumsCart { get; }

    /// <summary><c>us_pos_cart</c>: the simple part-of-speech tagger.</summary>
    public Cart PosCart { get; }

    /// <summary><c>us_phrasing_cart</c>: where the phrase breaks are.</summary>
    public Cart PhrasingCart { get; }

    /// <summary><c>us_phoneset</c>.</summary>
    public Phoneset Phoneset { get; }

    /// <summary>The word classes of <c>us_gpos</c>.</summary>
    public IReadOnlyList<GposClass> Gpos { get; }

    private static Cart ToCart(FliteCart cart) => FliteProsodyVoice.ToCart(cart);
}

/// <summary>
/// The text stages of the US English voice: tokenization, text analysis (the Word relation) and the part-of-speech tagger, as <c>utt_synth</c> runs them before phrasing.
/// </summary>
/// <param name="lexicon">The lexicon, as far as the text analysis asks it.</param>
internal sealed class UsEnglishFrontEnd(ILexicon lexicon)
{
    private readonly UsEnglishModels models = UsEnglishModels.Instance;

    /// <summary>A new utterance with the voice features and the feature functions of the US English voice (the phone set, the silence phone and <c>us_ff_register</c>).</summary>
    /// <returns>The utterance.</returns>
    public Utterance NewUtterance()
    {
        Utterance u = new();
        u.Features.Set("phoneset", new PhonesetValue(models.Phoneset));
        u.Features.SetString("silence", models.Phoneset.Silence);
        FeatureFunctions.Register(u.FFunctions, models.Gpos);
        return u;
    }

    /// <summary>Runs the tokenizer, the text analysis and the part-of-speech tagger over a text.</summary>
    /// <param name="byteText">The text as a byte string (<see cref="ByteText"/>).</param>
    /// <returns>The utterance with the relations Token and Word.</returns>
    public Utterance Analyse(string byteText)
    {
        Utterance u = NewUtterance();
        Tokenize(u, byteText);
        TextAnalysis(u);
        PosTag(u);
        return u;
    }

    /// <summary><c>default_tokenization</c>: the Token relation, one item per token of the text.</summary>
    /// <param name="u">The utterance.</param>
    /// <param name="byteText">The text as a byte string.</param>
    public static void Tokenize(Utterance u, string byteText)
    {
        Relation r = u.RelationCreate("Token");
        TokenStream fd = new(byteText);
        while (!fd.Eof)
        {
            TokenRead token = fd.Get();
            if (token.Name.Length > 0)
            {
                Item t = r.Append(null);
                Hrg.SetString(t, "name", token.Name);
                Hrg.SetString(t, "whitespace", token.Whitespace);
                Hrg.SetString(t, "prepunctuation", token.Prepunctuation);
                Hrg.SetString(t, "punc", token.Postpunctuation);
                Hrg.SetInt(t, "file_pos", token.FilePos);
                Hrg.SetInt(t, "line_number", token.LineNumber);
            }
        }
    }

    /// <summary>The Token relation of an utterance that <c>flite_ts_to_speech</c> built from the tokens it read (one item per token, an empty name included, as C appends it).</summary>
    /// <param name="u">The utterance.</param>
    /// <param name="tokens">The tokens of the utterance.</param>
    internal static void TokenizeTokens(Utterance u, IReadOnlyList<TokenRead> tokens)
    {
        Relation r = u.RelationCreate("Token");
        foreach (TokenRead token in tokens)
        {
            Item t = r.Append(null);
            Hrg.SetString(t, "name", token.Name);
            Hrg.SetString(t, "whitespace", token.Whitespace);
            Hrg.SetString(t, "prepunctuation", token.Prepunctuation);
            Hrg.SetString(t, "punc", token.Postpunctuation);
            Hrg.SetInt(t, "file_pos", token.FilePos);
            Hrg.SetInt(t, "line_number", token.LineNumber);
        }
    }

    /// <summary><c>default_textanalysis</c> with <c>us_tokentowords</c>: the Word relation, the words of every token as daughters of the token.</summary>
    /// <param name="u">The utterance.</param>
    /// <param name="user">The words of the block (their own phones, the words that are spelled), or null.</param>
    /// <param name="cancellation">Stops the work; it is looked at every few hundred tokens, so that a huge text can be cancelled.</param>
    public void TextAnalysis(Utterance u, Lexicon.FliteUserLexicon? user = null, CancellationToken cancellation = default)
    {
        Relation wordRel = u.RelationCreate("Word");
        UsText text = new(models.Data, user is null || user.IsEmpty ? lexicon : user.Over(lexicon), models.NumsCart);
        int count = 0;
        for (Item? t = u.GetRelation("Token").Head; t is not null; t = Hrg.Next(t))
        {
            if ((++count & 255) == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            foreach (ExpWord w in text.TokenToWords(t))
            {
                Item word = Hrg.AddDaughter(t, null)!;
                Hrg.SetString(word, "name", w.Name);
                if (w.Break)
                {
                    Hrg.SetString(word, "break", "1");
                }

                wordRel.Append(word);
            }
        }
    }

    /// <summary><c>default_pos_tagger</c>: the feature <c>pos</c> of every word, from <c>us_pos_cart</c>.</summary>
    /// <param name="u">The utterance.</param>
    /// <param name="cancellation">Stops the work; it is looked at every few hundred words.</param>
    public void PosTag(Utterance u, CancellationToken cancellation = default)
    {
        int count = 0;
        for (Item? word = u.GetRelation("Word").Head; word is not null; word = Hrg.Next(word))
        {
            if ((++count & 255) == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            Hrg.SetString(word, "pos", Values.String(models.PosCart.Interpret(word)));
        }
    }
}
