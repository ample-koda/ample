namespace Ample.Speech.Flite.Core;

// Port of Flite (CMU, commit 6c9f20dc), src/utils/cst_features.c. Translated to C# and modified: see Value.cs.

/// <summary>
/// A feature set (<c>cst_features</c>): values by name. A set can be linked to another one that is searched when the name is not here
/// (the utterance links the feature functions of the voice this way).
/// </summary>
internal sealed class FeatureSet
{
    private readonly Dictionary<string, Value> _values = new(StringComparer.Ordinal);

    /// <summary>The set that is searched next (<c>linked</c>), or null.</summary>
    public FeatureSet? Linked { get; set; }

    /// <summary><c>feat_length</c>: the number of features in this set (not in the linked one).</summary>
    public int Length => _values.Count;

    /// <summary><c>feat_present</c>, null tolerant: the name is in this set or in a linked one.</summary>
    public static bool Present(FeatureSet? f, string name)
    {
        for (FeatureSet? s = f; s is not null; s = s.Linked)
        {
            if (s._values.ContainsKey(name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><c>feat_val</c>, null tolerant: the value, or null when it is really not there.</summary>
    public static Value? Val(FeatureSet? f, string name)
    {
        for (FeatureSet? s = f; s is not null; s = s.Linked)
        {
            if (s._values.TryGetValue(name, out Value? v))
            {
                return v;
            }
        }

        return null;
    }

    /// <summary><c>feat_int</c>: the value through <c>val_int</c> (an error when the feature is missing).</summary>
    public static int Int(FeatureSet? f, string name) => Values.Int(Val(f, name));

    /// <summary><c>feat_float</c>: the value through <c>val_float</c> (an error when the feature is missing).</summary>
    public static float Float(FeatureSet? f, string name) => Values.Float(Val(f, name));

    /// <summary><c>feat_string</c>: the value through <c>val_string</c> (an error when it is missing or not a string).</summary>
    public static string String(FeatureSet? f, string name) => Values.String(Val(f, name));

    /// <summary><c>get_param_val</c>: the value, or the default when it is not there.</summary>
    public static Value? Param(FeatureSet? f, string name, Value? def)
    {
        Value? v = Val(f, name);
        return v ?? def;
    }

    /// <summary>
    /// <c>feat_set</c>: sets or replaces a feature. A null value is refused as in C (the C code prints a message and leaves the set as it was);
    /// the return value tells whether the value was stored.
    /// </summary>
    public bool Set(string name, Value? value)
    {
        if (value is null)
        {
            return false;
        }

        _values[name] = value;
        return true;
    }

    /// <summary><c>feat_set_int</c>.</summary>
    public void SetInt(string name, int value) => Set(name, new IntValue(value));

    /// <summary><c>feat_set_float</c>.</summary>
    public void SetFloat(string name, float value) => Set(name, new FloatValue(value));

    /// <summary><c>feat_set_string</c>.</summary>
    public void SetString(string name, string value) => Set(name, new StringValue(value));

    /// <summary><c>feat_remove</c>: true when the feature was there (only this set, not the linked one).</summary>
    public bool Remove(string name) => _values.Remove(name);
}
