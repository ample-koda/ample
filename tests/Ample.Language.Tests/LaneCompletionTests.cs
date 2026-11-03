namespace Ample.Language.Tests;

/// <summary>The completions of the connections: the inputs that still take a source, the ones that fit, and the lanes offered after a dash.</summary>
public sealed class LaneCompletionTests
{
    private const string Head = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n";

    private static IReadOnlyList<CompletionItem> At(string song, int line, int col) => Queries.CompletionAt(Parser.Parse(song, Corpus.Spec), Corpus.Spec, line, col);

    private static (string Song, int Line, int Col) Mixer(string last)
    {
        string song = Head + "synth a at 1 for 2 {\n  wave sine\n}\n\nsynth b at 1 for 2 {\n  wave saw\n}\n\nmixer m {\n  from a.audio -> in1\n" + last + "\n}\n";
        int line = song.Split('\n').Length - 2;   // the last line before the closing brace
        return (song, line, last.TrimEnd('\n').Length + 1);
    }

    [Fact]
    public void After_the_arrow_only_the_free_inputs_of_the_right_type_are_offered()
    {
        (string song, int line, int col) = Mixer("  from b.audio -> ");
        IReadOnlyList<string> names = [.. At(song, line, col).Where(c => c.Kind == "input").Select(c => c.Label)];
        Assert.Contains("in2", names);
        Assert.DoesNotContain("in1", names);   // it has a source already
    }

    [Fact]
    public void A_dash_after_an_output_offers_the_arrow_with_each_free_input()
    {
        (string song, int line, int col) = Mixer("  from b.audio -");
        IReadOnlyList<CompletionItem> lanes = [.. At(song, line, col).Where(c => c.Kind == "lane")];
        Assert.Contains(lanes, c => c.Label == "-> in2" && c.Insert == "-> in2");
        Assert.DoesNotContain(lanes, c => c.Label == "-> in1");
    }

    [Fact]
    public void A_dash_on_a_line_of_its_own_offers_connections_that_fit()
    {
        (string song, int line, int col) = Mixer("  -");
        IReadOnlyList<CompletionItem> lanes = [.. At(song, line, col).Where(c => c.Kind == "lane")];
        Assert.Contains(lanes, c => c.Insert == "from b.audio -> in2");
        Assert.DoesNotContain(lanes, c => c.Insert!.EndsWith("-> in1", StringComparison.Ordinal));   // taken
    }
}
