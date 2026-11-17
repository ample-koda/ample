namespace Ample.Language.Tests;

/// <summary>WP7 of plan/diagnostics/PLAN.md (R1.7): the process of an audio component reads observation statements with the shared parser; warn reaches the diagnostics of the instance, the others say they are not available yet, and none of it changes the audio graph or its hash.</summary>
public sealed class AudioObservationTests
{
    private const string Header = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n";

    private static string Component(string extra) => "component koda.fx.echo version 1.0.0 current {\n  id \"demo.koda.fx.echo\"\n  input audio: Audio\n  property room: Percent = 40%\n  property label: String = \"quiet\"\n  output audio: Audio\n  process {\n" + extra + "    emit audio\n      |> reverb(size=room)\n  }\n}\n\n";

    private const string Source = "midi-clip melody at 1 for 1 {\n  notes 1:0:1:60:100\n}\n\npiano lead at 1 for 1 {\n  from melody.notes -> notes\n}\n\n";

    private const string Song = "midi-clip melody at 1 for 1 {\n  notes 1:0:1:60:100\n}\n\npiano lead at 1 for 1 {\n  from melody.notes -> notes\n}\n\nkoda.fx.echo space {\n  from lead.audio -> audio\n}\n";

    private static (AmpleDocument Doc, ComponentDefinition Definition) Parse(string extra)
    {
        AmpleDocument doc = Parser.Parse(Header + Component(extra) + Song, Corpus.Spec);
        return (doc, doc.Components.Single());
    }

    private static IEnumerable<ModuleDiagnostic> Expansion(string extra) => ComponentExpander.Expand(Header + Component(extra) + Song, Corpus.Spec).Diagnostics;

    [Fact]
    public void A_warn_in_an_audio_process_becomes_a_warning_of_the_instance_with_its_code()
    {
        ModuleDiagnostic warning = Assert.Single(Expansion("    warn \"The room is large\", code=\"ROOM_LARGE\"\n"), d => d.Code == "AMP6001");
        Assert.Equal(Severity.Warning, warning.Severity);
        Assert.Contains("The room is large [ROOM_LARGE]", warning.Message, StringComparison.Ordinal);
        Assert.Contains("space", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_warn_that_reads_a_property_shows_its_value()
    {
        ModuleDiagnostic warning = Assert.Single(Expansion("    warn label\n"), d => d.Code == "AMP6001");
        Assert.Contains("quiet", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_warn_in_a_branch_that_is_not_taken_says_nothing()
    {
        Assert.DoesNotContain(Expansion("    if (room > 90%) {\n      warn \"too much\"\n    }\n"), d => d.Code == "AMP6001");
    }

    private static ComponentExpansion ExpandWith(string settings, string extra) => ComponentExpander.Expand(Header + settings + Component(extra) + Song, Corpus.Spec);

    [Fact]
    public void Log_debug_and_trace_are_events_of_the_log_view_at_their_level_and_never_diagnostics()
    {
        ComponentExpansion expansion = ExpandWith("settings { logging.level trace }\n\n", "    log \"a\"\n    debug room\n    trace \"c\", code=\"C\"\n");
        Assert.Equal([EventLevel.Info, EventLevel.Debug, EventLevel.Trace], expansion.Observations.Select(e => e.Level));
        Assert.Equal("40%", expansion.Observations[1].Message);   // debug room shows the value of the property
        Assert.DoesNotContain(expansion.Diagnostics, d => d.Code is "AMP6001" or "AMP6013");
    }

    [Fact]
    public void The_level_of_the_song_decides_what_is_registered_and_the_default_registers_no_log()
    {
        Assert.Empty(ExpandWith(string.Empty, "    log \"a\"\n    debug \"b\"\n    trace \"c\"\n").Observations);
        ComponentExpansion info = ExpandWith("settings { logging.level info }\n\n", "    log \"a\"\n    debug \"b\"\n    trace \"c\"\n");
        Assert.Equal(EventLevel.Info, Assert.Single(info.Observations).Level);
    }

    [Fact]
    public void A_warning_is_registered_at_every_level_even_off()
    {
        ComponentExpansion off = ExpandWith("settings { logging.level off }\n\n", "    warn \"w\"\n    log \"l\"\n");
        Assert.Equal(EventLevel.Warning, Assert.Single(off.Observations).Level);
    }

    [Fact]
    public void A_statement_that_is_not_an_observation_is_still_refused_as_before()
    {
        Assert.Contains(Expansion("    banana 3\n"), d => d.Code == "AMP2231");
    }

    [Fact]
    public void The_graph_and_the_semantic_hash_are_the_same_with_or_without_a_warn()
    {
        (_, ComponentDefinition plain) = Parse(string.Empty);
        (_, ComponentDefinition warned) = Parse("    warn \"hello\"\n");
        string Semantic(ComponentDefinition d) => ComponentHashing.Semantic(d, ComponentHashing.DependenciesOf(d, Corpus.Spec, () => []));
        Assert.Equal(Semantic(plain), Semantic(warned));
        AudioGraph a = AudioGraphBuilder.Build(plain, Corpus.Spec, new Dictionary<string, string>(StringComparer.Ordinal), 100);
        AudioGraph b = AudioGraphBuilder.Build(warned, Corpus.Spec, new Dictionary<string, string>(StringComparer.Ordinal), 100);
        Assert.Equal(AudioGraphBuilder.ParameterText(a.Nodes[0]), AudioGraphBuilder.ParameterText(b.Nodes[0]));
        Assert.Single(b.Warnings);
        Assert.Empty(a.Warnings);
    }

    private static string WithProperty(string name, string value)
    {
        const string head = "component koda.fx.echo version 1.0.0 current {\n  id \"demo.koda.fx.echo\"\n  input audio: Audio\n";
        const string tail = "  output audio: Audio\n  process {\n    warn ";
        return head + "  property " + name + ": String = \"" + value + "\"\n" + tail + name + "\n    emit audio\n  }\n}\n\n";
    }

    [Fact]
    public void A_secret_named_property_is_not_shown_in_a_warning_and_an_ordinary_one_is()
    {
        IEnumerable<ModuleDiagnostic> secret = ComponentExpander.Expand(Header + WithProperty("token", "very-private") + Song, Corpus.Spec).Diagnostics;
        ModuleDiagnostic shown = Assert.Single(secret, d => d.Code == "AMP6001");
        Assert.DoesNotContain("very-private", shown.Message, StringComparison.Ordinal);
        Assert.Contains("[redacted]", shown.Message, StringComparison.Ordinal);
        IEnumerable<ModuleDiagnostic> plain = ComponentExpander.Expand(Header + WithProperty("label", "plain-text") + Song, Corpus.Spec).Diagnostics;
        Assert.Contains("plain-text", Assert.Single(plain, d => d.Code == "AMP6001").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_message_is_cut_like_every_other_event_message_and_a_code_must_be_a_name()
    {
        string longText = new('x', 350);   // longer than 400 is refused by the expression reader already (AMP6013)
        ModuleDiagnostic cut = Assert.Single(Expansion("    warn \"" + longText + "\", code=\"not a name\"\n"), d => d.Code == "AMP6001");
        Assert.True(cut.Message.Length < 400, cut.Message.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.DoesNotContain("[not a name]", cut.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_warnings_of_one_process_are_bounded_and_the_rest_is_counted_once()
    {
        string many = string.Concat(Enumerable.Range(0, 30).Select(n => "    warn \"w" + n + "\"\n"));
        List<ModuleDiagnostic> all = [.. Expansion(many)];
        Assert.Equal(AudioGraphBuilder.MaxWarnings, all.Count(d => d.Code == "AMP6001"));
        ModuleDiagnostic note = Assert.Single(all, d => d.Code == "AMP6007");
        Assert.Contains("10 more warnings", note.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_warnings_are_events_of_the_expansion_with_the_component_as_origin_and_the_instance_in_the_stack()
    {
        ComponentExpansion expansion = ComponentExpander.Expand(Header + Component("    warn \"hello\", code=\"HELLO\"\n") + Song, Corpus.Spec);
        LogEvent e = Assert.Single(expansion.Observations);
        Assert.Equal(EventLevel.Warning, e.Level);
        Assert.Equal("HELLO", e.Code);
        Assert.Equal("component:koda.fx.echo", e.Site!.Origin);
        Assert.Equal("space", Assert.Single(e.Stack).Instance);
    }

    [Fact]
    public void Many_instances_stay_inside_the_diagnostics_budget_of_the_sink_and_the_rest_is_counted_once()
    {
        string instances = string.Concat(Enumerable.Range(0, 230).Select(n => "koda.fx.echo i" + n + " {\n  from lead.audio -> audio\n}\n\n"));
        ComponentExpansion expansion = ComponentExpander.Expand(Header + Component("    warn \"w\"\n") + Source + instances, Corpus.Spec);
        int budget = new ObservationBudgets().DiagnosticMaxTotal;
        Assert.Equal(budget, expansion.Diagnostics.Count(d => d.Code == "AMP6001"));
        Assert.Equal(budget, expansion.Observations.Count);
        Assert.Contains(expansion.Diagnostics, d => d.Code == "AMP6007");
    }

    [Fact]
    public void A_session_layer_wins_over_the_song_for_the_events_of_an_audio_process()
    {
        ConfigLayerInput session = new(new ConfigSource(ConfigLayer.Session, "session"), new Dictionary<string, (string, int)> { ["logging.level"] = ("trace", 0) });
        string song = Header + "settings { logging.level off }\n\n" + Component("    trace \"t\"\n") + Song;
        Assert.Empty(ComponentExpander.Expand(song, Corpus.Spec).Observations);
        Assert.Equal(EventLevel.Trace, Assert.Single(ComponentExpander.Expand(song, Corpus.Spec, null, session).Observations).Level);
    }

    // ---- WP13: the message is an expression of the shared evaluator

    [Fact]
    public void The_message_is_an_expression_over_the_properties()
    {
        ComponentExpansion expansion = ExpandWith("settings { logging.level debug }\n\n", "    debug room * 2\n    log {room: room, label: label}\n");
        Assert.Equal(2, expansion.Observations.Count);
        Assert.Equal("80%", expansion.Observations[0].Message);
        Assert.Contains("40%", expansion.Observations[1].Message, StringComparison.Ordinal);
        Assert.Contains("quiet", expansion.Observations[1].Message, StringComparison.Ordinal);
        Assert.DoesNotContain(expansion.Diagnostics, d => d.Code == "AMP6013");
    }

    [Fact]
    public void A_statement_may_span_lines()
    {
        ModuleDiagnostic warning = Assert.Single(Expansion("    warn(\n      \"The room is large\",\n      code=\"ROOM_LARGE\"\n    )\n"), d => d.Code == "AMP6001");
        Assert.Contains("The room is large [ROOM_LARGE]", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_named_property_is_redacted_inside_an_expression_too()
    {
        string component = "component koda.fx.echo version 1.0.0 current {\n  id \"demo.koda.fx.echo\"\n  input audio: Audio\n  property token: String = \"very-private\"\n  output audio: Audio\n  process {\n    warn {key: token}\n    emit audio\n  }\n}\n\n";
        ModuleDiagnostic shown = Assert.Single(ComponentExpander.Expand(Header + component + Song, Corpus.Spec).Diagnostics, d => d.Code == "AMP6001");
        Assert.DoesNotContain("very-private", shown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_that_reads_something_outside_the_properties_is_AMP6013()
    {
        ModuleDiagnostic problem = Assert.Single(Expansion("    warn missing * 2\n"), d => d.Code == "AMP6013");
        Assert.Contains("cannot be evaluated", problem.Message, StringComparison.Ordinal);
        Assert.Single(Expansion("    warn environment(\"HOME\")\n"), d => d.Code == "AMP6012");   // the machine is not visible from an observation, as in a helper
    }
}
