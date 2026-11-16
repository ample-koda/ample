namespace Ample.Language.Tests;

/// <summary>
/// The condition of an if is written with or without parentheses around the whole of it: <c>if (a &gt; 5) {</c> and <c>if a &gt; 5 {</c> mean the
/// same, in a Notes helper and in the process of an audio component. The parentheses of
/// the whole condition are told from parentheses inside it by matching them (<see cref="ConditionSyntax"/>); the formatter writes the parenthesized form.
/// </summary>
public sealed class IfSyntaxTests
{
    private const string Header = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n";

    // ---- the condition: the parentheses of the whole of it, and the ones inside it

    [Theory]
    [InlineData("a > 5", "a > 5", false)]
    [InlineData("(a > 5)", "a > 5", true)]
    [InlineData("((a > 5))", "a > 5", true)]
    [InlineData("  ( a > 5 )  ", "a > 5", true)]
    [InlineData("(a > 5) && (b < 3)", "(a > 5) && (b < 3)", false)]   // the first ( closes before the end: they are two groups, not one
    [InlineData("((a > 5) && (b < 3))", "(a > 5) && (b < 3)", true)]
    [InlineData("a > 5 && (b < 10 || c == 3)", "a > 5 && (b < 10 || c == 3)", false)]
    [InlineData("(a > 5 && (b < 10 || c == 3))", "a > 5 && (b < 10 || c == 3)", true)]
    [InlineData("(level - 1) * 2", "(level - 1) * 2", false)]
    [InlineData("f(g(1), 2) > 3", "f(g(1), 2) > 3", false)]
    [InlineData("(f(g(1), 2) > 3)", "f(g(1), 2) > 3", true)]
    [InlineData("(name == \")\")", "name == \")\"", true)]   // a ) inside a string is not a parenthesis
    [InlineData("name == \"(x\"", "name == \"(x\"", false)]
    [InlineData("(name == \"a\\\"(\")", "name == \"a\\\"(\"", true)]   // an escaped quote does not end the string
    public void The_parentheses_of_the_whole_condition_are_told_from_the_ones_inside_it(string written, string inner, bool wrapped)
    {
        Assert.Equal(inner, ConditionSyntax.Unwrap(written));
        Assert.Equal(wrapped, ConditionSyntax.IsWrapped(written));
        Assert.Equal("(" + inner + ")", ConditionSyntax.Wrap(written));
        Assert.Equal(ConditionSyntax.Wrap(written), ConditionSyntax.Wrap(ConditionSyntax.Wrap(written)));   // the parenthesized form is stable: no layer is added each time
    }

    [Theory]
    [InlineData("a > 5", null)]
    [InlineData("(a > 5 && (b < 10))", null)]
    [InlineData("(name == \")\")", null)]
    [InlineData("(a > 5", "never closed")]
    [InlineData("a > 5)", "closes a ( that is not there")]
    [InlineData("((a > 5)", "never closed")]
    [InlineData("name == \"abc", "string")]
    public void An_unbalanced_condition_is_named(string condition, string? expected)
    {
        ConditionSyntax.Problem? problem = ConditionSyntax.ParenProblem(condition);
        if (expected is null) Assert.Null(problem);
        else Assert.Contains(expected, problem!.Message, StringComparison.Ordinal);
    }

    // ---- the head of a conditional on a line

    [Theory]
    [InlineData("if (value > 5) {", ConditionSyntax.HeadKind.If, false, "value > 5", true)]
    [InlineData("if value > 5 {", ConditionSyntax.HeadKind.If, false, "value > 5", false)]
    [InlineData("if(value > 5){", ConditionSyntax.HeadKind.If, false, "value > 5", true)]
    [InlineData("  if (a > 5 && (b < 10 || c == 3)) {", ConditionSyntax.HeadKind.If, false, "a > 5 && (b < 10 || c == 3)", true)]
    [InlineData("if a > 5 && (b < 10 || c == 3) {", ConditionSyntax.HeadKind.If, false, "a > 5 && (b < 10 || c == 3)", false)]
    [InlineData("} else if (value == 5) {", ConditionSyntax.HeadKind.ElseIf, true, "value == 5", true)]
    [InlineData("} else if value == 5 {", ConditionSyntax.HeadKind.ElseIf, true, "value == 5", false)]
    [InlineData("}else if(value == 5){", ConditionSyntax.HeadKind.ElseIf, true, "value == 5", true)]
    [InlineData("} else {", ConditionSyntax.HeadKind.Else, true, "", false)]
    public void Both_spellings_of_a_head_are_read_the_same(string line, ConditionSyntax.HeadKind kind, bool closes, string condition, bool wrapped)
    {
        ConditionSyntax.Head head = ConditionSyntax.Parse(line)!;
        Assert.Null(head.Problem);
        Assert.Equal(kind, head.Kind);
        Assert.Equal(closes, head.Closes);
        Assert.Equal(condition, head.Condition);
        Assert.Equal(wrapped, head.Wrapped);
    }

    [Theory]
    [InlineData("if (value > 5) {", "if (value > 5) {")]
    [InlineData("if value > 5 {", "if (value > 5) {")]
    [InlineData("if ((value > 5)) {", "if (value > 5) {")]
    [InlineData("if (a) && (b) {", "if ((a) && (b)) {")]
    [InlineData("} else if value == 5 {", "} else if (value == 5) {")]
    [InlineData("} else if (value == 5) {", "} else if (value == 5) {")]
    [InlineData("}else{", "} else {")]
    [InlineData("if value > 5 { return 1 }", "if (value > 5) { return 1 }")]
    public void The_formatter_writes_the_parenthesized_head(string line, string expected)
    {
        ConditionSyntax.Head head = ConditionSyntax.Parse(line)!;
        Assert.Null(head.Problem);
        Assert.Equal(expected, ConditionSyntax.Format(head));
        Assert.Equal(expected, ConditionSyntax.Format(ConditionSyntax.Parse(expected)!));   // and writing it again changes nothing
    }

    [Theory]
    [InlineData("let a = 1")]
    [InlineData("emit audio")]
    [InlineData("iffy = 3")]
    [InlineData("elsewhere = 3")]
    [InlineData("return if(a, 1, 2)")]
    [InlineData("}")]
    public void A_line_that_is_not_a_conditional_is_left_alone(string line) => Assert.Null(ConditionSyntax.Parse(line));

    [Theory]
    [InlineData("if (value > 5 {", "never closed")]
    [InlineData("if value > 5) {", "closes a ( that is not there")]
    [InlineData("if () {", "empty")]
    [InlineData("if {", "no condition")]
    [InlineData("} else if () {", "empty")]
    [InlineData("} else if {", "no condition")]
    [InlineData("if value > 5", "no { after")]
    public void A_head_with_a_mistake_says_what_and_where(string line, string expected)
    {
        ConditionSyntax.Head head = ConditionSyntax.Parse(line)!;
        Assert.NotNull(head.Problem);
        Assert.Contains(expected, head.Problem!.Message, StringComparison.Ordinal);
        Assert.InRange(head.Problem.Column, 0, line.Length);
    }

    // ---- the process of an audio component

    private static string Component(string process, string properties = "  property tone: Bool = true\n  property level: Int = 1\n") =>
        "component koda.fx.pick version 1.0.0 current {\n  id \"demo.koda.fx.pick\"\n  input sad: Audio { optional true }\n" + properties
            + "  output audio: Audio\n  process {\n" + process + "    emit bed -> audio\n  }\n}\n";

    private static ComponentDefinition Only(string component) => Assert.Single(Parser.Parse(Header + component, Corpus.Spec, new ParseOptions { RequireProjectLine = false }).Components);

    private static AudioGraph Graph(string component, string tone, string level) =>
        AudioGraphBuilder.Build(Only(component), Corpus.Spec, new Dictionary<string, string>(StringComparer.Ordinal) { ["tone"] = tone, ["level"] = level }, 100);

    private static string Chosen(AudioGraph graph)
    {
        Assert.DoesNotContain(graph.Issues, i => i.Severity == Severity.Error);
        AudioNode only = Assert.Single(graph.Nodes);
        return only.Template == "silence" ? "silence " + (only.Parameters.FirstOrDefault(p => p.Name == "length").Value ?? "auto") : only.Template;
    }

    private static string ThreeBranches(string ifHead, string elseIfHead) =>
        $"    {ifHead}\n      let bed = sad |> gain(-6dB)\n    {elseIfHead}\n      let bed = silence(10s)\n    }} else {{\n      let bed = silence()\n    }}\n";

    public static TheoryData<string, string, string, string, string> Spellings() => new()
    {
        { "if (tone) {", "} else if (level) {", "true", "1", "volume" },
        { "if tone {", "} else if level {", "true", "1", "volume" },
        { "if (tone) {", "} else if level {", "false", "1", "silence 10s" },   // mixed
        { "if tone {", "} else if (level) {", "false", "1", "silence 10s" },   // mixed
        { "if (tone) {", "} else if (level) {", "false", "0", "silence auto" },
        { "if tone {", "} else if level {", "false", "0", "silence auto" },
        { "if((tone)){", "}else if((level)){", "false", "0", "silence auto" },   // doubled and tight
    };

    [Theory]
    [MemberData(nameof(Spellings))]
    public void Both_spellings_of_if_and_else_if_pick_the_same_branch_in_an_audio_process(string ifHead, string elseIfHead, string tone, string level, string expected) =>
        Assert.Equal(expected, Chosen(Graph(Component(ThreeBranches(ifHead, elseIfHead)), tone, level)));

    [Theory]
    [InlineData("level - 1", "1", "silence auto")]   // 0 is false
    [InlineData("level - 1", "3", "volume")]
    [InlineData("(level - 1) * 2", "1", "silence auto")]   // the first ( closes before the end: this is one condition, not a parenthesized one
    [InlineData("(level - 1) * 2", "3", "volume")]
    [InlineData("(level - 1) * (tone + 1)", "3", "volume")]
    [InlineData("int(level / 2)", "1", "silence auto")]
    [InlineData("int(level / 2)", "5", "volume")]
    [InlineData("if(tone, level, 0)", "4", "volume")]   // a function called if is a call, not the keyword
    public void A_condition_with_parentheses_inside_it_is_the_same_with_and_without_the_parentheses_around_it(string condition, string level, string expected)
    {
        string Process(string head) => $"    {head}\n      let bed = sad |> gain(-6dB)\n    }} else {{\n      let bed = silence()\n    }}\n";
        string without = Chosen(Graph(Component(Process($"if {condition} {{")), "true", level));
        string with = Chosen(Graph(Component(Process($"if ({condition}) {{")), "true", level));
        Assert.Equal(expected, without);
        Assert.Equal(expected, with);
    }

    [Theory]
    [InlineData("true", "1", "volume")]
    [InlineData("true", "0", "silence 10s")]
    [InlineData("false", "1", "silence auto")]
    public void Nested_ifs_in_both_spellings_and_mixed(string tone, string level, string expected)
    {
        string nested = "    if tone {\n      if (level) {\n        let bed = sad |> gain(-6dB)\n      } else {\n        let bed = silence(10s)\n" +
            "      }\n    } else {\n      let bed = silence()\n    }\n";
        string parenthesized = "    if (tone) {\n      if (level) {\n        let bed = sad |> gain(-6dB)\n      } else {\n" +
            "        let bed = silence(10s)\n      }\n    } else {\n      let bed = silence()\n    }\n";
        string bare = "    if tone {\n      if level {\n        let bed = sad |> gain(-6dB)\n      } else {\n        let bed = silence(10s)\n      }\n" +
            "    } else {\n      let bed = silence()\n    }\n";
        Assert.Equal(expected, Chosen(Graph(Component(nested), tone, level)));
        Assert.Equal(expected, Chosen(Graph(Component(parenthesized), tone, level)));
        Assert.Equal(expected, Chosen(Graph(Component(bare), tone, level)));
    }

    [Fact]
    public void An_if_with_no_parentheses_around_a_function_call_is_one_condition()
    {
        // int(level / 2) + 1 starts with a name, not with a (; and (level) + (tone) starts with a ( that closes early
        string process = "    if (level) + (tone) {\n      let bed = sad |> gain(-6dB)\n    } else {\n      let bed = silence()\n    }\n";
        Assert.Equal("volume", Chosen(Graph(Component(process), "true", "1")));
        Assert.Equal("volume", Chosen(Graph(Component(process), "false", "2")));
        Assert.Equal("silence auto", Chosen(Graph(Component(process), "false", "0")));
    }

    [Theory]
    [InlineData("    if (tone {\n      let bed = silence()\n    }\n", "never closed")]
    [InlineData("    if tone) {\n      let bed = silence()\n    }\n", "closes a ( that is not there")]
    [InlineData("    if () {\n      let bed = silence()\n    }\n", "empty")]
    [InlineData("    if {\n      let bed = silence()\n    }\n", "no condition")]
    [InlineData("    if tone {\n      let bed = silence()\n    } else if () {\n      let bed = silence()\n    }\n", "empty")]
    [InlineData("    if tone\n      let bed = silence()\n    }\n", "no { after")]
    public void A_mistake_in_a_head_is_one_clear_error_at_its_line_and_the_graph_is_still_read(string process, string expected)
    {
        // a let before the if keeps the rest of the component readable, so that the head is the only thing that is wrong
        AudioGraph graph = Graph(Component("    let bed = silence()\n" + process), "true", "1");
        ComponentIssue issue = Assert.Single(graph.Issues, i => i.Code == "AMP2231" && i.Message.Contains(expected, StringComparison.Ordinal));
        Assert.True(issue.Line >= 0);
        Assert.True(issue.EndCol > issue.Col);
        Assert.DoesNotContain(graph.Issues, i => i.Code == "AMP2232");   // the condition that could not be read is not calculated on top of it
    }

    [Fact]
    public void A_condition_that_cannot_be_calculated_is_the_existing_error_in_both_spellings()
    {
        foreach (string head in new[] { "if tone + {", "if (tone +) {" })
        {
            AudioGraph graph = Graph(Component($"    {head}\n      let bed = silence()\n    }}\n    let bed = silence()\n"), "true", "1");
            Assert.Contains(graph.Issues, i => i.Code == "AMP2232");
        }
    }

    [Fact]
    public void An_else_on_its_own_line_is_named_not_swallowed()
    {
        AudioGraph graph = Graph(Component("    if tone {\n      let bed = silence()\n    }\n    else {\n      let bed = silence()\n    }\n"), "true", "1");
        Assert.Contains(graph.Issues, i => i.Code == "AMP2231" && i.Message.Contains("else", StringComparison.Ordinal));
    }

    // ---- the hash: one component, whichever way its conditions are written; and no hash that exists changes

    private static string SemanticOf(string process)
    {
        ComponentDefinition d = Only(Component(process));
        return ComponentHashing.Semantic(d, []);
    }

    [Fact]
    public void Two_spellings_of_the_same_conditions_are_one_component_for_the_semantic_hash()
    {
        string parenthesized = ThreeBranches("if (tone) {", "} else if (level) {");
        string bare = ThreeBranches("if tone {", "} else if level {");
        string mixed = ThreeBranches("if (tone) {", "} else if level {");
        string doubled = ThreeBranches("if ((tone)) {", "} else if ((level)) {");
        Assert.Equal(SemanticOf(parenthesized), SemanticOf(bare));
        Assert.Equal(SemanticOf(parenthesized), SemanticOf(mixed));
        Assert.NotEqual(SemanticOf(parenthesized), SemanticOf(ThreeBranches("if (tone) {", "} else if (level - 1) {")));   // a different condition is a different component
        _ = doubled;   // extra parentheses are text that was written: they are taken as written, as before (only the form that could not be written before is read as another)
    }

    [Fact]
    public void A_process_that_was_valid_before_keeps_exactly_the_text_that_is_hashed()
    {
        // the text that goes into the hash of a component whose conditions have their parentheses is the text as written, collapsed: the hashes and seals that exist are not touched
        string text = ComponentHashing.SemanticText(Only(Component(ThreeBranches("if (tone) {", "} else if (level) {"))), []);
        Assert.Contains("process=if (tone) { let bed = sad |> gain(-6dB) } else if (level) { let bed = silence(10s) } else { let bed = silence() } emit bed -> audio", text, StringComparison.Ordinal);
        string doubled = ComponentHashing.SemanticText(Only(Component(ThreeBranches("if ((tone)) {", "} else if ((level)) {"))), []);
        Assert.Contains("process=if ((tone)) {", doubled, StringComparison.Ordinal);   // written with two layers: hashed with two layers, as before
        string bare = ComponentHashing.SemanticText(Only(Component(ThreeBranches("if tone {", "} else if level {"))), []);
        Assert.Contains("process=if (tone) { let bed", bare, StringComparison.Ordinal);   // the new form is read as the old one
        Assert.Contains("} else if (level) {", bare, StringComparison.Ordinal);
    }

    // ---- the formatter: parenthesized heads, braces decide the indentation, and writing it again changes nothing

    private const string Song = "composition \"t\" { ample 1  tempo 120  master-seed 1 }\n\n";

    private static string Fmt(string text) => Formatter.Format(text, Corpus.Spec);

    [Fact]
    public void The_formatter_writes_the_parenthesized_head_and_indents_a_helper_by_its_braces()
    {
        string messy = Song + "helper pick(Int minute) -> Int {\n  if minute < 20 {\n  return 3\n  } else if (minute < 40) {\n  return 4\n" +
            "  } else if minute < 60 {\n  return 5\n  } else {\n  return 6\n}\n}\n";
        string expected = Song + "helper pick(Int minute) -> Int {\n  if (minute < 20) {\n    return 3\n  } else if (minute < 40) {\n    return 4\n" +
            "  } else if (minute < 60) {\n    return 5\n  } else {\n    return 6\n  }\n}\n";
        Assert.Equal(expected, Fmt(messy));
        Assert.Equal(expected, Fmt(expected));
    }

    [Fact]
    public void The_formatter_keeps_nested_parentheses_strings_and_comments_of_a_condition()
    {
        string text = Song + "helper f(Int a, Int b, Int c) -> Int {\n  if a > 5 && (b < 10 || c == 3) {   # keep this\n    return 1\n  }\n" +
            "  if ((a > 5)) {\n    return 2\n  }\n  return 0\n}\n";
        string once = Fmt(text);
        Assert.Contains("  if (a > 5 && (b < 10 || c == 3)) {  # keep this\n", once, StringComparison.Ordinal);
        Assert.Contains("  if (a > 5) {\n", once, StringComparison.Ordinal);   // two layers written by hand become one: the same condition
        Assert.Equal(once, Fmt(once));
    }

    [Fact]
    public void The_formatter_does_the_same_in_the_process_of_a_component_and_keeps_it_one_component()
    {
        string bare = Song + Component(ThreeBranches("if tone {", "} else if level {"));
        string parenthesized = Song + Component(ThreeBranches("if (tone) {", "} else if (level) {"));
        string formatted = Fmt(bare);
        Assert.Contains("    if (tone) {\n", formatted, StringComparison.Ordinal);
        Assert.Contains("    } else if (level) {\n", formatted, StringComparison.Ordinal);
        Assert.Equal(formatted, Fmt(formatted));
        Assert.Equal(Fmt(parenthesized), formatted);   // both spellings format to the same text
        Assert.Equal(SemanticOf(ThreeBranches("if tone {", "} else if level {")), ComponentHashing.Semantic(Only(formatted), []));   // and to the same component
    }

    [Fact]
    public void The_formatter_leaves_an_incomplete_head_alone_and_does_not_throw()
    {
        foreach (string head in new[] { "if (value > 5 {", "if value > {", "} else if () {", "if {", "if value > 5" })
        {
            string text = Song + "helper f(Int value) -> Int {\n  " + head + "\n    return 1\n  }\n}\n";
            string once = Fmt(text);
            Assert.Equal(once, Fmt(once));
        }
    }
}
