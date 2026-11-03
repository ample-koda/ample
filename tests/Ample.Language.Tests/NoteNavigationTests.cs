namespace Ample.Language.Tests;

/// <summary>What the editor can ask about helpers: definition, references, rename, hover (with the capability status of volatile built-ins), signature help (the piped notes are the first argument).</summary>
public sealed class NoteNavigationTests
{
    private const string Song = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\nhelper canonVoice(Notes source, Interval by, Duration delay = 2bars) -> Notes {\n  source |> transpose(by) |> shift(delay)\n}\n\ndynamic helper wobble() -> Int {\n  return currentTime().minute % 5   # the clock\n}\n\nmidi-clip theme at 1 for auto {\n  notes \"1:0:1:60:100 1:1:1:62:100\"\n}\n\nmidi-clip lead at 2 for auto {\n  notes theme.notes |> canonVoice(+7st) |> canonVoice(+2st, 1bar)\n}\n";

    private static AmpleDocument Doc() => Parser.Parse(Song, Corpus.Spec);

    private static (int Line, int Col) At(string needle, int occurrence = 0)
    {
        string[] lines = Song.Split('\n');
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
    public void Definition_references_and_rename_follow_a_helper_everywhere_it_is_called()
    {
        AmpleDocument doc = Doc();
        (int line, int col) = At("canonVoice", 1);   // the first call
        Location definition = Assert.IsType<Location>(Queries.FindDefinition(doc, line, col));
        Assert.Equal(3, definition.Line);
        Assert.Equal(8, definition.Col);
        Assert.Equal(3, Queries.FindReferences(doc, line, col).Count);   // the declaration and two calls
        EditResult rename = Edits.RenameHelper(doc, "canonVoice", "echoVoice");
        Assert.True(rename.Ok);
        Assert.Equal(3, rename.Edits.Count);
        Assert.All(rename.Edits, e => Assert.Equal("echoVoice", e.NewText));
        Assert.False(Edits.RenameHelper(doc, "canonVoice", "transpose").Ok);   // an operator has that name
        Assert.False(Edits.RenameHelper(doc, "canonVoice", "wobble").Ok);   // another helper has that name
        Assert.False(Edits.RenameHelper(doc, "canonVoice", "bad-name").Ok);
    }

    [Fact]
    public void Hover_shows_the_real_signature_the_effect_and_the_capability_of_a_volatile_call()
    {
        AmpleDocument doc = Doc();
        (int line, int col) = At("canonVoice", 1);
        string helper = Queries.HoverAt(doc, Corpus.Spec, line, col)!;
        Assert.Contains("canonVoice(Notes source, Interval by, Duration delay = 2bars) -> Notes", helper, StringComparison.Ordinal);
        Assert.Contains("helper (pure)", helper, StringComparison.Ordinal);
        (int tl, int tc) = At("currentTime");
        string clock = Queries.HoverAt(doc, Corpus.Spec, tl, tc)!;
        Assert.Contains("Needs the capability `clock`: enabled", clock, StringComparison.Ordinal);
        (int wl, int wc) = At("wobble");
        Assert.Contains("dynamic helper", Queries.HoverAt(doc, Corpus.Spec, wl, wc)!, StringComparison.Ordinal);
        (int ol, int oc) = At("transpose(by)");
        Assert.Contains("Interval semitones", Queries.HoverAt(doc, Corpus.Spec, ol, oc)!, StringComparison.Ordinal);
        AmpleDocument locked = Parser.Parse(Song.Replace("composition \"x\" { ample 1  tempo 100  master-seed 1 }", "composition \"x\" { ample 1  tempo 100  master-seed 1 }\nsettings { capabilities.clock off }", StringComparison.Ordinal), Corpus.Spec);
        (int l2, int c2) = (At("currentTime").Line + 1, At("currentTime").Col);
        Assert.Contains("permanently disabled by this song", Queries.HoverAt(locked, Corpus.Spec, l2, c2)!, StringComparison.Ordinal);
    }

    [Fact]
    public void Signature_help_counts_the_piped_notes_as_the_first_argument()
    {
        AmpleDocument doc = Doc();
        string before = Song[..(Song.IndexOf("canonVoice(+7st)", StringComparison.Ordinal) + "canonVoice(".Length)];
        var first = Queries.SignatureAt(doc, before)!.Value;
        Assert.Equal("canonVoice(Notes source, Interval by, Duration delay = 2bars) -> Notes", first.Label);
        Assert.Equal(1, first.Active);   // the notes before the pipe are the first argument: the cursor is on "by"
        var second = Queries.SignatureAt(doc, before + "+2st, ")!.Value;
        Assert.Equal(2, second.Active);
        var catalog = Queries.SignatureAt(doc, "notes theme.notes |> shift(")!.Value;
        Assert.Equal("shift(Duration by) -> Notes", catalog.Label);
        Assert.Null(Queries.SignatureAt(doc, "notes theme.notes"));
    }
}
