using Ample.Speech.Flite.Core;
using Ample.Speech.Flite.Data;

namespace Ample.Speech.Flite.Prosody;

// Port of Flite (CMU, commit 6c9f20dc): the parameters that lang/cmu_us_kal16/cmu_us_kal16.c and lang/usenglish/usenglish.c set on the voice.
// Translated to C# and modified: the trees, the phoneset and the tables come from the converted data (Ample.Speech.Flite.Data).

/// <summary>The voice features the prosody modules read (the trees, the duration table, the F0 model parameters), as in the <c>cst_voice</c> of kal16.</summary>
internal sealed class FliteProsodyVoice
{
    /// <summary>The kind of an object value that holds the duration statistics (<c>dur_stats</c>).</summary>
    public const int TypeDurStats = 100;

    private FliteProsodyVoice(FeatureSet features, FeatureSet ffunctions)
    {
        Features = features;
        FFunctions = ffunctions;
    }

    /// <summary>The features of the voice: phoneset, silence, the trees, the duration statistics, the F0 and duration parameters.</summary>
    public FeatureSet Features { get; }

    /// <summary>The feature functions of the voice (<c>us_ff_register</c>).</summary>
    public FeatureSet FFunctions { get; }

    /// <summary>Builds the voice of the converted data: <c>usenglish_init</c> and the features of <c>register_cmu_us_kal16</c> that the prosody uses.</summary>
    /// <param name="english">The converted US English front end data.</param>
    public static FliteProsodyVoice Create(FliteEnglishData english)
    {
        FeatureSet features = new();
        FeatureSet ffunctions = new();
        Phoneset phoneset = ToPhoneset(english.Phoneset);
        features.Set("phoneset", new PhonesetValue(phoneset));
        features.SetString("silence", phoneset.Silence);
        features.Set("phrasing_cart", new ObjectValue(Values.TypeCart, ToCart(english.Cart("us_phrasing_cart"))));
        features.Set("int_cart_accents", new ObjectValue(Values.TypeCart, ToCart(english.Cart("us_int_accent_cart"))));
        features.Set("int_cart_tones", new ObjectValue(Values.TypeCart, ToCart(english.Cart("us_int_tone_cart"))));
        features.Set("dur_cart", new ObjectValue(Values.TypeCart, ToCart(english.Cart("us_durz_cart"))));
        features.Set("dur_stats", new ObjectValue(TypeDurStats, english.DurationStats));
        features.SetFloat("int_f0_target_mean", 95.0f);
        features.SetFloat("int_f0_target_stddev", 11.0f);
        features.SetFloat("duration_stretch", 1.1f);
        FeatureFunctions.Register(ffunctions, [.. english.WordLists.Select(list => new GposClass(list.Words[0], [.. list.Words.Skip(1)]))]);
        return new FliteProsodyVoice(features, ffunctions);
    }

    /// <summary><c>utt_init</c>: a new utterance whose features and feature functions are linked to those of the voice.</summary>
    public Utterance NewUtterance()
    {
        Utterance utterance = new();
        utterance.Features.Linked = Features;
        utterance.FFunctions.Linked = FFunctions;
        return utterance;
    }

    /// <summary>The tree of the converted data as an interpretable tree (the values are strings or floats, as in C).</summary>
    /// <param name="data">The converted tree.</param>
    public static Core.Cart ToCart(FliteCart data)
    {
        CartNode[] rules = new CartNode[data.Nodes.Count];
        for (int i = 0; i < rules.Length; i++)
        {
            FliteCartNode node = data.Nodes[i];
            Value? value = node.Kind switch
            {
                FliteCartValueKind.Text => new StringValue(node.Text!),
                FliteCartValueKind.Number => new FloatValue(node.Number),
                _ => null,
            };
            rules[i] = new CartNode(node.Feature, node.Operator, node.NoNode, value);
        }

        return new Core.Cart(rules, [.. data.Features]);
    }

    /// <summary>The phoneset of the converted data (<c>us_phoneset</c>).</summary>
    /// <param name="data">The converted phoneset.</param>
    public static Phoneset ToPhoneset(FlitePhoneset data) =>
        new("us", [.. data.FeatureNames], [.. data.FeatureValues.Select(v => (Value)new StringValue(v))], [.. data.PhoneNames], data.Silence, [.. data.Rows.Select(row => row.ToArray())]);
}
