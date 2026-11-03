namespace Ample.Language.Tests;

public sealed class PianoRollTieTests
{
    [Fact]
    public void Bar_split_ties_keep_inherited_instrument_routing_and_round_trip_the_expanded_form()
    {
        const string text = "1:3:1:60:96;tie=17 2:0:4:60:96;tie=17";
        Assert.True(StructuredMidiNotes.TryParse(text, out IReadOnlyList<StructuredMidiNote> notes));
        Assert.All(notes, note => { Assert.Equal(17, note.TieId); Assert.Equal(-1, note.Channel); Assert.Equal(-1, note.Program); });
        Assert.True(ExpandedMidiNotes.TryExpand(text, 1_000_000, out string expanded));
        Assert.True(ExpandedMidiNotes.TryCompact(expanded, 1_000_000, out string compact));
        Assert.Equal(text, compact);
        string source = "composition \"ties\" { ample 1  tempo 120 master-seed 1 }\nmidi-clip clip {\n notes \"" + text + "\"\n}\n";
        Assert.DoesNotContain(Parser.Parse(source, Corpus.Spec).Diagnostics, diagnostic => diagnostic.Severity == Severity.Error);
    }

    [Theory]
    [InlineData("1:0:1:60:96;tie=-1")]
    [InlineData("1:0:1:60:96;tie=2000001")]
    [InlineData("1:0:1:60:96;tie=1;tie=2")]
    [InlineData("1:0:1:60:96:0:0:0:1;tie=1")]
    public void Invalid_or_ambiguous_tie_identity_is_rejected(string text)
        => Assert.False(StructuredMidiNotes.TryParse(text, out _));
}
