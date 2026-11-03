namespace Ample.Language.Tests;

/// <summary>The note pipelines in a document: bake, stale and edited stages, detach, the editable pieces, for auto, the limits and the sources.</summary>
public sealed class NoteBakeTests
{
    private const string Header = "composition \"x\" { ample 1  tempo 120  master-seed 1 }\n\n";
    private const string Source = "midi-clip a at 1 for 1 {\n  notes \"1:0:1:60:100 1:1:1:62:100 1:2:1:64:100\"\n}\n\n";

    private static string Song(string expression, string length = "auto") => Header + Source + $"midi-clip b at 2 for {length} {{\n  notes {expression}\n}}\n";

    private static IReadOnlyList<string> Codes(string text, ParseOptions? options = null) => [.. Parser.Parse(text, Corpus.Spec, options).Diagnostics.Select(d => d.Code)];

    private static string Baked(string expression = "a.notes\n    |> transpose(+3)\n    |> bake s") =>
        NoteBake.Bake(Song(expression), Corpus.Spec, "b", "s", null, null, 100_000).Text;

    [Theory]
    [InlineData("take.notes", true)]
    [InlineData("(a.notes)", true)]
    [InlineData("1:0:1:60:100", false)]
    [InlineData("\"1:0:1:60:100\"", false)]
    [InlineData("__invalid_expanded_notes__", false)]
    [InlineData("# just a comment\n a.notes", true)]
    public void An_expression_starts_with_a_word_and_a_literal_with_a_note(string value, bool expected) =>
        Assert.Equal(expected, NotePipeline.IsExpression(value));

    [Fact]
    public void A_fresh_bake_is_signed_and_neither_stale_nor_edited()
    {
        BakeOutcome outcome = NoteBake.Bake(Song("a.notes\n    |> transpose(+3)\n    |> bake s"), Corpus.Spec, "b", "s", null, null, 100_000);
        Assert.Equal(3, outcome.Notes);
        Assert.Contains("notes-hash", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("input-hash", outcome.Text, StringComparison.Ordinal);
        Assert.Contains("1:0:1:63:100", outcome.Text, StringComparison.Ordinal);
        IReadOnlyList<string> codes = Codes(outcome.Text);
        Assert.DoesNotContain("AMP2030", codes);
        Assert.DoesNotContain("AMP2031", codes);
    }

    [Fact]
    public void Baking_twice_gives_the_same_text()
    {
        string once = Baked();
        string twice = NoteBake.Bake(once, Corpus.Spec, "b", "s", null, null, 100_000).Text;
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Changing_the_pipeline_after_the_bake_makes_the_stage_stale_and_the_build_keeps_the_stored_notes()
    {
        string stale = Baked().Replace("transpose(+3)", "transpose(+5)", StringComparison.Ordinal);
        Assert.Contains("AMP2030", Codes(stale));
        AmpleDocument doc = Parser.Parse(stale, Corpus.Spec, new ParseOptions { ApplyNoteFlow = true });
        Assert.Equal("1:0:1:63:100 1:1:1:65:100 1:2:1:67:100", doc.Blocks.Single(b => b.Name == "b").Parameters.Single(p => p.Name == "notes").Value);
        Assert.DoesNotContain("AMP2030", Codes(NoteBake.Bake(stale, Corpus.Spec, "b", "s", null, null, 100_000).Text));
    }

    [Fact]
    public void Editing_the_baked_notes_by_hand_is_a_warning_and_with_strict_an_error()
    {
        string edited = Baked().Replace("1:0:1:63:100", "1:0:1:65:100", StringComparison.Ordinal);
        Diagnostic warning = Parser.Parse(edited, Corpus.Spec).Diagnostics.Single(d => d.Code == "AMP2031");
        Assert.Equal(Severity.Warning, warning.Severity);
        string strict = edited.Replace("bake s {", "bake s {\n      strict", StringComparison.Ordinal);
        Assert.Equal(Severity.Error, Parser.Parse(strict, Corpus.Spec).Diagnostics.Single(d => d.Code == "AMP2031").Severity);
    }

    [Fact]
    public void A_bake_in_the_middle_runs_the_steps_after_it_on_top_of_the_stored_notes()
    {
        string text = NoteBake.Bake(Song("a.notes\n    |> transpose(+3)\n    |> bake s\n    |> transpose(+1)"), Corpus.Spec, "b", "s", null, null, 100_000).Text;
        AmpleDocument doc = Parser.Parse(text, Corpus.Spec, new ParseOptions { ApplyNoteFlow = true });
        Assert.Equal("1:0:1:64:100 1:1:1:66:100 1:2:1:68:100", doc.Blocks.Single(b => b.Name == "b").Parameters.Single(p => p.Name == "notes").Value);
        Assert.DoesNotContain("AMP2030", Codes(text));
    }

    [Fact]
    public void A_bake_without_a_stage_adds_one()
    {
        string text = NoteBake.Bake(Song("a.notes |> transpose(+3)"), Corpus.Spec, "b", null, null, null, 100_000).Text;
        Assert.Contains("bake baked", text, StringComparison.Ordinal);
        Assert.Contains("notes-hash", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Detach_turns_the_pipeline_into_plain_notes()
    {
        string text = NoteBake.Detach(Song("a.notes |> transpose(+3)"), Corpus.Spec, "b", null, 100_000).Text;
        Parameter notes = Parser.Parse(text, Corpus.Spec).Blocks.Single(b => b.Name == "b").Parameters.Single(p => p.Name == "notes");
        Assert.False(NotePipeline.IsExpression(notes.Value));
        Assert.Equal("1:0:1:63:100 1:1:1:65:100 1:2:1:67:100", notes.Value);
    }

    [Fact]
    public void A_bake_can_keep_its_notes_in_a_file()
    {
        BakeOutcome outcome = NoteBake.Bake(Song("a.notes\n    |> transpose(+3)\n    |> bake s"), Corpus.Spec, "b", "s", "bakes/b.notes", null, 100_000);
        Assert.Equal("bakes/b.notes", outcome.FilePath);
        Assert.Equal("1:0:1:63:100 1:1:1:65:100 1:2:1:67:100\n", outcome.FileContent);
        Assert.DoesNotContain("1:0:1:63:100", outcome.Text, StringComparison.Ordinal);
        ParseOptions withFile = new() { ReadFile = path => path == "bakes/b.notes" ? outcome.FileContent : null };
        IReadOnlyList<string> codes = Codes(outcome.Text, withFile);
        Assert.DoesNotContain("AMP2033", codes);
        Assert.DoesNotContain("AMP2030", codes);
        Assert.DoesNotContain("AMP2031", codes);
        Assert.Contains("AMP2033", Codes(outcome.Text));
    }

    [Fact]
    public void A_bake_file_that_was_edited_is_reported_as_edited()
    {
        BakeOutcome outcome = NoteBake.Bake(Song("a.notes\n    |> transpose(+3)\n    |> bake s"), Corpus.Spec, "b", "s", "bakes/b.notes", null, 100_000);
        ParseOptions edited = new() { ReadFile = _ => "1:0:1:64:100 1:1:1:65:100 1:2:1:67:100\n" };
        Assert.Contains("AMP2031", Codes(outcome.Text, edited));
    }

    [Fact]
    public void An_editable_piece_is_rewritten_without_touching_the_rest()
    {
        string song = Song("a.notes\n    |> concat(\n         editable {\n           notes 1:0:1:72:100\n         }\n       )\n    |> transpose(+1)");
        string text = NoteBake.SetEditable(song, Corpus.Spec, "b", 0, "1:0:1:74:100 1:1:1:76:100");
        Assert.Contains("1:0:1:74:100", text, StringComparison.Ordinal);
        Assert.DoesNotContain("1:0:1:72:100", text, StringComparison.Ordinal);
        Assert.Contains("|> transpose(+1)", text, StringComparison.Ordinal);
        Assert.StartsWith(Header + Source, text, StringComparison.Ordinal);
        AmpleDocument doc = Parser.Parse(text, Corpus.Spec, new ParseOptions { ApplyNoteFlow = true });
        Assert.Equal("1:0:1:61:100 1:1:1:63:100 1:2:1:65:100 2:0:1:75:100 2:1:1:77:100", doc.Blocks.Single(b => b.Name == "b").Parameters.Single(p => p.Name == "notes").Value);
    }

    [Fact]
    public void An_editable_piece_that_does_not_exist_is_refused()
    {
        NoteBakeException e = Assert.Throws<NoteBakeException>(() => NoteBake.SetEditable(Song("a.notes |> transpose(+1)"), Corpus.Spec, "b", 0, "1:0:1:60:100"));
        Assert.Equal("AMP5702", e.Code);
    }

    [Fact]
    public void A_bake_of_a_clip_without_a_pipeline_is_refused()
    {
        Assert.Throws<NoteBakeException>(() => NoteBake.Bake(Header + Source, Corpus.Spec, "a", null, null, null, 100_000));
    }

    [Fact]
    public void For_auto_takes_the_length_from_the_notes()
    {
        AmpleDocument doc = Parser.Parse(Song("a.notes |> shift(+2bars)"), Corpus.Spec);
        Assert.Equal(3, doc.Blocks.Single(b => b.Name == "b").Parts.Single().Length);
        Assert.DoesNotContain("AMP1104", doc.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void A_pipeline_that_is_too_big_is_warned_about_before_it_runs_and_refused_when_it_runs()
    {
        IReadOnlyList<string> codes = Codes(Song("a.notes |> repeat(1024) |> repeat(1024)"));
        Assert.Contains("AMP2037", codes);
        Assert.Contains("AMP5701", codes);
    }

    [Fact]
    public void The_song_can_raise_its_own_limit_but_not_past_the_server()
    {
        const string big = "a.notes |> repeat(512) |> repeat(512)";
        Assert.Contains("AMP2037", Codes(Song(big)));
        string raised = Song(big).Replace("master-seed 1 }", "master-seed 1 }\nsettings { notes.maxEvents 1000000 }", StringComparison.Ordinal);
        IReadOnlyList<string> codes = Codes(raised);
        Assert.DoesNotContain("AMP2037", codes);
        Assert.DoesNotContain("AMP5701", codes);
        string tooMuch = Song("a.notes |> repeat(1024) |> repeat(1024)").Replace("master-seed 1 }", "master-seed 1 }\nsettings { notes.maxEvents 99000000 }", StringComparison.Ordinal);
        Assert.Contains("AMP5701", Codes(tooMuch));
    }

    [Fact]
    public void Two_clips_that_read_each_other_are_reported()
    {
        string text = Header + "midi-clip a at 1 for 1 {\n  notes b.notes |> transpose(1)\n}\n\nmidi-clip b at 2 for 1 {\n  notes a.notes |> transpose(1)\n}\n";
        Assert.Contains("AMP2029", Codes(text));
    }

    [Fact]
    public void A_midi_block_can_be_a_source_and_without_a_reader_it_is_a_placeholder()
    {
        string text = Header + "midi song at 1 for 1 {\n  file song.mid\n}\n\nmidi-clip b at 1 for 1 {\n  notes song.notes\n    |> transpose(+1)\n}\n";
        Assert.DoesNotContain("AMP2028", Codes(text));
        ParseOptions options = new()
        {
            ApplyNoteFlow = true,
            MidiSource = _ => new PipeValue { Events = [new PipeNote(0, NotePipeline.Beat, 60, 100, string.Empty, string.Empty), new PipeNote(NotePipeline.Beat, NotePipeline.Beat, 64, 90, string.Empty, string.Empty)], Span = NotePipeline.Bar },
        };
        AmpleDocument doc = Parser.Parse(text, Corpus.Spec, options);
        Assert.Equal("1:0:1:61:100 1:1:1:65:90", doc.Blocks.Single(b => b.Name == "b").Parameters.Single(p => p.Name == "notes").Value);
    }

    [Fact]
    public void A_midi_file_that_cannot_be_read_is_an_error()
    {
        string text = Header + "midi song at 1 for 1 {\n  file song.mid\n}\n\nmidi-clip b at 1 for 1 {\n  notes song.notes\n}\n";
        Assert.Contains("AMP2028", Codes(text, new ParseOptions { MidiSource = _ => null }));
    }

    [Fact]
    public void Comments_and_continuation_lines_do_not_change_the_notes()
    {
        string plain = Song("a.notes |> transpose(+3) |> bars(1..2)");
        string spread = Song("a.notes    # the source\n    |> transpose(+3)   # up a bit\n\n    # now only bar 1\n    |> bars(1..2)");
        ParseOptions apply = new() { ApplyNoteFlow = true };
        string Notes(string text) => Parser.Parse(text, Corpus.Spec, apply).Blocks.Single(b => b.Name == "b").Parameters.Single(p => p.Name == "notes").Value;
        Assert.Equal(Notes(plain), Notes(spread));
    }

    [Fact]
    public void The_editor_keeps_the_pipeline_text_and_the_build_replaces_it()
    {
        string song = Song("a.notes |> transpose(+3)");
        Assert.True(NotePipeline.IsExpression(Parser.Parse(song, Corpus.Spec).Blocks.Single(b => b.Name == "b").Parameters.Single(p => p.Name == "notes").Value));
        Assert.False(NotePipeline.IsExpression(Parser.Parse(song, Corpus.Spec, new ParseOptions { ApplyNoteFlow = true }).Blocks.Single(b => b.Name == "b").Parameters.Single(p => p.Name == "notes").Value));
    }

    [Fact]
    public void A_pipeline_that_uses_helpers_can_be_baked_and_stays_fresh()
    {
        string song = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\nhelper up(Notes n, Interval by) -> Notes { n |> transpose(by) }\n\nmidi-clip a at 1 for auto {\n  notes \"1:0:1:60:100 1:1:1:62:100\"\n}\n\nmidi-clip b at 2 for auto {\n  notes up(a.notes, +3st)\n    |> bake s\n}\n";
        BakeOutcome outcome = NoteBake.Bake(song, Corpus.Spec, "b", "s", null, null, 100_000);
        Assert.NotNull(outcome.Text);
        Assert.Contains("notes 1:0:1:63:100", outcome.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Parser.Parse(outcome.Text!, Corpus.Spec).Diagnostics, d => d.Code is "AMP2030" or "AMP2031" or "AMP2023" or "AMP2061");
    }

    [Fact]
    public void Dynamic_helpers_without_parameters_are_baked_with_the_value_of_the_build_instant()
    {
        string song = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\ndynamic helper phase() -> Int {\n  return currentTime().second % 4\n}\n\ndynamic helper shuffle(Int n) -> Int { return currentTime().second + n }\n\nmidi-clip a at 1 for auto {\n  notes \"1:0:1:60:100\"\n}\n\nmidi-clip b at 2 for auto {\n  notes a.notes |> transpose(phase())\n}\n";
        AmpleDocument doc = Parser.Parse(song, Corpus.Spec);
        EditResult edit = Edits.BakeDynamicHelpers(doc, new DateTime(2026, 10, 26, 12, 0, 7));
        Assert.Null(edit.Error);
        string baked = Edits.Apply(song, edit.Edits);
        Assert.Contains("dynamic helper phase() -> Int bake as 3 {", baked, StringComparison.Ordinal);
        Assert.Contains("dynamic helper shuffle(Int n) -> Int {", baked, StringComparison.Ordinal);
        AmpleDocument after = Parser.Parse(baked, Corpus.Spec);
        Assert.DoesNotContain(after.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Equal("baked-dynamic", after.Stability);
        Assert.NotNull(Edits.BakeDynamicHelpers(after, DateTime.Now).Error);
    }

    [Fact]
    public void One_call_of_a_dynamic_helper_can_be_baked_and_the_helper_stays_dynamic()
    {
        string song = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\ndynamic helper phase() -> Int {\n  return currentTime().second % 4\n}\n\nmidi-clip a at 1 for auto {\n  notes \"1:0:1:60:100\"\n}\n\nmidi-clip b at 2 for auto {\n  notes a.notes |> transpose(phase())\n}\n";
        AmpleDocument doc = Parser.Parse(song, Corpus.Spec);
        int line = doc.Lines.ToList().FindIndex(l => l.Contains("transpose(phase())", StringComparison.Ordinal)) + 1;
        int col = doc.Lines[line - 1].IndexOf("phase", StringComparison.Ordinal) + 2;
        EditResult edit = Edits.BakeCallSite(doc, line, col, new DateTime(2026, 10, 26, 12, 0, 6));
        Assert.Null(edit.Error);
        string baked = Edits.Apply(song, edit.Edits);
        Assert.Contains("transpose(bake phase() as 2)", baked, StringComparison.Ordinal);
        Assert.Contains("dynamic helper phase() -> Int {", baked, StringComparison.Ordinal);
        AmpleDocument after = Parser.Parse(baked, Corpus.Spec);
        Assert.DoesNotContain(after.Diagnostics, d => d.Severity == Severity.Error);
        Assert.NotNull(Edits.BakeCallSite(doc, 3, 20, DateTime.Now).Error);   // the declaration is not a call
    }

    private const string ClockSong = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\ndynamic helper phase() -> Int {\n  return currentTime().second % 4\n}\n\ndynamic helper hourShift(Int base) -> Int {\n  return base + currentTime().hour\n}\n\nmidi-clip a at 1 for auto {\n  notes \"1:0:1:60:100\"\n}\n\nmidi-clip b at 2 for auto {\n  notes a.notes |> transpose(currentTime().second % 4)\n}\n\nmidi-clip c at 3 for auto {\n  notes a.notes\n    |> transpose(hourShift(3))\n}\n\nmidi-clip d at 4 for auto {\n  notes a.notes |> transpose(phase())\n}\n";

    [Fact]
    public void The_whole_song_bakes_helpers_direct_clock_reads_and_calls_with_parameters()
    {
        AmpleDocument doc = Parser.Parse(ClockSong, Corpus.Spec);
        EditResult edit = Edits.BakeDynamicHelpers(doc, new DateTime(2026, 10, 26, 12, 0, 7));
        Assert.Null(edit.Error);
        string baked = Edits.Apply(ClockSong, edit.Edits);
        Assert.Contains("dynamic helper phase() -> Int bake as 3 {", baked, StringComparison.Ordinal);                  // the helper without parameters: in its declaration
        Assert.Contains("transpose(bake currentTime().second % 4 as 3)", baked, StringComparison.Ordinal);               // a direct clock read: where it stands
        Assert.Contains("transpose(bake hourShift(3) as 15)", baked, StringComparison.Ordinal);                          // a call with parameters, on a continuation line
        Assert.Contains("transpose(phase())", baked, StringComparison.Ordinal);                                          // already covered by the helper
        Assert.Contains("dynamic helper hourShift(Int base) -> Int {", baked, StringComparison.Ordinal);                 // helpers with parameters stay dynamic
        AmpleDocument after = Parser.Parse(baked, Corpus.Spec);
        Assert.DoesNotContain(after.Diagnostics, d => d.Severity == Severity.Error);
        Assert.NotEqual("dynamic", after.Stability);
        Assert.NotNull(Edits.BakeDynamicHelpers(after, DateTime.Now).Error);                                              // nothing left to bake
    }

    [Fact]
    public void One_block_bakes_its_own_clock_values_and_leaves_the_helpers_dynamic()
    {
        AmpleDocument doc = Parser.Parse(ClockSong, Corpus.Spec);
        EditResult edit = Edits.BakeDynamicHelpers(doc, new DateTime(2026, 10, 26, 12, 0, 7), "d");
        Assert.Null(edit.Error);
        string baked = Edits.Apply(ClockSong, edit.Edits);
        Assert.Contains("midi-clip d at 4 for auto {\n  notes a.notes |> transpose(bake phase() as 3)", baked, StringComparison.Ordinal);
        Assert.Contains("dynamic helper phase() -> Int {", baked, StringComparison.Ordinal);                             // the helper itself is untouched
        Assert.Contains("transpose(currentTime().second % 4)", baked, StringComparison.Ordinal);                         // the other blocks are untouched
        Assert.NotNull(Edits.BakeDynamicHelpers(doc, DateTime.Now, "a").Error);                                           // a block without anything dynamic
        Assert.NotNull(Edits.BakeDynamicHelpers(doc, DateTime.Now, "nope").Error);
    }

    [Fact]
    public void A_call_with_parameters_is_baked_at_the_call_when_its_arguments_are_plain_values()
    {
        AmpleDocument doc = Parser.Parse(ClockSong, Corpus.Spec);
        int line = doc.Lines.ToList().FindIndex(l => l.Contains("hourShift(3)", StringComparison.Ordinal)) + 1;
        int col = doc.Lines[line - 1].IndexOf("hourShift", StringComparison.Ordinal) + 3;
        EditResult edit = Edits.BakeCallSite(doc, line, col, new DateTime(2026, 10, 26, 12, 0, 7));
        Assert.Null(edit.Error);
        Assert.Contains("transpose(bake hourShift(3) as 15)", Edits.Apply(ClockSong, edit.Edits), StringComparison.Ordinal);
    }
}
