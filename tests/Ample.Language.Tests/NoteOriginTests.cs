namespace Ample.Language.Tests;

// Step 1 of plan/live-preview/note-origin.md: every note of a chain knows the source note it came from; the origin lives in memory only.
public sealed class NoteOriginTests
{
    private const string Theme = "1:0:1:60:100 1:1:1:62:100 1:2:1:64:100 1:3:1:65:100";
    private const string Other = "1:0:1:72:100 1:2:1:74:100";

    private static PipeValue Value(string notes)
    {
        List<PipeIssue> bad = [];
        return new PipeValue { Events = NotePipeline.ReadNotes(notes, bad), Span = 4 * NotePipeline.Beat };
    }

    private static PipeResult Run(string expression, string theme = Theme)
    {
        NotePipeline.Context context = new()
        {
            Tempo = "120",
            MasterSeed = 7,
            Source = name => name switch
            {
                "theme" => new NotePipeline.SourceInfo(Value(theme), "test:theme", null),
                "other" => new NotePipeline.SourceInfo(Value(Other), "test:other", null),
                _ => new NotePipeline.SourceInfo(null, null, $"There is no block \"{name}\""),
            },
        };
        PipeResult result = NotePipeline.Evaluate(expression, context);
        Assert.Empty(result.Errors ?? []);
        return result;
    }

    private static string Origins(PipeResult result) => string.Join(' ', result.Value!.Events.Select(e => e.Origin is null ? "-" : $"{e.Origin.Source}#{e.Origin.Index}" + (e.Origin.Lap > 0 ? $"@{e.Origin.Lap}" : string.Empty)));

    [Fact]
    public void A_reference_gives_every_note_its_place_in_the_source()
    {
        Assert.Equal("theme#0 theme#1 theme#2 theme#3", Origins(Run("theme.notes")));
    }

    [Theory]
    [InlineData("theme.notes |> transpose(+12)")]
    [InlineData("theme.notes |> shift(1beat)")]
    [InlineData("theme.notes |> stretch(2)")]
    [InlineData("theme.notes |> legato()")]
    [InlineData("theme.notes |> quantize(0.25beat)")]
    public void Operators_that_keep_the_notes_keep_the_origin(string expression)
    {
        Assert.Equal("theme#0 theme#1 theme#2 theme#3", Origins(Run(expression)));
    }

    [Theory]
    [InlineData("theme.notes |> bars(1..=1)", "theme#0 theme#1 theme#2 theme#3")]
    [InlineData("theme.notes |> remove(beats(2..4))", "theme#0 theme#3")]
    [InlineData("theme.notes |> mask(beats(1..3))", "theme#0 theme#1")]
    public void Operators_that_select_leave_the_survivors_with_their_own_origin(string expression, string expected)
    {
        Assert.Equal(expected, Origins(Run(expression)));
    }

    [Fact]
    public void Repeat_makes_copies_that_know_the_lap_and_a_repeat_of_a_repeat_keeps_the_laps_apart()
    {
        Assert.Equal("theme#0 theme#1 theme#0@1 theme#1@1 theme#0@2 theme#1@2", Origins(Run("theme.notes |> bars(1..=1) |> mask(beats(1..3)) |> repeat(3)")));
        string nested = Origins(Run("theme.notes |> mask(beats(1..2)) |> repeat(2) |> repeat(2)"));
        Assert.Equal("theme#0 theme#0@1 theme#0@2 theme#0@3", nested);
    }

    [Fact]
    public void Concat_and_merge_keep_every_note_with_its_own_source()
    {
        Assert.Equal("theme#0 theme#1 theme#2 theme#3 other#0 other#1", Origins(Run("theme.notes |> concat(other.notes)")));
        Assert.Equal("theme#0 theme#1 theme#2 theme#3 other#0 other#1", Origins(Run("theme.notes |> merge(other.notes)")));
    }

    [Fact]
    public void Reverse_moves_the_notes_and_the_origin_goes_with_them()
    {
        PipeResult result = Run("theme.notes |> reverse()");
        Assert.Equal(["theme#3", "theme#2", "theme#1", "theme#0"], result.Value!.Events.OrderBy(e => e.At).Select(e => $"{e.Origin!.Source}#{e.Origin.Index}"));
    }

    [Theory]
    [InlineData("euclid(3, 8, 60)")]
    [InlineData("euclid(5, 8, pitch=62)")]
    public void Notes_an_operator_makes_have_no_origin(string expression)
    {
        Assert.All(Run(expression).Value!.Events, e => Assert.Null(e.Origin));
    }

    [Fact]
    public void A_chain_through_a_chain_keeps_the_deepest_source()
    {
        NotePipeline.Context context = new()
        {
            Tempo = "120",
            MasterSeed = 7,
            Source = name => name == "mid"
                ? new NotePipeline.SourceInfo(new PipeValue { Events = [.. Value(Theme).Events.Select((e, i) => e with { Origin = new NoteOrigin("theme", i) })], Span = Value(Theme).Span }, "test:mid", null)
                : new NotePipeline.SourceInfo(null, null, "no"),
        };
        PipeResult result = NotePipeline.Evaluate("mid.notes |> transpose(+1)", context);
        Assert.Equal("theme#0 theme#1 theme#2 theme#3", Origins(result));
    }

    [Fact]
    public void The_origin_changes_nothing_that_is_written_hashed_or_compared()
    {
        // the same chain over sources that carry different origins: the notes text, the span and the recipe are byte-identical
        PipeResult plain = Run("theme.notes |> transpose(+3) |> repeat(2)");
        NotePipeline.Context tagged = new()
        {
            Tempo = "120",
            MasterSeed = 7,
            Source = _ => new NotePipeline.SourceInfo(new PipeValue { Events = [.. Value(Theme).Events.Select(e => e with { Origin = new NoteOrigin("elsewhere", 99, 5) })], Span = Value(Theme).Span }, "test:theme", null),
        };
        PipeResult other = NotePipeline.Evaluate("theme.notes |> transpose(+3) |> repeat(2)", tagged);
        Assert.Equal(plain.Notes, other.Notes);
        Assert.Equal(plain.Span, other.Span);
        Assert.Equal(plain.Recipe, other.Recipe);
        Assert.DoesNotContain("theme#", plain.Notes, StringComparison.Ordinal);
        PipeNote a = new(0, 1, 60, 100, string.Empty, string.Empty), b = a with { Origin = new NoteOrigin("x", 1) };
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    public static TheoryData<string, string, string> SharedCorpus()
    {
        TheoryData<string, string, string> data = [];
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(Corpus.Read("spec-tests/note-origins/origins.json"));
        foreach (System.Text.Json.JsonElement c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            data.Add(c.GetProperty("id").GetString()!, c.GetProperty("expr").GetString()!, string.Join(' ', c.GetProperty("origins").EnumerateArray().Select(o => o.GetString())));
        }

        return data;
    }

    // the JavaScript evaluator runs the very same file (web/lib/test/note-origin.test.mjs): the origins must agree
    [Theory]
    [MemberData(nameof(SharedCorpus))]
    public void The_shared_corpus_gives_the_same_origins_as_the_javascript_evaluator(string id, string expression, string expected)
    {
        Assert.False(string.IsNullOrEmpty(id));
        string actual = string.Join(' ', Run(expression).Value!.Events.Select(e => e.Origin is null ? "-" : $"{e.Origin.Source}#{e.Origin.Index}@{e.Origin.Lap}"));
        Assert.Equal(expected, actual);
    }
}
