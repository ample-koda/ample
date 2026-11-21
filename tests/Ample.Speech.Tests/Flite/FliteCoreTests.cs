using Ample.Speech.Flite.Core;

namespace Ample.Speech.Tests.Flite;

/// <summary>Flite M2: the core model on hand-built data (values, features, items and relations, feature paths, CART questions).</summary>
public sealed class FliteCoreTests
{
    [Fact]
    public void Values_are_strictly_typed_in_equality_but_coerce_in_accessors()
    {
        Assert.True(Values.Equal(new IntValue(3), new IntValue(3)));
        Assert.False(Values.Equal(new IntValue(1), new StringValue("1")));
        Assert.False(Values.Equal(new IntValue(1), new FloatValue(1f)));
        Assert.True(Values.Equal(new StringValue("a"), new StringValue("a")));
        Assert.False(Values.Equal(null, new StringValue("a")));
        Assert.True(Values.Equal(null, null));

        Assert.Equal(12, Values.Int(new StringValue("12abc")));
        Assert.Equal(-7, Values.Int(new StringValue("  -7")));
        Assert.Equal(0, Values.Int(new StringValue("x")));
        Assert.Equal(2, Values.Int(new FloatValue(2.9f)));
        Assert.Equal(-2, Values.Int(new FloatValue(-2.9f)));
        Assert.Equal(1.5f, Values.Float(new StringValue("1.5e0xyz")));
        Assert.Equal(0f, Values.Float(new StringValue("hello")));
        Assert.Equal(4f, Values.Float(new IntValue(4)));
        Assert.Throws<FliteException>(() => Values.String(new IntValue(4)));
        Assert.Throws<FliteException>(() => Values.Int(null));
    }

    [Fact]
    public void Shared_numbers_clamp_at_the_table_size_of_the_c_code()
    {
        Assert.Equal("0", Values.String(Values.StringN(-5)));
        Assert.Equal("7", Values.String(Values.StringN(7)));
        Assert.Equal("24", Values.String(Values.StringN(24)));
        Assert.Equal("24", Values.String(Values.StringN(100)));
        Assert.Equal(24, Values.Int(Values.IntN(1000)));
        Assert.Same(Values.StringN(3), Values.String3);
    }

    [Fact]
    public void Comparison_goes_through_floats_and_members_through_equality()
    {
        Assert.True(Values.Less(new StringValue("2"), new IntValue(3)));
        Assert.False(Values.Less(new IntValue(3), new IntValue(3)));
        Assert.True(Values.Greater(new FloatValue(3.5f), new StringValue("3")));
        ConsValue list = new(new StringValue("a"), new ConsValue(new IntValue(5), null));
        Assert.True(Values.Member(new StringValue("a"), list));
        Assert.True(Values.Member(new IntValue(5), list));
        Assert.False(Values.Member(new StringValue("5"), list));
        Assert.Equal(2, Values.Length(list));
        Assert.True(Values.Equal(list, new ConsValue(new StringValue("a"), new ConsValue(new IntValue(5), null))));
    }

    [Fact]
    public void A_feature_set_searches_its_linked_set_and_refuses_null()
    {
        FeatureSet voice = new();
        voice.SetString("lang", "us");
        FeatureSet utt = new() { Linked = voice };
        utt.SetInt("n", 2);
        Assert.True(FeatureSet.Present(utt, "lang"));
        Assert.Equal("us", FeatureSet.String(utt, "lang"));
        Assert.Equal(1, utt.Length);
        Assert.False(utt.Set("x", null));
        Assert.False(FeatureSet.Present(utt, "x"));
        Assert.Null(FeatureSet.Val(null, "x"));
        Assert.Same(Values.String0, FeatureSet.Param(utt, "x", Values.String0));
        Assert.True(utt.Remove("n"));
        Assert.False(utt.Remove("lang"));
    }

    [Fact]
    public void Items_of_several_relations_share_their_contents()
    {
        Utterance utt = new();
        Relation word = utt.RelationCreate("Word");
        Relation ss = utt.RelationCreate("SylStructure");
        Item w = word.Append(null);
        Hrg.SetString(w, "name", "hello");
        Item sw = ss.Append(w);

        Assert.True(Hrg.Equal(w, sw));
        Assert.Equal("hello", Hrg.Name(sw));
        Assert.Same(w, Hrg.ItemAs(sw, "Word"));
        Assert.Same(sw, Hrg.ItemAs(w, "SylStructure"));
        Assert.Null(Hrg.ItemAs(w, "Phrase"));
        Assert.Null(Hrg.ItemAs(null, "Word"));
    }

    [Fact]
    public void A_list_relation_appends_prepends_and_deletes()
    {
        Utterance utt = new();
        Relation r = utt.RelationCreate("Segment");
        Item b = r.Append(null);
        Item a = r.Prepend(null);
        Item c = Hrg.Append(b, null);
        Item x = Hrg.Prepend(b, null);
        Assert.Same(a, r.Head);
        Assert.Same(c, r.Tail);
        Assert.Same(a, Hrg.First(c));
        Assert.Same(c, Hrg.Last(a));
        Assert.Same(x, Hrg.Next(a));
        Assert.Same(b, Hrg.Next(x));

        Hrg.DeleteItem(x);
        Assert.Same(b, Hrg.Next(a));
        Assert.Same(a, Hrg.Prev(b));
        Hrg.DeleteItem(a);
        Assert.Same(b, r.Head);
        Assert.Throws<FliteException>(() => Hrg.Append(b, c));
    }

    [Fact]
    public void A_tree_relation_has_daughters_and_a_parent_through_the_first_sibling()
    {
        Utterance utt = new();
        Relation r = utt.RelationCreate("SylStructure");
        Item root = r.Append(null);
        Item d1 = Hrg.AddDaughter(root, null)!;
        Item d2 = Hrg.AddDaughter(root, null)!;
        Item d3 = Hrg.AddDaughter(root, null)!;
        Assert.Same(d1, Hrg.Daughter(root));
        Assert.Same(d3, Hrg.LastDaughter(root));
        Assert.Same(d2, Hrg.NthDaughter(root, 1));
        Assert.Null(Hrg.NthDaughter(root, 3));
        Assert.Same(root, Hrg.Parent(d3));
        Assert.Same(root, Hrg.Parent(d1));
        Assert.Null(Hrg.Parent(root));

        // a new first daughter takes over the link to the parent
        Item d0 = Hrg.Prepend(d1, null);
        Assert.Same(d0, Hrg.Daughter(root));
        Assert.Same(root, Hrg.Parent(d0));
        Assert.Same(root, Hrg.Parent(d1));
    }

    [Fact]
    public void Putting_the_same_contents_in_a_relation_twice_moves_the_back_reference()
    {
        Utterance utt = new();
        Relation r = utt.RelationCreate("Word");
        Relation other = utt.RelationCreate("Phrase");
        Item first = r.Append(null);
        Item shown = other.Append(first);
        Assert.Same(shown, Hrg.ItemAs(first, "Phrase"));
        Item again = other.Append(first);
        Assert.Same(again, Hrg.ItemAs(first, "Phrase"));
    }

    [Fact]
    public void Deleting_a_relation_removes_its_items_from_their_contents()
    {
        Utterance utt = new();
        Relation word = utt.RelationCreate("Word");
        Relation phrase = utt.RelationCreate("Phrase");
        Item w = word.Append(null);
        phrase.Append(w);
        Assert.True(utt.RelationDelete("Phrase"));
        Assert.Null(Hrg.ItemAs(w, "Phrase"));
        Assert.False(utt.RelationPresent("Phrase"));
        Assert.Throws<FliteException>(() => utt.GetRelation("Phrase"));
    }

    [Fact]
    public void A_feature_path_walks_the_graph_and_ends_in_a_feature_or_a_function()
    {
        Utterance utt = new();
        Relation seg = utt.RelationCreate("Segment");
        string[] names = ["hh", "ax", "l", "ow"];
        List<Item> items = [];
        foreach (string n in names)
        {
            Item i = seg.Append(null);
            Hrg.SetString(i, "name", n);
            items.Add(i);
        }

        utt.FFunctions.Set("twice", new FunctionValue(item => new StringValue(Hrg.Name(item) + Hrg.Name(item))));
        Item second = items[1];
        Assert.Equal("ax", Values.String(FeaturePath.Ffeature(second, "name")));
        Assert.Equal("hh", Values.String(FeaturePath.Ffeature(second, "p.name")));
        Assert.Equal("ow", Values.String(FeaturePath.Ffeature(second, "n.n.name")));
        Assert.Equal("ow", Values.String(FeaturePath.Ffeature(second, "nn.name")));
        Assert.Equal("ow", Values.String(FeaturePath.Ffeature(items[3], "last.name")));
        Assert.Equal("hh", Values.String(FeaturePath.Ffeature(items[3], "first.name")));
        Assert.Equal("owow", Values.String(FeaturePath.Ffeature(items[2], "n.twice")));
        Assert.Equal("ax", Values.String(FeaturePath.Ffeature(second, "R:Segment.name")));

        // a path that leads nowhere, and a feature that is not there, give the string "0"
        Assert.Equal("0", Values.String(FeaturePath.Ffeature(items[0], "p.name")));
        Assert.Equal("0", Values.String(FeaturePath.Ffeature(items[0], "p.p.name")));
        Assert.Equal("0", Values.String(FeaturePath.Ffeature(second, "nothing")));
        Assert.Equal("0", Values.String(FeaturePath.Ffeature(second, "R:Word.name")));
        Assert.Equal("0", Values.String(FeaturePath.Ffeature(null, "name")));

        // an unknown step gives no value at all
        Assert.Null(FeaturePath.Ffeature(second, "sideways.name"));
        Assert.Same(items[2], FeaturePath.PathToItem(second, "n"));
        Assert.Null(FeaturePath.PathToItem(items[3], "n"));
        Assert.Same(items[0], FeaturePath.PathToItem(second, "p"));
    }

    [Fact]
    public void A_cart_asks_each_path_once_and_follows_yes_to_the_next_row_and_no_to_the_jump()
    {
        // 0: name IS "a" ? yes -> 1 : no -> 3      1: n.name IN (b c) ? yes -> 2 : no -> 4     2: leaf "ab"    3: leaf "x"    4: leaf "a?"
        Cart tree = new(
            [
                new CartNode(0, CartOps.Is, 3, new StringValue("a")),
                new CartNode(1, CartOps.In, 4, new ConsValue(new StringValue("b"), new ConsValue(new StringValue("c"), null))),
                new CartNode(255, CartOps.Leaf, 0, new StringValue("ab")),
                new CartNode(255, CartOps.Leaf, 0, new StringValue("x")),
                new CartNode(255, CartOps.Leaf, 0, new StringValue("a?")),
            ],
            ["name", "n.name"]);

        List<string> asked = [];
        Value? Ask(Func<string, Value?> f)
        {
            asked.Clear();
            return tree.Interpret(f, (path, _) => asked.Add(path));
        }

        Dictionary<string, Value> v = new() { ["name"] = new StringValue("a"), ["n.name"] = new StringValue("c") };
        Assert.Equal("ab", Values.String(Ask(p => v[p])));
        Assert.Equal(["name", "n.name"], asked);

        v["n.name"] = new StringValue("z");
        Assert.Equal("a?", Values.String(Ask(p => v[p])));

        v["name"] = new StringValue("q");
        Assert.Equal("x", Values.String(Ask(p => v[p])));
        Assert.Equal(["name"], asked);
    }

    [Fact]
    public void A_cart_compares_with_the_type_rules_of_the_c_code()
    {
        static Cart One(byte op, Value val) => new(
            [new CartNode(0, op, 2, val), new CartNode(255, CartOps.Leaf, 0, new StringValue("yes")), new CartNode(255, CartOps.Leaf, 0, new StringValue("no"))],
            ["f"]);

        string Run(Cart c, Value v) => Values.String(c.Interpret(_ => v));

        // IS is typed: the int 1 is not the string "1"
        Assert.Equal("no", Run(One(CartOps.Is, new StringValue("1")), new IntValue(1)));
        Assert.Equal("yes", Run(One(CartOps.Is, new StringValue("1")), new StringValue("1")));

        // LESS and GREATER read both sides as floats, so a string "2" is less than 3
        Assert.Equal("yes", Run(One(CartOps.Less, new FloatValue(3f)), new StringValue("2")));
        Assert.Equal("no", Run(One(CartOps.Less, new FloatValue(2f)), new StringValue("2")));
        Assert.Equal("yes", Run(One(CartOps.Greater, new IntValue(2)), new StringValue("2.5")));

        Cart matches = new(One(CartOps.Matches, new IntValue(0)).Rules, ["f"], (index, text) => index == 0 && text.StartsWith('a'));
        Assert.Equal("yes", Run(matches, new StringValue("abc")));
        Assert.Equal("no", Run(matches, new StringValue("xbc")));
        Assert.Throws<FliteException>(() => Run(One(CartOps.Matches, new IntValue(0)), new StringValue("abc")));
        Assert.Throws<FliteException>(() => Run(One(CartOps.EqualsOp, new IntValue(0)), new StringValue("abc")));
    }

    [Fact]
    public void A_cart_asks_again_for_a_path_that_gave_no_value()
    {
        // the C code refuses to cache a null: the second question about the same path asks again
        Cart tree = new(
            [
                new CartNode(0, CartOps.Is, 2, new StringValue("a")),
                new CartNode(255, CartOps.Leaf, 0, new StringValue("yes")),
                new CartNode(0, CartOps.Is, 4, new StringValue("a")),
                new CartNode(255, CartOps.Leaf, 0, new StringValue("yes again")),
                new CartNode(255, CartOps.Leaf, 0, new StringValue("no")),
            ],
            ["f"]);
        int calls = 0;
        Value? answer = tree.Interpret(
            _ =>
            {
                calls++;
                return null;
            });
        Assert.Equal("no", Values.String(answer));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void A_phoneset_gives_the_first_phone_and_feature_for_unknown_names()
    {
        Phoneset ps = new(
            "t",
            ["vc", "ctype"],
            [new StringValue("+"), new StringValue("-"), new StringValue("0"), new StringValue("s")],
            ["aa", "p"],
            "pau",
            [[0, 2], [1, 3]]);
        Assert.Equal("+", ps.PhoneFeatureString("aa", "vc"));
        Assert.Equal("s", ps.PhoneFeatureString("p", "ctype"));

        // phone_id and phone_feat_id answer 0 when they find nothing
        Assert.Equal("+", ps.PhoneFeatureString("zz", "vc"));
        Assert.Equal("-", ps.PhoneFeatureString("p", "nothing"));
        Assert.Equal(2, ps.NumPhones);
    }
}
