namespace Ample.Language.Tests;

public sealed class SetLayoutsTests
{
    private const string Song = "composition \"d\" { ample 1  tempo 92  master-seed 1 }\ndrums a at 1 for 4 {\n}\ndrums b at 1 for 4 {\n}\ndrums c at 1 for 4 {\n}\nlayout a 10 10\n";

    [Fact]
    public void ManyPlacesAreSetInOnePassLikeOneByOne()
    {
        AmpleDocument doc = Parser.Parse(Song, Corpus.Spec, ParseOptions.Editor);
        string once = Edits.Apply(Song, Edits.SetLayouts(doc, [("a", 50, 60), ("b", 70, 80), ("c", 90, 100)]).Edits);
        string text = Song;
        foreach ((string block, int x, int y) in new[] { ("a", 50, 60), ("b", 70, 80), ("c", 90, 100) })
        {
            text = Edits.Apply(text, Edits.SetLayout(Parser.Parse(text, Corpus.Spec, ParseOptions.Editor), block, x, y).Edits);
        }

        Assert.Equal(text, once);
        Assert.Contains("layout a 50 60", once);
        Assert.Contains("layout c 90 100", once);
        Assert.False(Edits.SetLayouts(doc, [("ghost", 1, 1)]).Ok);
    }
}
