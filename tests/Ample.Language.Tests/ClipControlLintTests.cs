namespace Ample.Language.Tests;

/// <summary>The lint of the controllers of a midi-clip, while writing: a value it cannot play (L013) and controllers only the gm block plays reaching another instrument (L012).</summary>
public sealed class ClipControlLintTests
{
    private const string Head = "composition \"t\" { ample 1  tempo 120  master-seed 1 }\n\n";

    private static AmpleDocument Parse(string text) => Parser.Parse(text, Corpus.Spec);

    private static string Song(string controls, string instrument) => Head
        + "midi-clip tune at 1 for 1 {\n  notes 1:0:1:60:100\n" + controls + "}\n\n"
        + "transpose steps {\n  from tune.notes -> notes\n}\n\n"
        + instrument + " lead at 1 for 1 {\n" + (instrument == "gm" ? "  file pack:musescore-general\n" : string.Empty) + "  from steps.notes -> notes\n}\n\n"
        + "master out {\n  from lead.audio -> audio\n}\n";

    [Fact]
    public void A_value_a_controller_cannot_play_is_an_error_while_writing()
    {
        AmpleDocument doc = Parse(Song("  volume loud\n  cc \"74:20\"\n", "gm"));
        Assert.Equal(2, doc.Diagnostics.Count(d => d.Code == "L013"));
        Assert.DoesNotContain(Parse(Song("  volume auto(1:0, 2:-12dB)\n  sustain on\n  cc \"74=20 67=127\"\n", "gm")).Diagnostics, d => d.Code == "L013");
    }

    [Fact]
    public void Controllers_only_gm_plays_warn_on_another_instrument_even_through_note_blocks()
    {
        AmpleDocument synth = Parse(Song("  modwheel 50\n  volume -6\n", "synth"));
        Diagnostic warning = Assert.Single(synth.Diagnostics, d => d.Code == "L012");
        Assert.Contains("modwheel", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("volume", warning.Message.Split(';')[1], StringComparison.Ordinal);
        Assert.DoesNotContain(Parse(Song("  modwheel 50\n", "gm")).Diagnostics, d => d.Code == "L012");
        Assert.DoesNotContain(Parse(Song("  volume -6\n  pan 20\n", "synth")).Diagnostics, d => d.Code == "L012");
    }
}
