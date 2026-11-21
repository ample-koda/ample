using Ample.Speech.Flite.Core;

namespace Ample.Speech.Tests.Flite;

/// <summary>
/// Flite M2 parity with the reference build of the C program (commit 6c9f20dc): the six English trees, answered from the features that the reference
/// recorded, and the languages of the two compiled regular expressions. The recorded calls come from synthesising the 341 lines of the corpus.
/// </summary>
public sealed class FliteReferenceParityTests
{
    private static readonly Lazy<FliteReferenceFixtures.Golden> Golden = new(FliteReferenceFixtures.LoadCarts);

    [Fact]
    public void The_six_trees_and_the_phoneset_are_read_whole()
    {
        FliteReferenceFixtures.Golden g = Golden.Value;
        Assert.Equal(["accent", "durz", "nums", "phrasing", "pos", "tone"], g.Trees.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(1598, g.Trees.Values.Sum(t => t.Rules.Length));
        Assert.Equal(50, g.Phoneset.NumPhones);
        Assert.Equal(8, g.Phoneset.FeatNames.Length);
        Assert.Equal("pau", g.Phoneset.Silence);
        Assert.Equal("+", g.Phoneset.PhoneFeatureString("aa", "vc"));
        Assert.Equal("s", g.Phoneset.PhoneFeatureString("p", "ctype"));
        Assert.All(g.Trees.Values, t => Assert.Equal(CartOps.Leaf, t.Rules[^1].Op));
    }

    [Fact]
    public void Every_recorded_tree_call_gets_the_answer_of_the_reference_from_the_same_questions()
    {
        FliteReferenceFixtures.Golden g = Golden.Value;
        int calls = 0;
        foreach (FliteReferenceFixtures.TreeCall call in FliteReferenceFixtures.LoadCalls())
        {
            calls++;
            Cart tree = g.Trees[call.Tree];
            int next = 0;
            Value? answer = tree.Interpret(path =>
            {
                Assert.True(next < call.Asked.Count, $"{call.Tree} call {calls}: asked {path} after the {call.Asked.Count} features of the reference");
                Assert.Equal(call.Asked[next].Path, path);
                return call.Asked[next++].Value;
            });

            Assert.True(next == call.Asked.Count, $"{call.Tree} call {calls}: asked {next} features, the reference {call.Asked.Count}");
            Assert.True(Values.Equal(call.Answer, answer), $"{call.Tree} call {calls}: answer differs from the reference");
            Assert.Equal(call.Answer!.TypeCode, answer!.TypeCode);
        }

        Assert.Equal(7766, calls);
    }

    [Fact]
    public void The_digits_and_double_expressions_have_the_language_of_the_compiled_programs()
    {
        FliteReferenceFixtures.Golden g = Golden.Value;
        Assert.True(g.Probes.Count >= 45);

        Utterance utt = new();
        FeatureFunctions.Register(utt.FFunctions, []);
        Relation token = utt.RelationCreate("Token");
        foreach (FliteReferenceFixtures.RegexProbe probe in g.Probes)
        {
            Item t = token.Append(null);
            Hrg.SetString(t, "name", probe.Text);
            string expected = probe.Digits ? "numeric" : probe.Double ? "number" : "_other_";
            Assert.True(expected == Values.String(FeatureFunctions.TokenPosGuess(t)), $"'{probe.Text}': digits {probe.Digits}, double {probe.Double}");
        }
    }
}
