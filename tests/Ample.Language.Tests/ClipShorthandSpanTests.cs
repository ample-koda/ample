namespace Ample.Language.Tests;

// A clip written with melody is as long as its melody, trailing rests and a silent melody included: the pipeline places what follows after that length (2026-11-18).
public sealed class ClipShorthandSpanTests
{
    private const string Header = "composition \"x\" { ample 1  tempo 120  master-seed 1 }\n\n";

    private static string NotesOf(string clips, string name)
    {
        var result = Parser.Parse(Header + clips, Corpus.Spec, new ParseOptions { ApplyNoteFlow = true });
        return result.Blocks.Single(b => b.Name == name).Parameters.Single(p => p.Name == "notes").Value.Trim('"');
    }

    [Theory]
    [InlineData("melody \"c4:1 r:7\"", "1:0:1:60:96 3:0:1:60:96")]             // 8 beats: two bars, so the repeat starts in bar 3
    [InlineData("melody \"c4:1 r:3\"", "1:0:1:60:96 2:0:1:60:96")]             // 4 beats: one bar
    [InlineData("melody \"c4:1 r:7\"\n  bars 3", "1:0:1:60:96 4:0:1:60:96")]   // bars said: three bars, whatever the melody says
    public void A_melody_clip_repeats_after_its_whole_length_trailing_rests_included(string shorthand, string expected)
    {
        Assert.Equal(expected, NotesOf($"midi-clip theme {{\n  {shorthand}\n}}\n\nmidi-clip copy at 1 for auto {{\n  notes theme.notes |> repeat(2)\n}}\n", "copy"));
    }

    [Fact]
    public void A_silent_melody_is_a_bar_long_not_empty()
    {
        string notes = NotesOf("midi-clip rest {\n  melody \"r:4\"\n}\n\nmidi-clip one {\n  notes \"1:0:1:60:96\"\n}\n\nmidi-clip both at 1 for auto {\n  notes rest.notes |> concat(one.notes)\n}\n", "both");
        Assert.Equal("2:0:1:60:96", notes);
    }

    [Theory]
    [InlineData("c4:1 r:3", 4.0)]
    [InlineData("mp c4:2 < d4:1 f", 3.0)]   // the markers and the hairpin take no time
    [InlineData("r:4 r:4", 8.0)]
    public void MelodyBeats_counts_every_token_that_takes_time(string melody, double beats)
    {
        Assert.Equal(beats, ClipShorthand.MelodyBeats(melody));
    }
}
