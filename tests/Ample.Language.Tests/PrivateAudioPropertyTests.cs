namespace Ample.Language.Tests;

/// <summary>
/// WP-P2 of plan/diagnostics/PLAN.md (11g, D36r, D32): <c>private property</c> in an audio component, and the <c>dynamic property</c> mark. A private property is a value of the instance worked out from its default in the
/// order written; only the ones the process reads are run, no instance can give one a value, and a process that reads a dynamic property is a dynamic helper.
/// </summary>
public sealed class PrivateAudioPropertyTests
{
    private const string Header = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n";

    private static string Hush(string properties, string body) =>
        "component koda.fx.hush version 1.0.0 {\n  input audio: Audio\n  property amount: Gain = -3dB\n" + properties + "  output audio: Audio\n  process {\n" + body + "  }\n}\n";

    private static ComponentDefinition Only(string component) => Assert.Single(Parser.Parse(Header + component, Corpus.Spec, new ParseOptions { RequireProjectLine = false }).Components);

    private static AudioGraph Graph(string component) => AudioGraphBuilder.Build(Only(component), Corpus.Spec, new Dictionary<string, string>(StringComparer.Ordinal), 100);

    private static string[] Codes(string component) => [.. Parser.Parse(Header + component, Corpus.Spec).Diagnostics.Select(d => d.Code)];

    private static string Parameters(AudioGraph graph) => string.Join("; ", graph.Nodes.Select(n => $"{n.Template}({string.Join(",", n.Parameters.Select(p => p.Item1 + "=" + p.Item2))})"));

    [Fact]
    public void A_private_property_is_available_in_an_audio_component_and_the_old_error_is_gone()
    {
        string component = Hush("  private property level: Gain = -6dB\n", "    emit audio |> gain(level)\n");
        Assert.DoesNotContain("AMP2263", Codes(component));
        AudioGraph graph = Graph(component);
        Assert.Empty(graph.Issues);
        Assert.Contains("gain=-6", Parameters(graph).Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void A_private_property_is_worked_out_from_the_public_ones_and_the_ones_above_it()
    {
        string component = Hush("  private property base: Gain = -4dB\n  private property level: Gain = base\n", "    emit audio |> gain(level)\n");
        AudioGraph graph = Graph(component);
        Assert.Empty(graph.Issues);
        Assert.Contains("gain=-4", Parameters(graph).Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);   // the second reads the first
    }

    [Fact]
    public void A_private_property_that_nothing_reads_is_metadata_and_never_runs()
    {
        // its value cannot be calculated, but nothing reads it: no error, as in a Notes component (D39r)
        string component = Hush("  private property unused: Int = missing(1)\n", "    emit audio |> gain(amount)\n");
        Assert.Empty(Graph(component).Issues);
        string read = Hush("  private property used: Int = missing(1)\n", "    emit audio |> gain(used)\n");
        Assert.Contains(Graph(read).Issues, i => i.Code == "AMP2232" && i.Message.Contains("private property used", StringComparison.Ordinal));
    }

    [Fact]
    public void An_instance_cannot_give_a_private_property_of_an_audio_component_a_value()
    {
        string component = Hush("  private property level: Gain = -6dB\n", "    emit audio |> gain(level)\n");
        string song = Header + component + "koda.fx.hush quiet at 1 for 1 {\n  level -12dB\n  from loud.audio -> audio\n}\n";
        Assert.Contains(Parser.Parse(song, Corpus.Spec).Diagnostics, d => d.Code == "AMP2261");
    }

    [Fact]
    public void The_private_properties_that_the_process_reads_are_content_of_the_hash_and_the_others_are_not()
    {
        string used = Hush("  private property level: Gain = -6dB\n", "    emit audio |> gain(level)\n");
        string changed = Hush("  private property level: Gain = -9dB\n", "    emit audio |> gain(level)\n");
        Assert.NotEqual(ComponentHashing.Semantic(Only(used), []), ComponentHashing.Semantic(Only(changed), []));
        string metadata = Hush("  private property note: String = \"a\"\n", "    emit audio |> gain(amount)\n");
        string otherMetadata = Hush("  private property note: String = \"b\"\n", "    emit audio |> gain(amount)\n");
        Assert.Equal(ComponentHashing.Semantic(Only(metadata), []), ComponentHashing.Semantic(Only(otherMetadata), []));
        Assert.NotEqual(ComponentHashing.Payload(Only(metadata), []), ComponentHashing.Payload(Only(otherMetadata), []));   // the seal still sees it
    }

    [Fact]
    public void A_dynamic_property_is_parsed_public_and_private_and_a_plain_component_keeps_its_hash()
    {
        ComponentDefinition d = Only(Hush("  dynamic property seed: Int = 1\n  private dynamic property salt: Int = 2\n", "    emit audio |> gain(amount)\n"));
        Assert.True(d.Properties.Single(p => p.Name == "seed").IsDynamic);
        Assert.False(d.Properties.Single(p => p.Name == "amount").IsDynamic);
        Assert.True(d.PrivateProperties.Single().IsDynamic);
        Assert.Empty(Codes(Hush("  dynamic property seed: Int = 1\n", "    emit audio |> gain(amount)\n")));
        string plain = Hush(string.Empty, "    emit audio |> gain(amount)\n");
        Assert.DoesNotContain("dynamic", ComponentHashing.SemanticText(Only(plain), []), StringComparison.Ordinal);
    }

    [Fact]
    public void Dynamic_is_only_allowed_before_property()
    {
        Assert.Contains("AMP2200", Codes(Hush("  dynamic input again: Audio\n", "    emit audio |> gain(amount)\n")));
    }

    private const string NotesComponent =
        "component koda.notes.drift version 1.0.0 {\n  input source: Notes\n{0}\n  output notes: Notes\n\n  process {\n{1}  }\n}\n\n";

    private static string Drift(string properties, string body) => NotesComponent.Replace("{0}", properties, StringComparison.Ordinal).Replace("{1}", body, StringComparison.Ordinal);

    [Fact]
    public void A_process_that_reads_a_dynamic_property_is_a_dynamic_helper_and_one_that_does_not_is_not()
    {
        ComponentDefinition reads = Only(Drift("  dynamic property shift: Int = 0\n", "    emit source |> transpose(shift)\n"));
        Assert.True(ComponentHelper.ReadsDynamicProperty(reads));
        Assert.StartsWith("dynamic helper ", ComponentHelper.Text(reads), StringComparison.Ordinal);
        ComponentDefinition ignores = Only(Drift("  dynamic property shift: Int = 0\n", "    emit source\n"));
        Assert.False(ComponentHelper.ReadsDynamicProperty(ignores));   // passing it along without reading it needs no mark (D32 c)
        Assert.StartsWith("helper ", ComponentHelper.Text(ignores), StringComparison.Ordinal);
        ComponentDefinition viaPrivate = Only(Drift("  dynamic property shift: Int = 0\n  private property total: Int = shift + 1\n", "    emit source |> transpose(total)\n"));
        Assert.True(ComponentHelper.ReadsDynamicProperty(viaPrivate));   // a private default that reads it counts
        ComponentDefinition inString = Only(Drift("  dynamic property shift: Int = 0\n", "    log \"shift is shift\"\n    emit source\n"));
        Assert.False(ComponentHelper.ReadsDynamicProperty(inString));   // a name in a text or an observation is not a read
    }

    [Fact]
    public void The_dynamic_mark_is_part_of_the_meaning_and_so_of_the_hash()
    {
        string plain = Drift("  property shift: Int = 0\n", "    emit source |> transpose(shift)\n");
        string marked = Drift("  dynamic property shift: Int = 0\n", "    emit source |> transpose(shift)\n");
        Assert.NotEqual(ComponentHashing.Semantic(Only(plain), []), ComponentHashing.Semantic(Only(marked), []));
    }

    private static (AmpleDocument Doc, string[] Lines) Open(string component)
    {
        string text = Header + component;
        return (Parser.Parse(text, Corpus.Spec), text.Split('\n'));
    }

    [Fact]
    public void The_hover_on_a_private_property_says_it_is_private_and_on_a_dynamic_one_that_it_makes_the_component_dynamic()
    {
        (AmpleDocument doc, string[] lines) = Open(Hush("  private dynamic property level: Gain = -6dB\n", "    emit audio |> gain(level)\n"));
        int declared = Array.FindIndex(lines, l => l.Contains("private dynamic property", StringComparison.Ordinal)) + 1;
        string? onDeclaration = Queries.HoverAt(doc, Corpus.Spec, declared, lines[declared - 1].IndexOf("level", StringComparison.Ordinal) + 2);
        Assert.Contains("private dynamic property level: Gain = -6dB", onDeclaration, StringComparison.Ordinal);
        Assert.Contains("Private: only the process of this component reads it", onDeclaration, StringComparison.Ordinal);
        Assert.Contains("Reading it makes the component dynamic", onDeclaration, StringComparison.Ordinal);
        int use = Array.FindIndex(lines, l => l.Contains("gain(level)", StringComparison.Ordinal)) + 1;
        Assert.Contains("private dynamic property level", Queries.HoverAt(doc, Corpus.Spec, use, lines[use - 1].IndexOf("level", StringComparison.Ordinal) + 2), StringComparison.Ordinal);
        (AmpleDocument unusedDoc, string[] unusedLines) = Open(Hush("  private property spare: Int = 1\n", "    emit audio |> gain(amount)\n"));
        int spare = Array.FindIndex(unusedLines, l => l.Contains("spare", StringComparison.Ordinal)) + 1;
        Assert.Contains("metadata", Queries.HoverAt(unusedDoc, Corpus.Spec, spare, unusedLines[spare - 1].IndexOf("spare", StringComparison.Ordinal) + 2), StringComparison.Ordinal);
        (AmpleDocument publicDoc, string[] publicLines) = Open(Hush("  dynamic property seed: Int = 1\n", "    emit audio |> gain(amount)\n"));
        int seed = Array.FindIndex(publicLines, l => l.Contains("dynamic property seed", StringComparison.Ordinal)) + 1;
        Assert.Contains("dynamic property seed: Int = 1", Queries.HoverAt(publicDoc, Corpus.Spec, seed, publicLines[seed - 1].IndexOf("seed", StringComparison.Ordinal) + 2), StringComparison.Ordinal);
    }

    [Fact]
    public void The_hover_on_the_component_counts_the_private_properties_without_naming_them()
    {
        (AmpleDocument doc, string[] lines) = Open(Hush("  private property level: Gain = -6dB\n", "    emit audio |> gain(level)\n"));
        int header = Array.FindIndex(lines, l => l.StartsWith("component koda.fx.hush", StringComparison.Ordinal)) + 1;
        string? hover = Queries.HoverAt(doc, Corpus.Spec, header, 12);
        Assert.Contains("Private properties** 1", hover, StringComparison.Ordinal);
        Assert.DoesNotContain("level", hover, StringComparison.Ordinal);
    }

    [Fact]
    public void The_completion_offers_the_two_marks_and_the_types_after_them_and_the_template_has_no_private_property()
    {
        (AmpleDocument doc, string[] lines) = Open(Hush("  \n", "    emit audio |> gain(amount)\n"));
        int blank = Array.FindIndex(lines, l => l == "  ") + 1;
        IReadOnlyList<CompletionItem> statements = Queries.CompletionAt(doc, Corpus.Spec, blank, 3);
        Assert.Contains(statements, i => i.Label == "private property");
        Assert.Contains(statements, i => i.Label == "dynamic property");
        (AmpleDocument typed, string[] typedLines) = Open(Hush("  private dynamic property level: \n", "    emit audio |> gain(amount)\n"));
        int line = Array.FindIndex(typedLines, l => l.Contains("private dynamic property level:", StringComparison.Ordinal)) + 1;
        Assert.Contains(Queries.CompletionAt(typed, Corpus.Spec, line, typedLines[line - 1].Length + 1), i => i.Label == "Int");
        Assert.Equal(["amount"], ComponentChecker.TemplateOf(Only(Hush("  private property level: Gain = -6dB\n", "    emit audio |> gain(level)\n"))).Parameters.Keys);
    }
}
