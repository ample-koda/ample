namespace Ample.Language.Tests;

/// <summary>The musical types (docs/language/15): the typed catalogue, the static checker with its diagnostics and fixes, and the typed spellings (+7st, -1oct, 25%, C2) that mean the same as the plain ones.</summary>
public sealed class NoteTypesTests
{
    private static IReadOnlyList<string> Ids(string expression) => [.. NotePipeline.CheckTypes(expression).Select(i => i.Id)];

    private static NotePipeline.Context Context() => new() { Tempo = "120", MasterSeed = 7 };

    private static string Notes(string expression)
    {
        PipeResult result = NotePipeline.Evaluate(expression, Context());
        Assert.Empty(result.Errors);
        return result.Notes!;
    }

    [Fact]
    public void The_catalogue_has_a_typed_signature_for_every_operator_and_value_function()
    {
        string[] names = ["bars", "beats", "time", "mask", "remove", "cut", "replace", "transpose", "shift", "concat", "merge", "reverse", "stretch", "legato", "quantize", "humanize", "repeat", "arp", "where", "euclid", "randomize", "thin", "scramble", "clamp", "fold", "snap", "keep", "avoid", "walk", "chords", "scale", "random", "choose", "chance", "stable", "random-seed", "bake"];
        foreach (string name in names) Assert.True(NoteCatalog.Signatures.ContainsKey(name), name);
        foreach (NoteSignature sig in NoteCatalog.Signatures.Values)
        {
            foreach (NoteParam p in sig.Params) Assert.True(NoteCatalog.Types.ContainsKey(p.Type), sig.Id + "." + p.Name + ": " + p.Type);
        }
    }

    [Fact]
    public void Valid_calls_have_no_issues()
    {
        Assert.Empty(Ids("walk(C, minor, steps=16, step=0.5beat, octave=5, seed=stable(\"melody\"))"));
        Assert.Empty(Ids("walk(F#, dorian, step=250ms, seed=7)"));
        Assert.Empty(Ids("chords(\"Cm Ab Bb Cm\", length=1bar, octave=3, velocity=70)"));
        Assert.Empty(Ids("euclid(3, 8, pitch=C2, step=0.25beat)"));
        Assert.Empty(Ids("1:0:1:60:100 |> transpose(+7st) |> shift(0.5beat) |> thin(25%, seed=1) |> bars(1..=2)"));
        Assert.Empty(Ids("1:0:1:60:100 |> humanize(timing=20ms, velocity=8, seed=stable(\"a\").fork(\"b\"))"));
    }

    [Fact]
    public void Bad_arguments_are_found_before_anything_runs_each_with_its_own_code()
    {
        IReadOnlyList<PipeIssue> issues = NotePipeline.CheckTypes("walk(123, \"minor\", steps=\"sixteen\", step=7, octave=5.5, seed=stable(\"m\"))");
        Assert.Contains(issues, i => i.Id == "type-mismatch" && i.Message.Contains("123", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Id == "quoted-word" && i.Suggestion == "minor");
        Assert.Contains(issues, i => i.Id == "type-mismatch" && i.Message.Contains("sixteen", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Id == "missing-unit" && i.Message.Contains('7'));
        Assert.Contains(issues, i => i.Id == "not-whole" && i.Suggestion == "6");   // half to even: 5.5 -> 6
    }

    [Fact]
    public void A_vocabulary_word_that_is_close_gets_a_suggestion_and_a_far_one_lists_the_choices()
    {
        PipeIssue near = Assert.Single(NotePipeline.CheckTypes("walk(C, mnor, seed=1)"));
        Assert.Equal("minor", near.Suggestion);
        PipeIssue far = Assert.Single(NotePipeline.CheckTypes("walk(C, banana, seed=1)"));
        Assert.Null(far.Suggestion);
        Assert.Contains("one of:", far.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ranges_refinements_dimensions_and_chords_are_checked()
    {
        Assert.Contains("type-mismatch", Ids("euclid(3, 8, velocity=300)"));
        Assert.Contains("type-mismatch", Ids("euclid(0, 0)"));
        Assert.Contains("wrong-dimension", Ids("1:0:1:60:100 |> shift(3st)"));
        Assert.Contains("wrong-dimension", Ids("1:0:1:60:100 |> transpose(2bars)"));
        Assert.Contains("missing-unit", Ids("1:0:1:60:100 |> shift(2)"));
        Assert.Contains("fractional-interval", Ids("1:0:1:60:100 |> transpose(+50cents)"));
        Assert.Contains("type-mismatch", Ids("1:0:1:60:100 |> thin(2, seed=1)"));
        Assert.Contains("type-mismatch", Ids("1:0:1:60:100 |> thin(0.5, seed=1, per=sometimes)"));
        PipeIssue chord = Assert.Single(NotePipeline.CheckTypes("chords(\"Cm Ab Xq Cm\")"));
        Assert.Equal(17 - 3, chord.Start);   // the offset of the bad token inside the text
        Assert.Equal("type-mismatch", chord.Id);
    }

    [Fact]
    public void Typed_spellings_mean_the_same_as_the_plain_ones()
    {
        string source = "1:0:1:60:100 1:1:1:62:100 1:2:1:64:100 1:3:1:65:100";
        Assert.Equal(Notes(source + " |> transpose(+7)"), Notes(source + " |> transpose(+7st)"));
        Assert.Equal(Notes(source + " |> transpose(-12)"), Notes(source + " |> transpose(-1oct)"));
        Assert.Equal(Notes(source + " |> transpose(+2)"), Notes(source + " |> transpose(200cents)"));
        Assert.Equal(Notes(source + " |> thin(0.25, seed=1)"), Notes(source + " |> thin(25%, seed=1)"));
        Assert.Equal(Notes("euclid(3, 8, pitch=36)"), Notes("euclid(3, 8, pitch=C2)"));
        Assert.Equal(Notes("euclid(3, 8, pitch=63)"), Notes("euclid(3, 8, pitch=Eb4)"));
    }

    [Fact]
    public void Note_names_follow_the_midi_convention()
    {
        Assert.True(NotePipeline.TryNoteName("C4", out int c4));
        Assert.Equal(60, c4);
        Assert.True(NotePipeline.TryNoteName("A0", out int a0));
        Assert.Equal(21, a0);
        Assert.True(NotePipeline.TryNoteName("Bb2", out int bb2));
        Assert.Equal(46, bb2);
        Assert.False(NotePipeline.TryNoteName("H2", out _));
        Assert.False(NotePipeline.TryNoteName("C10", out _));
    }

    [Fact]
    public void No_valid_case_of_the_corpus_has_a_type_issue()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(Corpus.Root, "spec-tests", "note-pipelines"), "core.json"))
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            foreach (System.Text.Json.JsonElement c in doc.RootElement.GetProperty("cases").EnumerateArray())
            {
                if (c.GetProperty("expect").TryGetProperty("errors", out System.Text.Json.JsonElement errors) && errors.GetArrayLength() > 0) continue;
                string expr = c.GetProperty("expr").GetString()!;
                Assert.True(NotePipeline.CheckTypes(expr).Count == 0, c.GetProperty("id").GetString() + ": " + string.Join("; ", NotePipeline.CheckTypes(expr).Select(i => i.Message)));
            }
        }
    }

    [Fact]
    public void A_song_reports_the_type_problems_with_codes_and_a_fix()
    {
        string song = "composition \"x\" { ample 1  tempo 120 }\n\nmidi-clip m at 1 for auto {\n  notes walk(C, \"minor\", seed=7)\n}\n";
        var result = Parser.Parse(song, Corpus.Spec);
        Diagnostic quoted = Assert.Single(result.Diagnostics, d => d.Code == "AMP2045");
        Assert.NotNull(quoted.Fix);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "AMP2024");   // the evaluation does not repeat what the checker explained
    }

    private const string Melody = "composition \"x\" { ample 1  tempo 120  master-seed 1 }\n\nmidi-clip melody at 1 for auto {\n  notes walk(C, minor, steps=16, step=0.5beat, octave=5, seed=7)\n}\n\n";

    private static string Player(string length) => "gm play at 1 for " + length + " {\n  file pack:musescore-general\n  from melody.notes -> notes\n}\n";

    [Fact]
    public void An_instrument_with_for_auto_takes_the_length_of_the_notes_that_feed_it()
    {
        var result = Parser.Parse(Melody + Player("auto"), Corpus.Spec);
        Assert.Equal(2, result.Blocks.Single(b => b.Name == "play").Parts[0].Length);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code is "AMP2036" or "AMP2049");
    }

    [Fact]
    public void A_fixed_length_that_differs_from_a_known_span_gets_an_info_with_the_auto_fix_and_a_matching_one_does_not()
    {
        var different = Parser.Parse(Melody + Player("4"), Corpus.Spec);
        Diagnostic info = Assert.Single(different.Diagnostics, d => d.Code == "AMP2049");
        Assert.Equal(Severity.Info, info.Severity);
        Assert.NotNull(info.Fix);
        Assert.DoesNotContain(Parser.Parse(Melody + Player("2"), Corpus.Spec).Diagnostics, d => d.Code == "AMP2049");
    }

    private const string Song = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\nmidi score {\n  file scores/a.mid\n  bars 18\n}\n\n";

    [Fact]
    public void A_render_without_a_length_that_is_fed_by_a_longer_midi_is_warned_with_the_for_auto_fix_which_then_takes_the_length_of_the_midi()
    {
        var cut = Parser.Parse(Song + "midi-render sound {\n  from score.sequence -> sequence\n}\n", Corpus.Spec);
        Diagnostic warning = Assert.Single(cut.Diagnostics, d => d.Code == "AMP2089");
        Assert.Equal(Severity.Warning, warning.Severity);
        Assert.NotNull(warning.Fix);
        var auto = Parser.Parse(Song + "midi-render sound at 1 for auto {\n  from score.sequence -> sequence\n}\n", Corpus.Spec);
        Assert.DoesNotContain(auto.Diagnostics, d => d.Code is "AMP2089" or "AMP2036");
        Assert.Equal(18, auto.Blocks.Single(b => b.Name == "sound").Parts[0].Length);
    }

    [Fact]
    public void The_span_class_says_what_is_known_without_running_the_notes()
    {
        Assert.Equal("known", NotePipeline.SpanClass("1:0:1:60:100"));
        Assert.Equal("derived", NotePipeline.SpanClass("walk(C, minor, steps=16, step=0.5beat, seed=1) |> transpose(+7)"));
        Assert.Equal("unresolved", NotePipeline.SpanClass("theme.notes |> shift(1beat)"));
        Assert.Equal("unresolved", NotePipeline.SpanClass("walk(C, minor, seed=1) |> merge(theme.notes)"));
    }
}
