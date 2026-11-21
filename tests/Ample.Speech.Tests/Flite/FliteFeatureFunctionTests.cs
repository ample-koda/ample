using Ample.Speech.Flite.Core;

namespace Ample.Speech.Tests.Flite;

/// <summary>
/// Flite M2: the feature functions on a hand-built utterance of the two words "hello world" in one phrase,
/// with the syllables hh ax | l ow | w er l d. The expected values were worked out by hand from the C code.
/// </summary>
public sealed class FliteFeatureFunctionTests
{
    private readonly Utterance _utt = new();
    private readonly Item[] _word = new Item[2];
    private readonly Item[] _syl = new Item[3];
    private readonly Item[] _seg = new Item[8];

    public FliteFeatureFunctionTests()
    {
        // the phoneset knows only vc and ctype; vc: + for a vowel, ctype: f fricative, l liquid, r glide, s stop, 0 none
        Phoneset ps = new(
            "t",
            ["vc", "ctype"],
            [new StringValue("+"), new StringValue("-"), new StringValue("0"), new StringValue("f"), new StringValue("l"), new StringValue("r"), new StringValue("s")],
            ["hh", "ax", "l", "ow", "w", "er", "d"],
            "pau",
            [[1, 3], [0, 2], [1, 4], [0, 2], [1, 5], [0, 2], [1, 6]]);
        _utt.Features.Set("phoneset", new PhonesetValue(ps));
        FeatureFunctions.Register(_utt.FFunctions, [new GposClass("in", "in", "of", "for")]);

        Relation word = _utt.RelationCreate("Word");
        Relation phrase = _utt.RelationCreate("Phrase");
        Relation syllable = _utt.RelationCreate("Syllable");
        Relation segment = _utt.RelationCreate("Segment");
        Relation ss = _utt.RelationCreate("SylStructure");

        Item root = phrase.Append(null);
        Hrg.SetString(root, "name", "BB");
        string[] words = ["hello", "world"];
        string[][] syllables = [["hh", "ax"], ["l", "ow"], ["w", "er", "l", "d"]];
        string[] stress = ["0", "1", "1"];
        int[] sylOfWord = [0, 2, 3];
        float[] ends = [0.1f, 0.2f, 0.3f, 0.45f, 0.55f, 0.7f, 0.8f, 0.95f];

        int segCount = 0;
        for (int w = 0; w < 2; w++)
        {
            _word[w] = word.Append(null);
            Hrg.SetString(_word[w], "name", words[w]);
            Hrg.AddDaughter(root, _word[w]);
            Item ssWord = ss.Append(_word[w]);
            for (int s = sylOfWord[w]; s < sylOfWord[w + 1]; s++)
            {
                _syl[s] = syllable.Append(null);
                Hrg.SetString(_syl[s], "stress", stress[s]);
                Item ssSyl = Hrg.AddDaughter(ssWord, _syl[s])!;
                foreach (string phone in syllables[s])
                {
                    _seg[segCount] = segment.Append(null);
                    Hrg.SetString(_seg[segCount], "name", phone);
                    Hrg.SetFloat(_seg[segCount], "end", ends[segCount]);
                    Hrg.AddDaughter(ssSyl, _seg[segCount]);
                    segCount++;
                }
            }
        }

        Hrg.SetString(_syl[1], "accent", "H*");
        Hrg.SetString(_syl[2], "endtone", "L-L%");
    }

    private static string S(Value? v) => Values.String(v);

    [Fact]
    public void Words_and_breaks()
    {
        Assert.Equal(2, Values.Int(FeatureFunctions.WordNumsyls(_word[0])));
        Assert.Equal(1, Values.Int(FeatureFunctions.WordNumsyls(_word[1])));
        Assert.Equal("1", S(FeatureFunctions.WordBreak(_word[0])));
        Assert.Equal("4", S(FeatureFunctions.WordBreak(_word[1])));
        Assert.Equal("0", S(FeatureFunctions.SylBreak(_syl[0])));
        Assert.Equal("1", S(FeatureFunctions.SylBreak(_syl[1])));
        Assert.Equal("4", S(FeatureFunctions.SylBreak(_syl[2])));
        Assert.Equal("4", S(FeaturePath.Ffeature(_syl[2], "old_syl_break")));
    }

    [Fact]
    public void Syllable_shape_and_position()
    {
        Assert.Equal("1", S(FeatureFunctions.SylOnsetsize(_syl[0])));
        Assert.Equal("1", S(FeatureFunctions.SylOnsetsize(_syl[2])));
        Assert.Equal("1", S(FeatureFunctions.SylCodasize(_syl[0])));
        Assert.Equal("3", S(FeatureFunctions.SylCodasize(_syl[2])));
        Assert.Equal("initial", S(FeatureFunctions.PositionType(_syl[0])));
        Assert.Equal("final", S(FeatureFunctions.PositionType(_syl[1])));
        Assert.Equal("single", S(FeatureFunctions.PositionType(_syl[2])));
        Assert.Equal("0", S(FeatureFunctions.PosInWord(_syl[0])));
        Assert.Equal("1", S(FeatureFunctions.PosInWord(_syl[1])));
        Assert.Equal("1", S(FeatureFunctions.PosInPhrase(_word[1])));
        Assert.Equal("0", S(FeatureFunctions.PosInPhrase(_word[0])));
    }

    [Fact]
    public void Syllables_in_and_out_of_the_phrase()
    {
        Assert.Equal("2", S(FeatureFunctions.SylIn(_syl[2])));
        Assert.Equal("0", S(FeatureFunctions.SylIn(_syl[0])));
        Assert.Equal("2", S(FeatureFunctions.SylOut(_syl[0])));
        Assert.Equal("0", S(FeatureFunctions.SylOut(_syl[2])));
        Assert.Equal("0", S(FeatureFunctions.SubPhrases(_syl[0])));
    }

    [Fact]
    public void Accents_and_stress_counts()
    {
        Assert.Equal("0", S(FeatureFunctions.Accented(_syl[0])));
        Assert.Equal("1", S(FeatureFunctions.Accented(_syl[1])));
        Assert.Equal("1", S(FeatureFunctions.Accented(_syl[2])));
        Assert.Equal("1", S(FeatureFunctions.AsylIn(_syl[2])));
        Assert.Equal("2", S(FeatureFunctions.AsylOut(_syl[0])));
        Assert.Equal("1", S(FeatureFunctions.SsylIn(_syl[2])));
        Assert.Equal("2", S(FeatureFunctions.SsylOut(_syl[0])));
        Assert.Equal("0", S(FeatureFunctions.SsylIn(_syl[0])));
        Assert.Equal("0", S(FeatureFunctions.LastAccent(_syl[2])));
        Assert.Equal("1", S(FeatureFunctions.LastAccent(_syl[0])));
        Assert.Equal("0", S(FeatureFunctions.NextAccent(_syl[0])));
        Assert.Equal("0", S(FeatureFunctions.NextAccent(_syl[2])));
    }

    [Fact]
    public void Segment_features()
    {
        Assert.Equal("+", S(FeatureFunctions.PhVc(_seg[1])));
        Assert.Equal("f", S(FeatureFunctions.PhCtype(_seg[0])));
        Assert.Equal("0", S(FeatureFunctions.PosInSyl(_seg[2])));
        Assert.Equal("1", S(FeatureFunctions.PosInSyl(_seg[3])));
        Assert.Equal("onset", S(FeatureFunctions.SegOnsetcoda(_seg[0])));
        Assert.Equal("coda", S(FeatureFunctions.SegOnsetcoda(_seg[1])));
        Assert.Equal("1", S(FeatureFunctions.SylInitial(_seg[0])));
        Assert.Equal("0", S(FeatureFunctions.SylInitial(_seg[1])));
        Assert.Equal("0", S(FeatureFunctions.SylFinal(_seg[0])));
        Assert.Equal("1", S(FeatureFunctions.SylFinal(_seg[1])));
        Assert.Equal("ow", S(FeatureFunctions.SylVowel(_syl[1])));
        Assert.Equal("4", S(FeatureFunctions.SylNumphones(_syl[2])));
    }

    [Fact]
    public void Onset_and_coda_consonant_classes_are_looked_up_by_the_names_of_the_c_code()
    {
        // lisp_onset_fric of hh: hh is a fricative in the onset
        Assert.Equal("1", S(FeaturePath.Ffeature(_seg[0], "lisp_onset_fric")));
        Assert.Equal("0", S(FeaturePath.Ffeature(_seg[0], "lisp_coda_fric")));
        Assert.Equal("1", S(FeaturePath.Ffeature(_seg[7], "lisp_coda_stop")));
        Assert.Equal("1", S(FeaturePath.Ffeature(_seg[7], "lisp_coda_glide")));

        // the trees ask for "seg_coda_fric": it is not a registered name (the C code registers lisp_coda_fric), so the path gives the default "0"
        Assert.Equal("0", S(FeaturePath.Ffeature(_seg[0], "seg_coda_fric")));
    }

    [Fact]
    public void Durations_are_floats_of_the_end_times()
    {
        Assert.Equal(0.1f, Values.Float(FeatureFunctions.SegmentDuration(_seg[0])));
        Assert.Equal(0.2f - 0.1f, Values.Float(FeatureFunctions.SegmentDuration(_seg[1])));
        Assert.IsType<FloatValue>(FeatureFunctions.SegmentDuration(_seg[1]));
        Assert.Equal(0.45f - 0.2f, Values.Float(FeatureFunctions.SyllableDuration(_syl[1])));
    }

    [Fact]
    public void Word_classes_and_content_words()
    {
        Assert.Equal("content", S(FeaturePath.Ffeature(_word[0], "gpos")));
        Hrg.SetString(_word[0], "name", "of");
        Assert.Equal("in", S(FeaturePath.Ffeature(_word[0], "gpos")));
        Hrg.SetString(_word[0], "name", "hello");
        Assert.Equal("1", S(FeatureFunctions.ContentWordsIn(_word[1])));
        Assert.Equal("0", S(FeatureFunctions.ContentWordsIn(_word[0])));
        Assert.Equal("1", S(FeatureFunctions.ContentWordsOut(_word[0])));
        Assert.Equal("0", S(FeatureFunctions.ContentWordsOut(_word[1])));
    }

    [Fact]
    public void Token_features()
    {
        Relation token = _utt.RelationCreate("Token");
        Item T(string name)
        {
            Item t = token.Append(null);
            Hrg.SetString(t, "name", name);
            return t;
        }

        Assert.Equal(4, Values.Int(FeatureFunctions.NumDigits(T("1234"))));
        Assert.Equal(2, Values.Int(FeatureFunctions.NumDigits(T("Ã©"))));   // the name is a byte string: the UTF-8 bytes of the letter
        Assert.Equal("1", S(FeatureFunctions.MonthRange(T("12"))));
        Assert.Equal("0", S(FeatureFunctions.MonthRange(T("32"))));
        Assert.Equal("0", S(FeatureFunctions.MonthRange(T("x"))));
        Assert.Equal("numeric", S(FeatureFunctions.TokenPosGuess(T("123"))));
        Assert.Equal("number", S(FeatureFunctions.TokenPosGuess(T("-1.5e3"))));
        Assert.Equal("month", S(FeatureFunctions.TokenPosGuess(T("Sept"))));
        Assert.Equal("day", S(FeatureFunctions.TokenPosGuess(T("Friday"))));
        Assert.Equal("a", S(FeatureFunctions.TokenPosGuess(T("A"))));
        Assert.Equal("to", S(FeatureFunctions.TokenPosGuess(T("To"))));
        Assert.Equal("flight", S(FeatureFunctions.TokenPosGuess(T("FLIGHT"))));
        Assert.Equal("_other_", S(FeatureFunctions.TokenPosGuess(T("hello"))));
    }

    [Fact]
    public void Word_punctuation_comes_from_the_token_when_the_word_ends_it()
    {
        Relation token = _utt.RelationCreate("Token");
        Item t = token.Append(null);
        Hrg.SetString(t, "name", "hello world");
        Hrg.SetString(t, "punc", ",");
        Item tw = Hrg.AddDaughter(t, _word[1])!;

        // a word that is the last of its token gets the punctuation of the token, a word that is not the last one gets ""
        Assert.Equal(",", S(FeatureFunctions.WordPunc(_word[1])));
        Item more = Hrg.AddDaughter(t, null)!;
        Hrg.SetString(more, "name", "again");
        Assert.Equal(string.Empty, S(FeatureFunctions.WordPunc(_word[1])));
        Assert.NotNull(tw);
    }
}
