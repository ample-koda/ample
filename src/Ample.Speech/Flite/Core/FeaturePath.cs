namespace Ample.Speech.Flite.Core;

// Port of Flite (CMU, commit 6c9f20dc), src/hrg/cst_ffeature.c. Translated to C# and modified: see Value.cs.

/// <summary>
/// The feature path evaluator (<c>ffeature</c>, <c>path_to_item</c>). A path is a list of steps and a feature, separated by "." or ":",
/// for example <c>R:SylStructure.parent.R:Syllable.p.syl_break</c>: the steps <c>n p pp nn parent daughter daughter1 daughtern first last</c>
/// and <c>R:name</c> (the item in another relation) move over the item graph; the last name is a feature function of the utterance or a feature of the item.
/// </summary>
internal static class FeaturePath
{
    /// <summary>The value a feature path gives when the item or the feature is not there: the string "0" (<c>ffeature_default_val</c>).</summary>
    public static readonly StringValue DefaultValue = new("0");

    // C copies at most 199 characters of the path into a buffer of 200 and splits it into at most 100 tokens
    private const int MaxPathLength = 199;

    /// <summary><c>ffeature</c>: the value of the feature path from <paramref name="item"/>. An unknown step gives null (the C code prints a message).</summary>
    public static Value? Ffeature(Item? item, string featpath) => Evaluate(item, featpath, false) as Value;

    /// <summary><c>path_to_item</c>: the item the whole path leads to (the last name too is a step).</summary>
    public static Item? PathToItem(Item? item, string featpath) => Evaluate(item, featpath, true) as Item;

    /// <summary><c>ffeature_string</c>.</summary>
    public static string FfeatureString(Item? item, string featpath) => Values.String(Ffeature(item, featpath));

    /// <summary><c>ffeature_int</c>.</summary>
    public static int FfeatureInt(Item? item, string featpath) => Values.Int(Ffeature(item, featpath));

    /// <summary><c>ffeature_float</c>.</summary>
    public static float FfeatureFloat(Item? item, string featpath) => Values.Float(Ffeature(item, featpath));

    private static string?[] Tokens(string featpath)
    {
        string path = featpath.Length > MaxPathLength ? featpath[..MaxPathLength] : featpath;
        List<string?> tokens = [];
        int start = 0;
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] is ':' or '.')
            {
                tokens.Add(path[start..i]);
                start = i + 1;
            }
        }

        tokens.Add(path[start..]);
        tokens.Add(null);
        return [.. tokens];
    }

    private static string? At(string?[] tokens, int j) => j < tokens.Length ? tokens[j] : null;

    private static object? Evaluate(Item? item, string featpath, bool pathOnly)
    {
        string?[] tokens = Tokens(featpath);
        int j = 0;
        string? tk = tokens[j];
        Item? pitem = item;
        while (pitem is not null && (pathOnly ? tk is not null : At(tokens, j + 1) is not null))
        {
            if (tk == "n")
            {
                pitem = Hrg.Next(pitem);
            }
            else if (tk == "p")
            {
                pitem = Hrg.Prev(pitem);
            }
            else if (tk == "pp")
            {
                pitem = Hrg.Prev(pitem) is not null ? Hrg.Prev(Hrg.Prev(pitem)) : null;
            }
            else if (tk == "nn")
            {
                pitem = Hrg.Next(pitem) is not null ? Hrg.Next(Hrg.Next(pitem)) : null;
            }
            else if (tk == "parent")
            {
                pitem = Hrg.Parent(pitem);
            }
            else if (tk is "daughter" or "daughter1")
            {
                pitem = Hrg.Daughter(pitem);
            }
            else if (tk == "daughtern")
            {
                pitem = Hrg.LastDaughter(pitem);
            }
            else if (tk == "first")
            {
                pitem = Hrg.First(pitem);
            }
            else if (tk == "last")
            {
                pitem = Hrg.Last(pitem);
            }
            else if (tk == "R")
            {
                // a relation move: the next token is the name of the relation
                j++;
                string relation = At(tokens, j) ?? throw new FliteException("ffeature: R without a relation name");
                pitem = Hrg.ItemAs(pitem, relation);
            }
            else
            {
                // "ffeature: unknown directive ignored" in C, which then returns NULL
                return null;
            }

            j++;
            tk = At(tokens, j);
        }

        if (pathOnly)
        {
            return pitem;
        }

        if (tk is null)
        {
            throw new FliteException("ffeature: the path ends in a step, not in a feature");
        }

        Value? ff = null;
        Utterance? utt = pitem is not null ? Hrg.Utt(pitem) : null;
        if (utt is not null)
        {
            ff = FeatureSet.Val(utt.FFunctions, tk);
        }

        Value? result = null;
        if (ff is null)
        {
            result = Hrg.Feat(pitem, tk);
        }
        else if (pitem is not null)
        {
            result = ((FunctionValue)ff).Function(pitem);
        }

        return result ?? DefaultValue;
    }
}
