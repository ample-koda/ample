using System.Text.Json.Nodes;
using Ample.Core.Abstractions;
using Ample.Language;

namespace Ample.Server.Tests;

public sealed class AiEditTests
{
    private const string Song = "project demo { tempo 100  master-seed 7 }\n# the beat\nmidi-clip chords at 1 for 4 {\n  chords Am F C G\n}\nmixer final {\n  from chords.notes -> audio1\n}\nmaster master {\n  from final.audio -> audio\n}\n";

    private static SpecData Spec()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        string spec = Path.Combine(dir ?? throw new InvalidOperationException("AMPLE root not found."), "spec");
        return SpecData.FromFiles(Directory.GetFiles(spec, "*.json", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(spec, f).Replace('\\', '/'), File.ReadAllText));
    }

    private static JsonArray Edits(string json) => JsonNode.Parse(json)!.AsArray();

    [Fact]
    public void Edits_change_parameters_blocks_and_connections_without_regular_expressions()
    {
        SpecData spec = Spec();
        AiEdit.Result p = AiEdit.Apply(Song, Edits("""[{"op":"set_param","block":"chords","name":"chords","value":"Dm G C"},{"op":"set_param","block":"chords","name":"swing","value":"0.1"}]"""), spec);
        Assert.Contains("  chords Dm G C", p.Text, StringComparison.Ordinal);
        Assert.Contains("  swing 0.1", p.Text, StringComparison.Ordinal);

        AiEdit.Result r = AiEdit.Apply(Song, Edits("""[{"op":"insert","before":"# the beat","text":"# a note"},{"op":"disconnect","from_block":"chords","from_output":"notes","to_block":"final","to_input":"audio1"},{"op":"remove_block","block":"chords"}]"""), spec);
        Assert.Contains("# a note", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("midi-clip chords", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("from chords.notes", r.Text, StringComparison.Ordinal);
        Assert.Equal(3, r.Summary.Count);

        AiEdit.Result c = AiEdit.Apply(Song, Edits("""[{"op":"connect","from_block":"chords","from_output":"notes","to_block":"master","to_input":"extra"},{"op":"add_block","text":"reverb room {\n  from final.audio -> audio\n}","after_block":"final"}]"""), spec);
        Assert.Contains("from chords.notes -> extra", c.Text, StringComparison.Ordinal);
        Assert.Contains("reverb room {", c.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_is_disabled_and_enabled_again_with_the_header_flag()
    {
        SpecData spec = Spec();
        AiEdit.Result off = AiEdit.Apply(Song, Edits("""[{"op":"set_flag","block":"chords","flag":"disabled"}]"""), spec);
        Assert.Contains("midi-clip chords at 1 for 4 disabled {", off.Text, StringComparison.Ordinal);
        Assert.NotEqual(Song, off.Text);
        AiEdit.Result on = AiEdit.Apply(off.Text, Edits("""[{"op":"set_flag","block":"chords","flag":"disabled","on":false}]"""), spec);
        Assert.Equal(Song, on.Text);
        Assert.Throws<AmpleException>(() => AiEdit.Apply(Song, Edits("""[{"op":"set_flag","block":"chords","flag":"enabled"}]"""), spec));
        AiEdit.Result plain = AiEdit.Apply(Song, Edits("""[{"op":"set_flag","block":"final","flag":"disabled"},{"op":"set_flag","block":"final","flag":"disabled"}]"""), spec);
        Assert.Contains("mixer final disabled {", plain.Text, StringComparison.Ordinal);
        Assert.Equal(1, plain.Text.Split("disabled").Length - 1);   // twice is still once
    }

    [Fact]
    public void A_replace_with_the_same_text_is_refused_and_disabling_names_what_else_goes_quiet()
    {
        SpecData spec = Spec();
        AmpleException same = Assert.Throws<AmpleException>(() => AiEdit.Apply(Song, Edits("""[{"op":"replace","old":"chords Am F C G","new":"chords Am F C G"}]"""), spec));
        Assert.Contains("same text", same.Message, StringComparison.Ordinal);

        AiEdit.Result source = AiEdit.Apply(Song, Edits("""[{"op":"set_flag","block":"chords","flag":"disabled"}]"""), spec);
        Assert.Contains(source.Summary, x => x.StartsWith("WARNING:", StringComparison.Ordinal) && x.Contains("final", StringComparison.Ordinal));   // the mixer reads from it
        AiEdit.Result mixer = AiEdit.Apply(Song, Edits("""[{"op":"set_flag","block":"final","flag":"disabled"}]"""), spec);
        Assert.Contains(mixer.Summary, x => x.StartsWith("WARNING:", StringComparison.Ordinal) && x.Contains("mixer", StringComparison.Ordinal));
        AiEdit.Result enable = AiEdit.Apply(source.Text, Edits("""[{"op":"set_flag","block":"chords","flag":"disabled","on":false}]"""), spec);
        Assert.DoesNotContain(enable.Summary, x => x.StartsWith("WARNING:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_block_written_with_backslash_n_instead_of_line_breaks_still_lands_as_separate_lines()
    {
        // the model sent the two characters backslash and n (a double-escaped line break) instead of a real line break
        AiEdit.Result r = AiEdit.Apply(Song, Edits("""[{"op":"add_block","text":"reverb room {\\n  size 50%\\n  from chords.notes -> audio\\n}"}]"""), Spec());
        Assert.DoesNotContain("\\n", r.Text, StringComparison.Ordinal);
        Assert.Contains("reverb room {\n  size 50%\n  from chords.notes -> audio\n}", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_edit_that_does_not_fit_stops_the_whole_call_and_says_why()
    {
        SpecData spec = Spec();
        AmpleException none = Assert.Throws<AmpleException>(() => AiEdit.Apply(Song, Edits("""[{"op":"replace","old":"chords Am F C G","new":"x"},{"op":"replace","old":"does not exist","new":"y"}]"""), spec));
        Assert.Contains("Edit 2 did not fit, so nothing was changed", none.Message, StringComparison.Ordinal);
        AmpleException many = Assert.Throws<AmpleException>(() => AiEdit.Apply(Song, Edits("""[{"op":"replace","old":"chords","new":"x"}]"""), spec));
        Assert.Contains("found 3 times", many.Message, StringComparison.Ordinal);
        Assert.Equal(3, AiEdit.Apply(Song, Edits("""[{"op":"replace","old":"chords","new":"chordz","all":true}]"""), spec).Text.Split("chordz").Length - 1);
        Assert.Throws<AmpleException>(() => AiEdit.Apply(Song, Edits("""[{"op":"set_param","block":"nope","name":"a","value":"b"}]"""), spec));
        Assert.Throws<AmpleException>(() => AiEdit.Apply(Song, Edits("""[{"op":"shell","cmd":"rm"}]"""), spec));
        Assert.Contains("\r\n", AiEdit.Apply(Song.Replace("\n", "\r\n", StringComparison.Ordinal), Edits("""[{"op":"insert","after":"# the beat","text":"# more"}]"""), spec).Text, StringComparison.Ordinal);
    }
}
