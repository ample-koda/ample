namespace Ample.Language.Tests;

/// <summary>Block comments (/* */) next to #, and doc comments (/** */ or ##) that the hover shows for helpers, blocks and the built-in operators.</summary>
public sealed class CommentsTests
{
    private const string Header = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n";

    private const string Doc = "/**\n * Moves a voice up and delays it.\n * @param source the notes to copy\n * @param by how far up, in semitones\n * @return the copy, shifted\n * @example theme.notes |> canonVoice(+7st)\n */\nhelper canonVoice(Notes source, Interval by, Duration delay = 2bars) -> Notes {\n  source |> transpose(by) |> shift(delay)\n}\n\n";

    private static (int Line, int Col) At(string song, string needle, int occurrence = 0)
    {
        string[] lines = song.Split('\n');
        int seen = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            int from = 0;
            while (lines[i].IndexOf(needle, from, StringComparison.Ordinal) is var at and >= 0)
            {
                if (seen++ == occurrence) return (i + 1, at + 2);
                from = at + 1;
            }
        }

        throw new InvalidOperationException(needle);
    }

    [Fact]
    public void A_block_comment_is_blanked_without_moving_anything_and_a_slash_star_that_is_not_a_comment_stays()
    {
        string text = "a /* one\ntwo */ b\nc \"/* not */\" d # /* not either\ne 4/*2 f\n";
        string blanked = Comments.BlankBlocks(text, out int open);
        Assert.Equal(-1, open);
        Assert.Equal(text.Length, blanked.Length);
        Assert.Equal(text.Count(c => c == '\n'), blanked.Count(c => c == '\n'));
        Assert.Equal("a       \n       b\nc \"/* not */\" d # /* not either\ne 4/*2 f\n", blanked);
    }

    [Fact]
    public void A_block_comment_that_is_never_closed_is_reported_with_its_line_and_its_pieces_are_known()
    {
        List<(int Line, int Start, int Length)> pieces = [];
        string blanked = Comments.BlankBlocks("x\n  /* open\nstill\n", out int open, pieces);
        Assert.Equal(1, open);
        Assert.Equal("x\n         \n     \n", blanked);
        Assert.Equal([(1, 2, 7), (2, 0, 5), (3, 0, 0)], pieces);
        List<(int Line, int Start, int Length)> closed = [];
        Comments.BlankBlocks("a /* b */ c", out _, closed);
        Assert.Equal([(0, 2, 7)], closed);
    }

    [Fact]
    public void A_song_with_block_comments_parses_like_the_same_song_without_them()
    {
        string plain = Header + "midi-clip theme at 1 for auto {\n  notes \"1:0:1:60:100 1:1:1:62:100\"\n}\n";
        string commented = "/* the song\n   of koda */\n" + Header + "/* the theme: */\nmidi-clip theme at 1 for auto {  /* inline */\n  notes \"1:0:1:60:100 1:1:1:62:100\"   /* two notes */\n}\n/* midi-clip gone at 3 for auto {\n  notes \"1:0:1:60:100\"\n} */\n";
        AmpleDocument a = Parser.Parse(plain, Corpus.Spec), b = Parser.Parse(commented, Corpus.Spec);
        Assert.Equal(a.Diagnostics.Select(x => x.Code), b.Diagnostics.Select(x => x.Code));   // the same notes (here: the unused output), and nothing about the comments
        Assert.Equal(a.Blocks.Select(x => x.Name), b.Blocks.Select(x => x.Name));
        Assert.Equal(a.Blocks[0].Parameters.First(p => p.Name == "notes").Value, b.Blocks[0].Parameters.First(p => p.Name == "notes").Value);
        Assert.Equal(commented.Split('\n'), b.Lines);   // the document keeps the comments, so the editors and the edits see them
        AmpleDocument open = Parser.Parse(Header + "/* never\nmidi-clip a at 1 for 1 {\n}\n", Corpus.Spec);
        Diagnostic d = Assert.Single(open.Diagnostics, x => x.Code == "AMP1116");
        Assert.Equal(3, d.Line);
    }

    [Fact]
    public void Doc_comments_are_found_above_the_declaration_in_both_spellings_and_a_blank_line_cuts_them_off()
    {
        string song = Header + Doc + "## The theme of the song.\n## @deprecated use lead instead\nmidi-clip theme at 1 for auto {\n  notes \"1:0:1:60:100\"\n}\n\n/** orphan */\n\nmidi-clip lead at 2 for auto {\n  notes \"1:0:1:60:100\"\n}\n";
        AmpleDocument doc = Parser.Parse(song, Corpus.Spec);
        DocComment helper = doc.Docs[7 + 2];
        Assert.Equal("Moves a voice up and delays it.", helper.Summary);
        Assert.Equal("the notes to copy", helper.ParamText("source"));
        Assert.Equal("how far up, in semitones", helper.ParamText("by"));
        Assert.Equal("the copy, shifted", helper.Returns);
        Assert.Equal("theme.notes |> canonVoice(+7st)", Assert.Single(helper.Examples));
        DocComment theme = doc.Docs[doc.Blocks.First(b => b.Name == "theme").Line];
        Assert.Equal("The theme of the song.", theme.Summary);
        Assert.Equal("use lead instead", theme.Deprecated);
        Assert.False(doc.Docs.ContainsKey(doc.Blocks.First(b => b.Name == "lead").Line));
    }

    [Fact]
    public void Hovering_a_helper_call_shows_its_types_its_doc_comment_and_the_notes_of_its_parameters()
    {
        string song = Header + Doc + "midi-clip theme at 1 for auto {\n  notes \"1:0:1:60:100\"\n}\n\nmidi-clip lead at 2 for auto {\n  notes theme.notes |> canonVoice(+7st)\n}\n";
        AmpleDocument doc = Parser.Parse(song, Corpus.Spec);
        (int line, int col) = At(song, "canonVoice", 1);
        string hover = Queries.HoverAt(doc, Corpus.Spec, line, col)!;
        Assert.Contains("canonVoice(Notes source, Interval by, Duration delay = 2bars) -> Notes", hover, StringComparison.Ordinal);
        Assert.Contains("Moves a voice up and delays it.", hover, StringComparison.Ordinal);
        Assert.Contains("- `Notes source` — the notes to copy", hover, StringComparison.Ordinal);
        Assert.Contains("- `Interval by` — how far up, in semitones", hover, StringComparison.Ordinal);
        Assert.Contains("- `Duration delay` (default `2bars`)", hover, StringComparison.Ordinal);
        Assert.Contains("**Returns** the copy, shifted", hover, StringComparison.Ordinal);
        Assert.Contains("**Example**", hover, StringComparison.Ordinal);
        string plain = Parser.Parse(song.Replace(Doc.Split("helper")[0], string.Empty, StringComparison.Ordinal), Corpus.Spec) is { } bare ? Queries.HoverAt(bare, Corpus.Spec, At(song, "canonVoice", 1).Line - 7, At(song, "canonVoice", 1).Col)! : string.Empty;
        Assert.Contains("Returns `Notes`.", plain, StringComparison.Ordinal);   // without a doc comment the types still say what it gives back
        Assert.DoesNotContain("the notes to copy", plain, StringComparison.Ordinal);
    }

    [Fact]
    public void Hovering_a_built_in_operator_shows_its_signature_what_it_does_and_what_each_parameter_means()
    {
        string song = Header + "midi-clip theme at 1 for auto {\n  notes \"1:0:1:60:100\" |> shift(1beat)\n}\n";
        AmpleDocument doc = Parser.Parse(song, Corpus.Spec);
        (int line, int col) = At(song, "shift(");
        string hover = Queries.HoverAt(doc, Corpus.Spec, line, col)!;
        Assert.Contains("shift(Duration by) -> Notes", hover, StringComparison.Ordinal);
        Assert.Contains("Move every note in time", hover, StringComparison.Ordinal);
        Assert.Contains("- `Duration by` — How far to move the notes in time", hover, StringComparison.Ordinal);
        Assert.Contains("Built in.", hover, StringComparison.Ordinal);
    }

    [Fact]
    public void A_doc_comment_can_give_types_and_unions_in_braces_or_bare_and_the_hover_shows_the_ones_that_differ_from_the_declaration()
    {
        string song = Header + "/**\n * Moves a voice.\n * @param {Interval|Int} by how far up\n * @param Notes source the notes to copy\n * @param delay when it starts\n * @return {Notes|Int} the copy\n */\nhelper up(Notes source, Interval by, Duration delay = 2bars) -> Notes {\n  source |> transpose(by) |> shift(delay)\n}\n";
        AmpleDocument doc = Parser.Parse(song, Corpus.Spec);
        DocComment comment = doc.Docs[doc.Helpers.Single(h => h.Name == "up").Line];
        Assert.Equal("Interval|Int", comment.ParamType("by"));
        Assert.Equal("how far up", comment.ParamText("by"));
        Assert.Equal("Notes", comment.ParamType("source"));
        Assert.Equal("the notes to copy", comment.ParamText("source"));
        Assert.Null(comment.ParamType("delay"));
        Assert.Equal("when it starts", comment.ParamText("delay"));
        Assert.Equal("Notes|Int", comment.ReturnType);
        Assert.Equal("the copy", comment.Returns);
        (int line, int col) = At(song, "up(Notes");
        string hover = Queries.HoverAt(doc, Corpus.Spec, line, col)!;
        Assert.Contains("- `Interval by` — how far up *(documented as `Interval|Int`)*", hover, StringComparison.Ordinal);
        Assert.Contains("- `Notes source` — the notes to copy\n", hover, StringComparison.Ordinal);   // the same as the declaration: nothing is added
        Assert.Contains("**Returns** `Notes|Int` the copy", hover, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1bar", "bar", "Length in bars")]
    [InlineData("250ms", "ms", "milliseconds")]
    [InlineData("Round", "Round", "nearest whole number")]
    [InlineData("min", "min", "smaller of two")]
    public void Hovering_a_unit_or_a_function_that_is_built_in_explains_it(string needle, string id, string text)
    {
        string song = Header + "helper pick(Number a) -> Int {\n  let x = 250ms\n  emit 1:0:1:60:100 |> shift(1bar)\n  return Round(min(a, 2))\n}\n";
        AmpleDocument doc = Parser.Parse(song, Corpus.Spec);
        (int line, int col) = At(song, needle);
        string hover = Queries.HoverAt(doc, Corpus.Spec, line, col + (needle.Length > 3 ? 0 : 1)) ?? string.Empty;
        Assert.Contains(id, hover, StringComparison.Ordinal);
        Assert.Contains(text, hover, StringComparison.Ordinal);
    }

    [Fact]
    public void A_unit_in_the_notes_line_of_a_block_is_explained_too()
    {
        string song = Header + "midi-clip lead at 3 for auto {\n  notes 1:0:1:60:100 |> stretch(2) |> shift(1bar)\n}\n";
        AmpleDocument doc = Parser.Parse(song, Corpus.Spec);
        (int line, int col) = At(song, "1bar");
        Assert.Contains("Length in bars", Queries.HoverAt(doc, Corpus.Spec, line, col) ?? "(nothing)", StringComparison.Ordinal);
        (int l2, int c2) = At(song, "stretch");
        Assert.Contains("stretch(Number factor)", Queries.HoverAt(doc, Corpus.Spec, l2, c2) ?? "(nothing)", StringComparison.Ordinal);
    }

    private static string[] DocCodes(string helper, string tail = "") =>
        [.. Parser.Parse(Header + helper + "\nmidi-clip a at 1 for 1 {\n  notes 1:0:1:60:100 |> up(+2st)" + tail + "\n}\n", Corpus.Spec).Diagnostics.Where(d => d.Code is "AMP2090" or "AMP2091" or "AMP2092" or "AMP2093" or "AMP2094").Select(d => d.Code)];

    private const string Decl = "helper up(Notes source, Interval by) -> Notes {\n  source |> transpose(by)\n}\n";

    [Fact]
    public void A_doc_comment_that_says_something_else_than_the_declaration_is_reported_and_one_that_agrees_is_not()
    {
        Assert.Equal(["AMP2091"], DocCodes("/**\n * @param Notes source the notes\n * @param {Interval|Int} by how far\n * @return Notes the copy\n */\nhelper up(Notes source, Interval by) -> Notes {\n  source |> transpose(by)\n}\n"));   // only the union on "by" is wider than the declaration
        Assert.Equal(["AMP2090"], DocCodes("/**\n * @param source the notes\n * @param byy how far\n */\n" + Decl).Where(c => c != "AMP2094"));   // a parameter that does not exist
        Assert.Equal(["AMP2091", "AMP2091"], DocCodes("/**\n * @param {Notes|Int} source the notes\n * @param by how far\n * @return {Notes|Int} the copy\n */\n" + Decl));   // wider than the declaration, for a parameter and for the return
        Assert.Contains("AMP2092", DocCodes("/**\n * @param {Notess} source the notes\n * @param by how far\n */\n" + Decl));
        Assert.Equal(["AMP2094"], DocCodes("/**\n * @param source the notes\n */\n" + Decl));
        Assert.Empty(DocCodes(Decl));   // no doc comment: nothing to compare
        Assert.Empty(DocCodes("type Shift = Interval | Int\n\n/**\n * @param Notes source the notes\n * @param {Interval|Int} by how far\n */\nhelper up(Notes source, Shift by) -> Notes {\n  source |> transpose(by)\n}\n"));   // an alias is written out
        Assert.Empty(DocCodes("# ample-ignore AMP2090\n/**\n * @param byy how far\n * @param source the notes\n * @param by how far\n */\n" + Decl));   // the comment line above the doc comment silences it
    }

    private static Diagnostic[] GenericDiagnostics(string doc, string declared) =>
        [.. Parser.Parse(Header + "type Num = Int | Float\n\n/**\n * @param " + doc + " xs the list\n */\nhelper f(" + declared + " xs) -> Notes {\n  return 1:0:1:60:100\n}\n", Corpus.Spec).Diagnostics.Where(d => d.Code is "AMP2090" or "AMP2091" or "AMP2092" or "AMP2094")];

    [Fact]
    public void A_union_inside_the_brackets_of_a_generic_is_one_type_and_the_arguments_are_compared_as_types_of_their_own()
    {
        Assert.Empty(GenericDiagnostics("{List<Int>}", "List<Int>"));
        Assert.Empty(GenericDiagnostics("{List<Float|Int>}", "List<Num>"));   // an alias inside the brackets is written out, the order of a union does not matter
        Assert.Empty(GenericDiagnostics("List<Int | Float>", "List<Num>"));   // bare, with spaces inside the brackets
        Diagnostic wider = Assert.Single(GenericDiagnostics("{List<Int|Float>}", "List<Float>"));
        Assert.Equal("AMP2091", wider.Code);   // a real difference, and no "type Float> does not exist"
        Diagnostic typo = Assert.Single(GenericDiagnostics("{List<Notess|Int>}", "List<Int>"), d => d.Code == "AMP2092");
        Assert.Contains("Notess", typo.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(">", typo.Message.Replace("Notess", string.Empty, StringComparison.Ordinal).Replace("\"", string.Empty, StringComparison.Ordinal).Replace(".", string.Empty, StringComparison.Ordinal).Replace("The doc comment of f uses a type that does not exist: ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    private const string VariableSong = Header + "let base = 60\nlet gap = 2bars\n\n/**\n * Builds a phrase.\n * @param count how many bars\n */\nhelper phrase(Int count, Interval up = +2st) -> Notes {\n  let motif = 1:0:0.5:76:86 1:1:0.25:79:78\n  var amount = 1\n  amount = 2.5\n  let name = \"verse\"\n  let flags = [1, 2.5]\n  let total = count * 2 / 4\n  let moved = motif |> transpose(up)\n  for step in 0..count {\n    emit moved |> shift(step * gap)\n  }\n  return moved\n}\n";

    private static string Hover(string song, string needle, int occurrence = 0)
    {
        AmpleDocument doc = Parser.Parse(song, Corpus.Spec);
        (int line, int col) = At(song, needle, occurrence);
        return Queries.HoverAt(doc, Corpus.Spec, line, col) ?? "(nothing)";
    }

    [Fact]
    public void Hovering_a_let_or_var_shows_its_value_its_worked_out_type_and_the_types_it_can_have()
    {
        string motif = Hover(VariableSong, "motif", 0);
        Assert.Contains("`let motif: Notes`", motif, StringComparison.Ordinal);
        Assert.Contains("= `1:0:0.5:76:86 1:1:0.25:79:78`", motif, StringComparison.Ordinal);
        Assert.Contains("**Type** `Notes` — from a list of notes", motif, StringComparison.Ordinal);
        string amount = Hover(VariableSong, "amount", 0);
        Assert.Contains("`var amount: Int | Float`", amount, StringComparison.Ordinal);
        Assert.Contains("**Potential types** `Int`, `Float` — it is assigned values of these types.", amount, StringComparison.Ordinal);
        Assert.Contains("`let flags: List<Int | Float>`", Hover(VariableSong, "flags", 0), StringComparison.Ordinal);
        Assert.Contains("`let name: String`", Hover(VariableSong, "name", 0), StringComparison.Ordinal);
        Assert.Contains("`let total: Float`", Hover(VariableSong, "total", 0), StringComparison.Ordinal);   // a division gives a Float
        Assert.Contains("`let moved: Notes`", Hover(VariableSong, "moved", 0), StringComparison.Ordinal);
        Assert.Contains("the call of transpose()", Hover(VariableSong, "moved", 0), StringComparison.Ordinal);
    }

    [Fact]
    public void A_use_of_a_variable_a_parameter_a_loop_variable_and_a_constant_show_their_type_too()
    {
        Assert.Contains("`let moved: Notes`", Hover(VariableSong, "moved", 1), StringComparison.Ordinal);   // in emit
        string count = Hover(VariableSong, "count", 1);   // the parameter inside the body
        Assert.Contains("`parameter count: Int`", count, StringComparison.Ordinal);
        Assert.Contains("how many bars", count, StringComparison.Ordinal);   // the note of the doc comment
        Assert.Contains("`parameter up: Interval`", Hover(VariableSong, "up", 1), StringComparison.Ordinal);
        Assert.Contains("`loop variable step: Int`", Hover(VariableSong, "step", 1), StringComparison.Ordinal);
        string gap = Hover(VariableSong, "gap", 1);   // the constant, used inside the helper
        Assert.Contains("`let gap: Length`", gap, StringComparison.Ordinal);
        Assert.Contains("constant", gap, StringComparison.Ordinal);
        Assert.Contains("`let base: Int`", Hover(VariableSong, "base", 0), StringComparison.Ordinal);
    }

    private const string UntypedSong = Header + "helper pulse(Notes n) {\n  emit n |> shift(1bar)\n}\n\nhelper size() {\n  let x = 1\n  return x\n}\n\nhelper pick(Int n) {\n  if n > 3 {\n    return 1\n  } else {\n    return 2.5\n  }\n}\n\nhelper double(Int n) {\n  n * 2\n}\n\nhelper user(Notes n) -> Notes {\n  let count = size()\n  let chosen = pick(2)\n  let notes = pulse(n)\n  return notes\n}\n";

    [Fact]
    public void A_helper_that_does_not_say_what_it_returns_gets_the_type_of_what_its_body_returns()
    {
        string pulse = Hover(UntypedSong, "pulse", 0);
        Assert.Contains("pulse(Notes n) -> Notes", pulse, StringComparison.Ordinal);
        Assert.Contains("Returns `Notes` — worked out from what the body returns.", pulse, StringComparison.Ordinal);   // it emits
        Assert.Contains("size() -> Int", Hover(UntypedSong, "size", 0), StringComparison.Ordinal);   // returns a variable
        Assert.Contains("pick(Int n) -> Int | Float", Hover(UntypedSong, "pick", 0), StringComparison.Ordinal);   // two returns of other types
        Assert.Contains("double(Int n) -> Int", Hover(UntypedSong, "double", 0), StringComparison.Ordinal);   // the last expression is the value
        Assert.Contains("`let count: Int`", Hover(UntypedSong, "count", 0), StringComparison.Ordinal);   // a call of such a helper has its type too
        Assert.Contains("`let chosen: Int | Float`", Hover(UntypedSong, "chosen", 0), StringComparison.Ordinal);
        Assert.Contains("`let notes: Notes`", Hover(UntypedSong, "notes", 0), StringComparison.Ordinal);
    }

    [Fact]
    public void Hovering_the_let_keyword_explains_it_and_says_what_it_declares()
    {
        string hover = Hover(VariableSong, "let motif");
        Assert.Contains("`let` — keyword", hover, StringComparison.Ordinal);
        Assert.Contains("Declares `motif: Notes`.", hover, StringComparison.Ordinal);
    }

    private static string ApplyFix(string text, Fix fix)
    {
        string[] lines = text.Split('\n');
        int Offset(int line, int col) => lines.Take(line - 1).Sum(l => l.Length + 1) + col - 1;
        int start = Offset(fix.Line, fix.Col), end = Offset(fix.EndLine == 0 ? fix.Line : fix.EndLine, fix.EndCol);
        return text[..start] + fix.Text + text[end..];
    }

    private static string FixedSong(string doc, string code, out Fix fix)
    {
        string song = Header + doc + "helper up(Notes source, Interval by, Duration delay = 2bars) -> Notes {\n  source |> transpose(by) |> shift(delay)\n}\n\nmidi-clip a at 1 for 1 {\n  notes 1:0:1:60:100 |> up(+2st)\n}\n";
        Diagnostic d = Assert.Single(Parser.Parse(song, Corpus.Spec).Diagnostics, x => x.Code == code);
        fix = Assert.IsType<Fix>(d.Fix);
        string applied = ApplyFix(song, fix);
        Assert.DoesNotContain(Parser.Parse(applied, Corpus.Spec).Diagnostics, x => x.Code == code);   // the fix really fixes it
        return applied;
    }

    [Fact]
    public void The_warnings_about_a_doc_comment_have_quick_fixes_that_change_only_the_comment()
    {
        string both = "/**\n * @param source the notes\n * @param by how far\n * @param delay when\n */\n";
        string renamed = FixedSong(both.Replace("@param by ", "@param byy "), "AMP2090", out Fix rename);
        Assert.Equal("Rename @param byy to by", rename.Label);
        Assert.Contains(" * @param by how far\n", renamed, StringComparison.Ordinal);
        string removed = FixedSong(both.Replace("@param by ", "@param nothingLikeIt "), "AMP2090", out Fix remove);
        Assert.Equal("Remove this @param line", remove.Label);
        Assert.True(remove.Risky);   // text goes away: "Fix all" leaves it alone
        Assert.DoesNotContain("nothingLikeIt", removed, StringComparison.Ordinal);
        Assert.Contains(" * @param source the notes\n * @param delay when\n */", removed, StringComparison.Ordinal);
        string typed = FixedSong(both.Replace("@param source", "@param {Notes|Int} source"), "AMP2091", out Fix type);
        Assert.Equal("Use Notes in the comment", type.Label);
        Assert.Contains("@param {Notes} source the notes", typed, StringComparison.Ordinal);
        string returned = FixedSong(both.Replace(" */", " * @return {Notes|Int} the copy\n */"), "AMP2091", out _);
        Assert.Contains("@return {Notes} the copy", returned, StringComparison.Ordinal);
        string typo = FixedSong(both.Replace("@param source", "@param {Notess} source"), "AMP2092", out Fix change);
        Assert.Equal("Change Notess to Notes", change.Label);
        Assert.Contains("@param {Notes} source", typo, StringComparison.Ordinal);
        string added = FixedSong("/**\n * @param source the notes\n * @param by how far\n */\n", "AMP2094", out Fix add);
        Assert.Equal("Add @param for delay", add.Label);
        Assert.Contains(" * @param by how far\n * @param delay \n */", added, StringComparison.Ordinal);
        string hashes = FixedSong("## @param source the notes\n## @param by how far\n", "AMP2094", out _);   // the same in the ## style
        Assert.Contains("## @param by how far\n## @param delay \nhelper up", hashes, StringComparison.Ordinal);
    }

    [Fact]
    public void A_call_of_a_deprecated_helper_is_a_warning_with_the_reason()
    {
        AmpleDocument doc = Parser.Parse(Header + "/**\n * Old.\n * @deprecated use up2\n */\n" + Decl + "\nmidi-clip a at 1 for 1 {\n  notes 1:0:1:60:100 |> up(+2st)\n}\n", Corpus.Spec);
        Diagnostic d = Assert.Single(doc.Diagnostics, x => x.Code == "AMP2093");
        Assert.Equal(Severity.Warning, d.Severity);
        Assert.Contains("deprecated: use up2", d.Message, StringComparison.Ordinal);
        Assert.Equal(doc.Lines.ToList().FindIndex(l => l.Contains("|> up(", StringComparison.Ordinal)) + 1, d.Line);
    }

    [Fact]
    public void Hovering_the_name_of_a_block_shows_the_doc_comment_above_it()
    {
        string song = Header + "/**\n * The bass line of the chorus.\n */\nmidi-clip bass at 1 for auto {\n  notes \"1:0:1:36:100\"\n}\n";
        AmpleDocument doc = Parser.Parse(song, Corpus.Spec);
        (int line, int col) = At(song, "bass at");
        Assert.Contains("The bass line of the chorus.", Queries.HoverAt(doc, Corpus.Spec, line, col)!, StringComparison.Ordinal);
    }
}
