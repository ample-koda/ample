namespace Ample.Speech.Flite.Core;

// Port of Flite (CMU, commit 6c9f20dc), src/stats/cst_cart.c and include/cst_cart.h. Translated to C# and modified: see Value.cs.

/// <summary>One row of a classification and regression tree (<c>cst_cart_node</c>): a question, or a leaf when <see cref="Op"/> is <see cref="CartOps.Leaf"/>.</summary>
/// <param name="Feat">Index into the feature table of the tree (the feature path of the question).</param>
/// <param name="Op">The comparison, see <see cref="CartOps"/>.</param>
/// <param name="NoNode">The row to go to when the answer is no (the row after this one is the yes row).</param>
/// <param name="Val">The value to compare with, or the answer of a leaf (for <see cref="CartOps.Matches"/> an int: the index in the regex table).</param>
internal readonly record struct CartNode(byte Feat, byte Op, ushort NoNode, Value? Val);

/// <summary>The operations of a tree row (<c>CST_CART_OP_*</c>).</summary>
internal static class CartOps
{
    public const byte Leaf = 255;
    public const byte Is = 0;
    public const byte In = 1;
    public const byte Less = 2;
    public const byte Greater = 3;
    public const byte Matches = 4;
    public const byte EqualsOp = 5;
}

/// <summary>A tree (<c>cst_cart</c>): rows plus the table of feature paths the rows point to. A loader fills these arrays (see the loader of milestone M1).</summary>
/// <param name="Rules">The rows; row 0 is the root. In the C data the table ends with a row whose value is null: it is not needed here.</param>
/// <param name="FeatTable">The feature paths.</param>
/// <param name="RegexTable">Decides a MATCHES question: the regular expression with the given index (<c>cst_regex_table</c>) matches the string; null when the tree has none.</param>
internal sealed record Cart(CartNode[] Rules, string[] FeatTable, Func<int, string, bool>? RegexTable = null)
{

    /// <summary>
    /// <c>cart_interpret</c> for an item: the answer (the value of the leaf) of the tree. Every feature path of the tree is evaluated at most once
    /// per call (<c>fcache</c>), by <see cref="FeaturePath.Ffeature"/>.
    /// </summary>
    public Value? Interpret(Item item, Action<string, Value?>? onFeature = null) => Interpret(path => FeaturePath.Ffeature(item, path), onFeature);

    /// <summary>
    /// <c>cart_interpret</c> with the features given by a function (the interpreter does not need the item graph): the answer of the tree.
    /// <paramref name="onFeature"/> is told, in order, of every feature path that is asked for the first time and the value it got.
    /// </summary>
    public Value? Interpret(Func<string, Value?> feature, Action<string, Value?>? onFeature = null)
    {
        FeatureSet cache = new();
        int node = 0;
        while (Rules[node].Op != CartOps.Leaf)
        {
            CartNode n = Rules[node];
            string treeFeat = FeatTable[n.Feat];
            Value? v = FeatureSet.Param(cache, treeFeat, null);
            if (v is null)
            {
                v = feature(treeFeat);

                // a null value is not cached: the C code only prints a message, and asks again when the path comes back
                cache.Set(treeFeat, v);
                onFeature?.Invoke(treeFeat, v);
            }

            bool r = n.Op switch
            {
                CartOps.Is => Values.Equal(v, n.Val),
                CartOps.Less => Values.Less(v, n.Val),
                CartOps.Greater => Values.Greater(v, n.Val),
                CartOps.In => Values.Member(v, n.Val),
                CartOps.Matches => (RegexTable ?? throw new FliteException("cart_interpret: no regex table")).Invoke(Values.Int(n.Val), Values.String(v)),
                _ => throw new FliteException($"cart_interpret_question: unknown op type {n.Op}"),
            };

            node = r ? node + 1 : n.NoNode;
        }

        return Rules[node].Val;
    }
}
