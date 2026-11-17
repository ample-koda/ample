namespace Ample.Language.Tests;

/// <summary>
/// WP1b of plan/diagnostics/PLAN.md: the formatter derives the form of an observation statement from its shape, and observation and assert statements are not part of the semantic hash of a helper or a component
/// but are part of its source tier (payload hash, seal), without changing any hash that exists.
/// </summary>
public sealed class ObservationFormatHashTests
{
    private const SyntaxFeatures On = SyntaxFeatures.Observe | SyntaxFeatures.Assert | SyntaxFeatures.Record;

    private const string Header = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n";

    private static string Fmt(string statement, string indent = "  ")
    {
        string song = Header + "helper f(Notes n) -> Notes {\n" + indent + statement + "\n  return n\n}\n";
        string formatted = Formatter.Format(song, Corpus.Spec);
        Assert.Equal(formatted, Formatter.Format(formatted, Corpus.Spec));   // idempotent
        string[] lines = formatted.Split('\n');
        int first = Array.FindIndex(lines, l => l.StartsWith("helper f", StringComparison.Ordinal)) + 1;
        int last = Array.FindIndex(lines, l => l.StartsWith("  return n", StringComparison.Ordinal));
        return string.Join('\n', lines[first..last]);
    }

    [Theory]
    [InlineData("log \"Hello\"", "  log \"Hello\"")]
    [InlineData("log(\"Hello\")", "  log \"Hello\"")]
    [InlineData("log (\"Hello\")", "  log \"Hello\"")]
    [InlineData("debug   voices", "  debug voices")]
    [InlineData("trace(voices * 2)", "  trace voices * 2")]
    [InlineData("assert voices > 0, \"Too many\"", "  assert voices > 0, \"Too many\"")]
    [InlineData("assert(voices <= 8, \"Too many\")", "  assert voices <= 8, \"Too many\"")]
    [InlineData("log (a + b) * 2", "  log((a + b) * 2)")]
    [InlineData("log((a + b) * 2)", "  log((a + b) * 2)")]
    [InlineData("warn \"x\", code=\"C\"", "  warn(\"x\", code=\"C\")")]
    [InlineData("warn(\"x\", context={a: 1})", "  warn(\"x\", context={a: 1})")]
    [InlineData("log \"x\"   # why", "  log \"x\"  # why")]
    [InlineData("log(\"x\")   # why", "  log \"x\"  # why")]
    public void The_form_follows_the_shape_of_the_statement(string written, string expected)
    {
        using IDisposable scope = SyntaxGate.Use(On);
        Assert.Equal(expected, Fmt(written));
    }

    [Fact]
    public void A_record_with_several_fields_or_a_nested_record_goes_on_several_lines()
    {
        using IDisposable scope = SyntaxGate.Use(On);
        string expected = "  warn(\n    \"Unknown preset\",\n    code=\"PRESET_UNKNOWN\",\n    context={preset: voices, fallback: \"normal\"}\n  )";
        Assert.Equal(expected, Fmt("warn \"Unknown preset\", code=\"PRESET_UNKNOWN\", context={preset: voices, fallback: \"normal\"}"));
        Assert.Equal(expected, Fmt("warn(\"Unknown preset\", code=\"PRESET_UNKNOWN\", context={preset: voices, fallback: \"normal\"})"));
        string nested = "  log(\n    \"x\",\n    context={a: {b: 1}}\n  )";
        Assert.Equal(nested, Fmt("log \"x\", context={a: {b: 1}}"));
    }

    [Fact]
    public void A_statement_that_does_not_fit_goes_on_several_lines_and_the_result_is_stable()
    {
        using IDisposable scope = SyntaxGate.Use(On);
        string longMessage = "\"" + new string('a', 120) + "\"";
        string result = Fmt("log " + longMessage + ", code=\"A\"");
        Assert.StartsWith("  log(\n", result, StringComparison.Ordinal);
        Assert.EndsWith("\n  )", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_multi_line_statement_keeps_its_arguments_and_gets_the_indentation_of_its_depth()
    {
        using IDisposable scope = SyntaxGate.Use(On);
        string result = Fmt("if voices > 1 {\n    warn(\n          \"x\",\n    code=\"C\",\n   context={a: 1, b: 2}\n  )\n  }");
        Assert.Contains("    warn(\n      \"x\",\n      code=\"C\",\n      context={a: 1, b: 2}\n    )", result, StringComparison.Ordinal);
    }

    [Fact]
    public void With_the_feature_off_the_formatter_leaves_a_statement_as_written()
    {
        using IDisposable none = SyntaxGate.Use(SyntaxFeatures.None);
        Assert.Equal("  assert(true)", Fmt("assert(true)"));
    }

    // ---- hashes

    private static NotePipeline.HelperTable Table(params string[] decls) => NotePipeline.HelperTable.Build(decls.Select((d, i) => (d, i * 20)), []);

    private const string Plain = "helper pick(Int n) -> Int {\n  if n > 2 { return 3 }\n  return 4\n}";

    private const string Observed = "helper pick(Int n) -> Int {\n  log \"checking\", context={n: n}\n  if n > 2 {\n    debug(\"big\")\n    return 3\n  }\n  assert n >= 0, \"negative\"\n  return 4\n}";

    [Fact]
    public void Observation_and_assert_statements_are_not_part_of_the_semantic_hash_of_a_helper()
    {
        using IDisposable scope = SyntaxGate.Use(On);
        IReadOnlyList<string> plain = Table(Plain).ClosureHashes("pick(1)");
        IReadOnlyList<string> observed = Table(Observed).ClosureHashes("pick(1)");
        Assert.Equal(plain.Where(h => !h.StartsWith("source:", StringComparison.Ordinal)), observed.Where(h => !h.StartsWith("source:", StringComparison.Ordinal)));
        Assert.DoesNotContain(plain, h => h.StartsWith("source:", StringComparison.Ordinal));
        Assert.Single(observed, h => h.StartsWith("source:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_text_without_observation_hashes_exactly_as_before_whatever_is_switched_on()
    {
        IReadOnlyList<string> off = Table(Plain).ClosureHashes("pick(1)");
        using IDisposable scope = SyntaxGate.Use(On);
        Assert.Equal(off, Table(Plain).ClosureHashes("pick(1)"));
        Assert.Equal(off, Table(Plain.Replace("\n", "\n\n  ", StringComparison.Ordinal) + "   # a comment").ClosureHashes("pick(1)"));   // whitespace and comments were never part of it
    }

    [Fact]
    public void A_different_message_or_another_spelling_is_the_same_semantic_helper_but_another_source()
    {
        using IDisposable scope = SyntaxGate.Use(On);
        IReadOnlyList<string> a = Table(Observed).ClosureHashes("pick(1)");
        IReadOnlyList<string> b = Table(Observed.Replace("log \"checking\", context={n: n}", "log(\"checking again\")", StringComparison.Ordinal)).ClosureHashes("pick(1)");
        Assert.Equal(a.Where(h => !h.StartsWith("source:", StringComparison.Ordinal)), b.Where(h => !h.StartsWith("source:", StringComparison.Ordinal)));
        Assert.NotEqual(a.Single(h => h.StartsWith("source:", StringComparison.Ordinal)), b.Single(h => h.StartsWith("source:", StringComparison.Ordinal)));
        // a statement that changes the music changes the semantic hash
        IReadOnlyList<string> c = Table(Observed.Replace("return 4", "return 5", StringComparison.Ordinal)).ClosureHashes("pick(1)");
        Assert.NotEqual(a[0], c[0]);
    }

    [Fact]
    public void A_helper_that_only_a_log_calls_is_not_in_the_semantic_closure_but_is_in_the_source_tier()
    {
        using IDisposable scope = SyntaxGate.Use(On);
        string helper = "helper shown(Int n) -> Int { return n + 1 }";
        IReadOnlyList<string> closure = Table(helper, "helper g(Int n) -> Int {\n  log shown(n)\n  return n\n}").ClosureHashes("g(1)");
        Assert.Equal(2, closure.Count(h => h.StartsWith("source:", StringComparison.Ordinal)));
        Assert.Single(closure, h => !h.StartsWith("source:", StringComparison.Ordinal));
    }

    private static ComponentDefinition Only(string text) => Assert.Single(Parser.Parse(Header + text, Corpus.Spec, new ParseOptions { RequireProjectLine = false }).Components);

    private static string Echo(string process) =>
        "component koda.notes.echo version 1.0.0 {\n  input source: Notes\n  output notes: Notes\n  process {\n" + process + "\n  }\n}\n\n";

    [Fact]
    public void A_log_in_a_component_keeps_the_semantic_hash_and_changes_the_payload_hash_and_voids_a_seal()
    {
        using IDisposable scope = SyntaxGate.Use(On);
        ComponentDefinition plain = Only(Echo("    emit source |> transpose(+7)"));
        ComponentDefinition observed = Only(Echo("    log \"echo\"\n    emit source |> transpose(+7)"));
        Assert.Equal(ComponentHashing.Semantic(plain, []), ComponentHashing.Semantic(observed, []));
        Assert.NotEqual(ComponentHashing.Payload(plain, []), ComponentHashing.Payload(observed, []));
        Assert.DoesNotContain("process-source", ComponentHashing.SemanticText(observed, []), StringComparison.Ordinal);

        SealCheck now = ComponentHashing.Check(plain, []);
        string sealedText = Echo("    emit source |> transpose(+7)").TrimEnd().TrimEnd('}').TrimEnd() + "\n" + string.Join('\n', ComponentHashing.SealLines(now.Payload, now.Semantic)) + "\n}\n\n";
        Assert.Equal(SealState.Valid, ComponentHashing.Check(Only(sealedText), []).State);
        SealCheck voided = ComponentHashing.Check(Only(sealedText.Replace("    emit source", "    log \"echo\"\n    emit source", StringComparison.Ordinal)), []);
        Assert.Equal(SealState.Invalid, voided.State);
        Assert.Contains("logging and assertion statements", voided.Reason, StringComparison.Ordinal);
        Assert.Equal(now.Semantic, voided.Semantic);   // the music is the same
    }

    [Fact]
    public void A_component_without_observation_keeps_its_payload_hash_text()
    {
        ComponentDefinition plain = Only(Echo("    emit source |> transpose(+7)"));
        Assert.DoesNotContain("process-source", ComponentHashing.SemanticText(plain, []), StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_extra_in_the_dependencies_is_read_by_the_payload_only()
    {
        ComponentDefinition plain = Only(Echo("    emit source |> transpose(+7)"));
        string[] extra = ["sha256:aaa", "source:sha256:bbb"];
        Assert.Equal(ComponentHashing.Semantic(plain, ["sha256:aaa"]), ComponentHashing.Semantic(plain, extra));
        Assert.NotEqual(ComponentHashing.Payload(plain, ["sha256:aaa"]), ComponentHashing.Payload(plain, extra));
    }
}
