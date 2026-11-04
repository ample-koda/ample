using System.Text.Json.Nodes;
using Ample.Core.Abstractions;
using Ample.Language;

namespace Ample.Server.Tests;

/// <summary>An edit that does not fit says why and where; a file with only carriage returns has lines.</summary>
public sealed class AiEditHintTests
{
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
    public void A_replace_that_does_not_fit_shows_the_closest_lines_and_the_places_and_can_pick_one()
    {
        SpecData spec = Spec();
        const string text = "a\n  gain 3 dB\nb\n  gain 3 dB\nc\n  mix 20%\n";
        AmpleException near = Assert.Throws<AmpleException>(() => AiEdit.Apply(text, Edits("""[{"op":"replace","old":"  mix 25%","new":"  mix 30%"}]"""), spec));
        Assert.Contains("line 6", near.Message, StringComparison.Ordinal);
        AmpleException twice = Assert.Throws<AmpleException>(() => AiEdit.Apply(text, Edits("""[{"op":"replace","old":"  gain 3 dB","new":"  gain 6 dB"}]"""), spec));
        Assert.Contains("lines 2, 4", twice.Message, StringComparison.Ordinal);
        string picked = AiEdit.Apply(text, Edits("""[{"op":"replace","old":"  gain 3 dB","new":"  gain 6 dB","occurrence":2}]"""), spec).Text;
        Assert.Equal("a\n  gain 3 dB\nb\n  gain 6 dB\nc\n  mix 20%\n", picked);
    }

    [Fact]
    public void A_file_with_only_carriage_returns_has_lines_and_delete_lines_says_what_is_wrong()
    {
        SpecData spec = Spec();
        Assert.Equal("one\rthree", AiEdit.Apply("one\rtwo\rthree", Edits("""[{"op":"delete_lines","from":2}]"""), spec).Text);
        AmpleException outside = Assert.Throws<AmpleException>(() => AiEdit.Apply("one\ntwo", Edits("""[{"op":"delete_lines","from":5,"to":6}]"""), spec));
        Assert.Contains("has 2 line(s)", outside.Message, StringComparison.Ordinal);
        AmpleException none = Assert.Throws<AmpleException>(() => AiEdit.Apply("one\ntwo", Edits("""[{"op":"delete_lines"}]"""), spec));
        Assert.Contains("needs 'from'", none.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_that_a_model_escaped_twice_is_read_as_the_characters_it_stands_for_and_an_insert_anchor_may_be_several_lines()
    {
        SpecData spec = Spec();
        const string text = "helper programFor(Int p) -> Program {\n  if p == 0 { return 0 }\n  else { return 24 }\n}\n\nhelper melody(Int p) -> Int {\n  return p\n}\n";
        // the anchor and the text arrive as "->" and "\n" typed out (the real characters are a backslash and letters)
        const string insert = """[{"op":"insert","after":"helper programFor(Int p) -\\u003E Program {\\n  if p == 0 { return 0 }\\n  else { return 24 }\\n}\\n","text":"\\nhelper seedFor(Int p) -\\u003E Int {\\n  return p\\n}\\n"}]""";
        AiEdit.Result done = AiEdit.Apply(text, Edits(insert), spec);
        Assert.Contains("helper seedFor(Int p) -> Int {\n  return p\n}", done.Text, StringComparison.Ordinal);
        Assert.True(done.Text.IndexOf("helper seedFor", StringComparison.Ordinal) > done.Text.IndexOf("else { return 24 }", StringComparison.Ordinal));   // after the anchor
        Assert.True(done.Text.IndexOf("helper seedFor", StringComparison.Ordinal) < done.Text.IndexOf("helper melody", StringComparison.Ordinal));
        Assert.Contains(done.Summary, line => line.Contains("escape sequences", StringComparison.Ordinal));
        // a replace with the same artifact
        AiEdit.Result replaced = AiEdit.Apply(text, Edits("""[{"op":"replace","old":"helper melody(Int p) -\\u003E Int {","new":"helper melody(Int p) -\\u003E Int { # music"}]"""), spec);
        Assert.Contains("helper melody(Int p) -> Int { # music", replaced.Text, StringComparison.Ordinal);
        // text that is really wrong still fails, with the closest lines
        AmpleException wrong = Assert.Throws<AmpleException>(() => AiEdit.Apply(text, Edits("""[{"op":"insert","after":"helper nothing(Int p) -\\u003E Int {","text":"x"}]"""), spec));
        Assert.Contains("no line contains", wrong.Message, StringComparison.Ordinal);
        // and a plain line anchor and a several-line anchor given properly work without any escape
        Assert.Contains("# note", AiEdit.Apply(text, Edits("""[{"op":"insert","after":"  return p","text":"  # note"}]"""), spec).Text, StringComparison.Ordinal);
        Assert.Contains("# note", AiEdit.Apply(text, Edits("[{\"op\":\"insert\",\"before\":\"helper melody(Int p) -> Int {\\n  return p\",\"text\":\"# note\"}]"), spec).Text, StringComparison.Ordinal);
    }
}
