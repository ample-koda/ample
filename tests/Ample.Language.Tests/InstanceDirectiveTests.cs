namespace Ample.Language.Tests;

/// <summary>
/// WP3 of plan/diagnostics/PLAN.md (F22, D22): the <c>logging.*</c> and <c>diagnostics.*</c> lines of a component instance are options of observation. They are kept apart from the parameters of the block, they travel in
/// a side channel of the expansion, and they are not accepted before the feature that reads them is on.
/// </summary>
public sealed class InstanceDirectiveTests
{
    private const string Header = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n";

    private const string Component =
        "component koda.notes.shifter version 1.0.0 {\n  input source: Notes\n  property steps: Int = 2\n  output notes: Notes\n  process {\n    emit source |> transpose(steps)\n  }\n}\n\n";

    private const string Intro = "midi-clip intro at 1 for 1 {\n  notes 1:0:1:60:100\n}\n\n";

    private static string Song(string lines) => Header + Component + Intro + "koda.notes.shifter up at 2 for 1 {\n  steps 3\n" + lines + "  from intro.notes -> source\n}\n";

    private const SyntaxFeatures On = SyntaxFeatures.Observe | SyntaxFeatures.Assert;

    [Fact]
    public void An_option_on_an_instance_is_not_supported_before_its_feature_is_on()
    {
        using IDisposable none = SyntaxGate.Use(SyntaxFeatures.None);
        AmpleDocument doc = Parser.Parse(Song("  logging.level trace\n  diagnostics.assertions on\n"), Corpus.Spec);
        Assert.Equal(2, doc.Diagnostics.Count(d => d.Code == "AMP6020"));
        Assert.Empty(doc.Blocks.First(b => b.Name == "up").Directives);   // never accepted and then ignored
    }

    [Fact]
    public void With_the_features_on_the_options_are_directives_and_never_parameters()
    {
        using IDisposable scope = SyntaxGate.Use(On);
        AmpleDocument doc = Parser.Parse(Song("  logging.level trace\n  diagnostics.assertions on\n"), Corpus.Spec);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        BlockNode up = doc.Blocks.First(b => b.Name == "up");
        Assert.Equal(["steps"], up.Parameters.Select(p => p.Name));   // the parameters are the same as without the options
        Assert.Equal([("logging.level", "trace"), ("diagnostics.assertions", "on")], up.Directives.Select(d => (d.Name, d.Value)));
        Assert.Equal([18, 19], up.Directives.Select(d => d.Line));   // the lines are known, for the question why an option is on
    }

    [Fact]
    public void The_parameters_of_the_block_are_identical_with_and_without_the_options()
    {
        using IDisposable scope = SyntaxGate.Use(On);
        BlockNode plain = Parser.Parse(Song(string.Empty), Corpus.Spec).Blocks.First(b => b.Name == "up");
        BlockNode noisy = Parser.Parse(Song("  logging.level trace\n"), Corpus.Spec).Blocks.First(b => b.Name == "up");
        Assert.Equal(plain.Parameters.Select(p => (p.Name, p.Value)), noisy.Parameters.Select(p => (p.Name, p.Value)));
        Assert.Equal(plain.Froms.Select(f => (f.Block, f.Input)), noisy.Froms.Select(f => (f.Block, f.Input)));
    }

    [Fact]
    public void The_expansion_carries_the_options_by_instance_and_the_generated_block_has_none_of_them()
    {
        using IDisposable scope = SyntaxGate.Use(On);
        ExpandedSong expanded = ModuleExpander.Expand(Song("  logging.level trace\n"), Corpus.Spec, _ => null);
        BlockDirective directive = Assert.Single(expanded.Directives["up"]);
        Assert.Equal(("logging.level", "trace"), (directive.Name, directive.Value));
        Assert.DoesNotContain("logging", expanded.Text.Split("midi-clip up", 2)[1], StringComparison.Ordinal);   // the lowered clip has no such line
        Assert.Empty(ModuleExpander.Expand(Song(string.Empty), Corpus.Spec, _ => null).Directives);
        Assert.Equal(ModuleExpander.Expand(Song(string.Empty), Corpus.Spec, _ => null).Text, expanded.Text);   // the lowered song is the same text with or without the option: nothing a take plan could read
    }

    [Fact]
    public void Expanded_evaluation_resolves_each_instances_level_and_keeps_the_musical_parameters()
    {
        using IDisposable scope = SyntaxGate.Use(On);
        string MakeSong(string level) => Song("  logging.level " + level + "\n").Replace(
            "    emit source", "    debug \"inside\"\n    emit source", StringComparison.Ordinal);
        AmpleDocument Run(string level, ConfigLayerInput? session)
        {
            ExpandedSong expanded = ModuleExpander.Expand(MakeSong(level), Corpus.Spec, _ => null);
            return Parser.Parse(expanded.Text, Corpus.Spec, new ParseOptions
            {
                ApplyNoteFlow = true,
                InstanceDirectives = expanded.Directives,
                ObservationOverride = session,
            });
        }

        AmpleDocument observed = Run("debug", null);
        AmpleDocument quiet = Run("off", null);
        Assert.DoesNotContain(observed.Diagnostics, diagnostic => diagnostic.Severity == Severity.Error);
        LogEvent entry = Assert.Single(observed.Observations);
        Assert.Equal("inside", entry.Message);
        Assert.EndsWith("|up", entry.SiteKey, StringComparison.Ordinal);
        Assert.Empty(quiet.Observations);
        Assert.Equal(
            observed.Blocks.Single(b => b.Name == "up").Parameters.Select(p => (p.Name, p.Value)),
            quiet.Blocks.Single(b => b.Name == "up").Parameters.Select(p => (p.Name, p.Value)));
        ConfigLayerInput sessionOff = new(new ConfigSource(ConfigLayer.Session),
            new Dictionary<string, (string Value, int Line)> { ["logging.level"] = ("off", 0) });
        Assert.Empty(Run("debug", sessionOff).Observations);
    }

    [Fact]
    public void A_private_property_used_only_in_observation_reads_the_clock_inside_the_fork()
    {
        using IDisposable scope = SyntaxGate.Use(On | SyntaxFeatures.Record);
        int clockReads = 0;
        AmpleDocument Run(string level)
        {
            string text = Song("  logging.level " + level + "\n")
                .Replace("  output notes:", "  private property seenHour: Int = currentTime().hour\n  output notes:", StringComparison.Ordinal)
                .Replace("    emit source", "    debug(seenHour, context={again: seenHour})\n    emit source", StringComparison.Ordinal);
            ExpandedSong expanded = ModuleExpander.Expand(text, Corpus.Spec, _ => null);
            return Parser.Parse(expanded.Text, Corpus.Spec, new ParseOptions
            {
                ApplyNoteFlow = true,
                InstanceDirectives = expanded.Directives,
                Clock = () =>
                {
                    clockReads++;
                    return new DateTime(2026, 11, 16, 13, 0, 0, DateTimeKind.Utc);
                },
            });
        }

        AmpleDocument quiet = Run("off");
        Assert.Empty(quiet.Observations);
        Assert.Equal(1, clockReads); // The parse records its own timestamp in both runs.
        AmpleDocument observed = Run("debug");
        Assert.DoesNotContain(observed.Diagnostics, diagnostic => diagnostic.Severity == Severity.Error);
        LogEvent entry = Assert.Single(observed.Observations);
        Assert.Equal("13", entry.Message);
        Assert.Equal("{\"again\":13}", entry.ContextJson);
        Assert.True(entry.Volatile);
        Assert.Equal(3, clockReads);
    }

    [Fact]
    public void An_observation_in_an_imported_component_points_to_its_own_source_file()
    {
        using IDisposable scope = SyntaxGate.Use(On);
        const string path = "fx.amplec";
        string component = Component.Replace("    emit source", "    warn \"inside\"\n    emit source", StringComparison.Ordinal);
        string text = Song(string.Empty).Replace(Component, "import \"fx.amplec\"\n\n", StringComparison.Ordinal);
        ComponentRegistryResult registry = ComponentRegistry.Resolve(text, "song", name => name == path ? component : null, Corpus.Spec);
        Assert.Empty(registry.Issues);
        ExpandedSong expanded = ModuleExpander.Expand(text, Corpus.Spec, _ => null, registry.Sources);
        AmpleDocument result = Parser.Parse(expanded.Text, Corpus.Spec, new ParseOptions
        {
            ApplyNoteFlow = true,
            ObservationHelperSites = expanded.ObservationHelperSites,
        });
        LogEvent entry = Assert.Single(result.Observations);
        Assert.NotNull(entry.Site);
        Assert.Equal(path, entry.Site.Origin);
        Assert.Equal(6, entry.Site.Line);
        Assert.EndsWith("|up", entry.SiteKey, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wrong_value_and_a_block_that_is_not_an_instance_get_a_clear_error()
    {
        using IDisposable scope = SyntaxGate.Use(On);
        Assert.Contains(Parser.Parse(Song("  logging.level loud\n"), Corpus.Spec).Diagnostics, d => d.Code == "AMP6030");
        AmpleDocument native = Parser.Parse(Header + "reverb room {\n  logging.level trace\n}\n", Corpus.Spec);
        Assert.Contains(native.Diagnostics, d => d.Code == "AMP6032");
    }
}
