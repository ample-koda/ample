namespace Ample.Speech.Flite.Core;

// Port of Flite (CMU, commit 6c9f20dc), src/hrg/cst_item.c, cst_relation.c, cst_utterance.c (the heterogeneous relation graph).
// Translated to C# and modified: pointers are references, freeing is left to the garbage collector, a C null dereference
// (a crash in C) is a FliteException. See Value.cs.

/// <summary>What an item stands for (<c>cst_item_contents</c>): its features, and the items that show it in each relation. Several items of different relations share one contents.</summary>
internal sealed class ItemContents
{
    public ItemContents()
    {
        Features = new FeatureSet();
        Relations = new FeatureSet();
    }

    /// <summary>The features of the thing itself (name, end, stress, ...).</summary>
    public FeatureSet Features { get; }

    /// <summary>Relation name to the <see cref="ItemValue"/> of the item that shows this contents in that relation.</summary>
    public FeatureSet Relations { get; }
}

/// <summary>
/// An item of one relation (<c>cst_item</c>): a place in a list (<see cref="N"/>, <see cref="P"/>) or tree (<see cref="U"/> up,
/// <see cref="D"/> first daughter) holding a shared <see cref="ItemContents"/>.
/// </summary>
internal sealed class Item
{
    public Item(Relation relation, Item? shared)
    {
        Relation = relation;
        ItemContentsSet(shared);
    }

    public ItemContents? Contents { get; private set; }

    public Item? N { get; set; }

    public Item? P { get; set; }

    public Item? U { get; set; }

    public Item? D { get; set; }

    public Relation Relation { get; }

    /// <summary><c>item_contents_set</c>: show the contents of <paramref name="shared"/> (or a new one when null) in this item.</summary>
    public void ItemContentsSet(Item? shared)
    {
        ItemContents c = shared is null ? new ItemContents() : shared.Contents!;
        if (!ReferenceEquals(c, Contents))
        {
            ItemUnrefContents();
            Contents = c;

            // if this contents is already in this relation, the other reference is emptied: C sets it to its own item again,
            // which changes nothing; the back reference below then points the contents at this item
            if (FeatureSet.Present(Contents.Relations, Relation.Name))
            {
                Item other = ((ItemValue)FeatureSet.Val(Contents.Relations, Relation.Name)!).Item;
                other.Contents!.Relations.Set(Relation.Name, new ItemValue(other));
            }

            Contents.Relations.Set(Relation.Name, new ItemValue(this));
        }
    }

    /// <summary><c>item_unref_contents</c>: forget the contents of this item (it disappears when no relation shows it any more).</summary>
    public void ItemUnrefContents()
    {
        if (Contents is not null)
        {
            Contents.Relations.Remove(Relation.Name);
            Contents = null;
        }
    }
}

/// <summary>A relation (<c>cst_relation</c>): a named list or tree of items of an utterance.</summary>
internal sealed class Relation
{
    public Relation(string name, Utterance utterance)
    {
        Name = name;
        Utterance = utterance;
        Features = new FeatureSet();
    }

    public string Name { get; }

    public FeatureSet Features { get; }

    public Item? Head { get; set; }

    public Item? Tail { get; set; }

    public Utterance Utterance { get; }

    /// <summary><c>relation_append</c>: a new item at the end; it shows the contents of <paramref name="shared"/> or new ones.</summary>
    public Item Append(Item? shared)
    {
        Item ni = new(this, shared);
        Head ??= ni;
        ni.P = Tail;
        if (Tail is not null)
        {
            Tail.N = ni;
        }

        Tail = ni;
        return ni;
    }

    /// <summary><c>relation_prepend</c>: a new item at the start.</summary>
    public Item Prepend(Item? shared)
    {
        Item ni = new(this, shared);
        Tail ??= ni;
        ni.N = Head;
        if (Head is not null)
        {
            Head.P = ni;
        }

        Head = ni;
        return ni;
    }
}

/// <summary>An utterance (<c>cst_utterance</c>): features, the feature functions (linked to those of the voice) and the relations by name.</summary>
internal sealed class Utterance
{
    private readonly FeatureSet _relations = new();

    public FeatureSet Features { get; } = new();

    public FeatureSet FFunctions { get; } = new();

    /// <summary><c>utt_relation_create</c>: a new empty relation; one of that name is deleted first.</summary>
    public Relation RelationCreate(string name)
    {
        RelationDelete(name);
        Relation r = new(name, this);
        _relations.Set(name, new RelationValue(r));
        return r;
    }

    /// <summary><c>utt_relation_delete</c>: true when the relation was there.</summary>
    public bool RelationDelete(string name)
    {
        if (FeatureSet.Present(_relations, name))
        {
            Relation r = ((RelationValue)FeatureSet.Val(_relations, name)!).Relation;
            for (Item? p = r.Head; p is not null;)
            {
                Item? np = p.N;
                Hrg.DeleteItem(p);
                p = np;
            }
        }

        return _relations.Remove(name);
    }

    /// <summary><c>utt_relation_present</c>.</summary>
    public bool RelationPresent(string name) => FeatureSet.Present(_relations, name);

    /// <summary><c>utt_relation</c>: the relation; an error when it is not there.</summary>
    public Relation GetRelation(string name) =>
        FeatureSet.Val(_relations, name) is RelationValue v ? v.Relation : throw new FliteException($"Relation: {name} not present in utterance");
}

/// <summary>The item operations of <c>cst_item.c</c> as functions that, like in C, accept a missing item (null) where the C code does.</summary>
internal static class Hrg
{
    /// <summary><c>item_as</c>: the same thing seen in another relation, or null.</summary>
    public static Item? ItemAs(Item? i, string relationName)
    {
        if (i is null)
        {
            return null;
        }

        Value? v = FeatureSet.Val(i.Contents!.Relations, relationName);
        return v is null ? null : ((ItemValue)v).Item;
    }

    /// <summary><c>item_next</c>.</summary>
    public static Item? Next(Item? i) => i?.N;

    /// <summary><c>item_prev</c>.</summary>
    public static Item? Prev(Item? i) => i?.P;

    /// <summary><c>item_append</c>: a new item after <paramref name="current"/>. The C code crashes when the item to share is already in this relation.</summary>
    public static Item Append(Item current, Item? shared)
    {
        if (shared is not null && ReferenceEquals(shared.Relation, current.Relation))
        {
            throw new FliteException("item_append: an item cannot be in a relation twice");
        }

        Item rni = new(current.Relation, shared)
        {
            N = current.N,
        };
        if (current.N is not null)
        {
            current.N.P = rni;
        }

        rni.P = current;
        current.N = rni;
        if (ReferenceEquals(current.Relation.Tail, current))
        {
            current.Relation.Tail = rni;
        }

        return rni;
    }

    /// <summary><c>item_prepend</c>: a new item before <paramref name="current"/>; in a tree it takes over the parent link.</summary>
    public static Item Prepend(Item current, Item? shared)
    {
        if (shared is not null && ReferenceEquals(shared.Relation, current.Relation))
        {
            throw new FliteException("item_prepend: an item cannot be in a relation twice");
        }

        Item rni = new(current.Relation, shared)
        {
            P = current.P,
        };
        if (current.P is not null)
        {
            current.P.N = rni;
        }

        rni.N = current;
        current.P = rni;
        if (current.U is not null)
        {
            current.U.D = rni;
            rni.U = current.U;
            current.U = null;
        }

        if (ReferenceEquals(current.Relation.Head, current))
        {
            current.Relation.Head = rni;
        }

        return rni;
    }

    /// <summary><c>item_parent</c>: the parent in a tree (the up link of the first of the siblings), or null.</summary>
    public static Item? Parent(Item? i)
    {
        Item? n = i;
        while (Prev(n) is not null)
        {
            n = Prev(n);
        }

        return n?.U;
    }

    /// <summary><c>item_daughter</c>: the first daughter.</summary>
    public static Item? Daughter(Item? i) => i?.D;

    /// <summary><c>item_nth_daughter</c>.</summary>
    public static Item? NthDaughter(Item? i, int n)
    {
        int d = 0;
        Item? p = Daughter(i);
        while (p is not null && d < n)
        {
            p = Next(p);
            d++;
        }

        return p;
    }

    /// <summary><c>item_last_daughter</c>.</summary>
    public static Item? LastDaughter(Item? i)
    {
        Item? p = Daughter(i);
        while (Next(p) is not null)
        {
            p = Next(p);
        }

        return p;
    }

    /// <summary><c>item_first</c>: the first of the siblings.</summary>
    public static Item? First(Item? i)
    {
        Item? p = i;
        while (Prev(p) is not null)
        {
            p = Prev(p);
        }

        return p;
    }

    /// <summary><c>item_last</c>: the last of the siblings.</summary>
    public static Item? Last(Item? i)
    {
        Item? p = i;
        while (Next(p) is not null)
        {
            p = Next(p);
        }

        return p;
    }

    /// <summary><c>item_add_daughter</c>: a new last daughter; null (with a message in C) when the first daughter would be an item that is in this relation already.</summary>
    public static Item? AddDaughter(Item i, Item? shared)
    {
        Item? p = LastDaughter(i);
        if (p is not null)
        {
            return Append(p, shared);
        }

        if (shared is not null && ReferenceEquals(shared.Relation, i.Relation))
        {
            return null;
        }

        Item rnd = new(i.Relation, shared)
        {
            U = i,
        };
        i.D = rnd;
        return rnd;
    }

    /// <summary><c>delete_item</c>: removes the item and its daughters from the lists and trees.</summary>
    public static void DeleteItem(Item item)
    {
        if (item.N is not null)
        {
            item.N.P = item.P;
            item.N.U = item.U;
        }

        if (item.P is not null)
        {
            item.P.N = item.N;
        }

        if (item.U is not null)
        {
            item.U.D = item.N;
        }

        if (ReferenceEquals(item.Relation.Head, item))
        {
            item.Relation.Head = item.N;
        }

        if (ReferenceEquals(item.Relation.Tail, item))
        {
            item.Relation.Tail = item.P;
        }

        Item? nds;
        for (Item? ds = item.D; ds is not null; ds = nds)
        {
            nds = ds.N;
            DeleteItem(ds);
        }

        item.ItemUnrefContents();
    }

    /// <summary><c>item_equal</c>: the same item, or two items that show the same contents.</summary>
    public static bool Equal(Item? a, Item? b) => ReferenceEquals(a, b) || (a is not null && b is not null && ReferenceEquals(a.Contents, b.Contents));

    /// <summary><c>item_feats</c>.</summary>
    public static FeatureSet? Feats(Item? i) => i?.Contents!.Features;

    /// <summary><c>item_feat_present</c>.</summary>
    public static bool FeatPresent(Item? i, string name) => FeatureSet.Present(Feats(i), name);

    /// <summary><c>item_feat</c>: the feature value of the contents, or null.</summary>
    public static Value? Feat(Item? i, string name) => FeatureSet.Val(Feats(i), name);

    /// <summary><c>item_feat_int</c>.</summary>
    public static int FeatInt(Item? i, string name) => FeatureSet.Int(Feats(i), name);

    /// <summary><c>item_feat_float</c>.</summary>
    public static float FeatFloat(Item? i, string name) => FeatureSet.Float(Feats(i), name);

    /// <summary><c>item_feat_string</c>.</summary>
    public static string FeatString(Item? i, string name) => FeatureSet.String(Feats(i), name);

    /// <summary><c>item_name</c>: the string feature "name".</summary>
    public static string Name(Item? i) => FeatString(i, "name");

    /// <summary><c>item_set</c>.</summary>
    public static void Set(Item i, string name, Value? value) => Feats(i)!.Set(name, value);

    /// <summary><c>item_set_int</c>.</summary>
    public static void SetInt(Item i, string name, int value) => Feats(i)!.SetInt(name, value);

    /// <summary><c>item_set_float</c>.</summary>
    public static void SetFloat(Item i, string name, float value) => Feats(i)!.SetFloat(name, value);

    /// <summary><c>item_set_string</c>.</summary>
    public static void SetString(Item i, string name, string value) => Feats(i)!.SetString(name, value);

    /// <summary><c>item_utt</c>: the utterance the relation of the item belongs to.</summary>
    public static Utterance? Utt(Item? i) => i?.Relation.Utterance;
}
