namespace Ample.Language.Tests;

/// <summary>Components (docs/language/19-components.md): versions, the declaration, its checks, the instances, what an instance makes, the hashes and the seal.</summary>
public sealed class ComponentTests
{
    private const string Header = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n";

    private const string Triple = "/** Repeats the notes. */\ncomponent koda.notes.tripleNotes version 1.0.0 {\n  input source: Notes {\n    optional true\n    fallback localNotes\n  }\n\n  property localNotes: Notes {\n    editor jam\n  }\n\n  #[MinValue(1), MaxValue(16), Step(1)]\n  property copies: Int = 3\n\n  property spacing: Duration = 1bar\n\n  output notes: Notes\n\n  process {\n    let n = connected(source) ? source : localNotes\n    for i in 0..copies {\n      emit n |> shift(i * spacing)\n    }\n  }\n}\n\n";

    private const string Intro = "midi-clip intro at 1 for 1 {\n  notes 1:0:1:60:100 1:1:1:62:100\n}\n\n";

    private const string Instance = "koda.notes.tripleNotes introTriple at 2 for 3 {\n  copies 3\n  spacing 1bar\n  from intro.notes -> source\n}\n";

    private static string Song(string components = Triple, string instances = Instance) => Header + components + Intro + instances;

    private static ComponentDefinition Only(string text) => Assert.Single(Parser.Parse(text, Corpus.Spec).Components);

    private static int Notes(string expandedText, string block)
    {
        AmpleDocument doc = Parser.Parse(expandedText, Corpus.Spec, new ParseOptions { ApplyNoteFlow = true });
        string notes = doc.Blocks.First(b => b.Name == block).Parameters.First(p => p.Name == "notes").Value;
        return notes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    [Fact]
    public void Versions_are_ordered_like_semver_and_a_request_matches_a_line_a_range_or_one_exact_version()
    {
        Assert.True(SemVer.Parse("1.10.0") > SemVer.Parse("1.9.9"));
        Assert.True(SemVer.Parse("4.0.0-beta.2") > SemVer.Parse("4.0.0-beta.1"));
        Assert.True(SemVer.Parse("4.0.0-beta.1") < SemVer.Parse("4.0.0"));
        Assert.Equal("1.8.0", SemVer.Parse("1.8").ToString());
        Assert.False(SemVer.TryParse("one", out _));
        VersionRequest line = VersionRequestOf("1.8");
        Assert.True(line.Matches(SemVer.Parse("1.8.4")));
        Assert.False(line.Matches(SemVer.Parse("1.9.0")));
        VersionRequest compatible = VersionRequestOf("^1.2");
        Assert.True(compatible.Matches(SemVer.Parse("1.7.1")));
        Assert.False(compatible.Matches(SemVer.Parse("2.0.0")));
        Assert.False(compatible.Matches(SemVer.Parse("1.1.9")));
        VersionRequest patch = VersionRequestOf("~1.2");
        Assert.True(patch.Matches(SemVer.Parse("1.2.9")));
        Assert.False(patch.Matches(SemVer.Parse("1.3.0")));
        VersionRequest exactPre = VersionRequestOf("4.0.0-beta.1");
        Assert.True(exactPre.Matches(SemVer.Parse("4.0.0-beta.1")));
        VersionRequest range = VersionRequestOf("^4.0");
        Assert.False(range.Matches(SemVer.Parse("4.0.0-beta.1")));   // a pre-release only comes when it is asked for by name
    }

    private static ComponentDefinition Version(string version, bool current = false) => Only(Header + Triple.Replace("1.0.0", version, StringComparison.Ordinal).Replace("component koda.notes.tripleNotes version " + version + " {", "component koda.notes.tripleNotes version " + version + (current ? " current" : string.Empty) + " {", StringComparison.Ordinal));

    [Fact]
    public void The_default_version_is_the_current_one_else_the_highest_release_and_never_a_pre_release_by_surprise()
    {
        ComponentDefinition v3 = Version("3.8.0"), beta = Version("4.0.0-beta.1"), old = Version("1.8.0"), current = Version("2.3.1", current: true);
        Assert.Same(v3, ComponentVersions.Default([old, v3, beta]));
        Assert.Same(current, ComponentVersions.Default([old, v3, current, beta]));   // current is an override of the highest release
        Assert.Same(beta, ComponentVersions.Default([beta]));   // only pre-releases exist
        Assert.Same(old, ComponentVersions.Pick([old, v3], VersionRequestOf("1.8")));
        Assert.Null(ComponentVersions.Pick([old, v3], VersionRequestOf("5")));
    }

    private static VersionRequest VersionRequestOf(string text)
    {
        Assert.True(VersionRequest.TryParse(text, out VersionRequest request));
        return request;
    }

    [Fact]
    public void A_component_is_read_with_its_contract_its_attributes_its_doc_comment_and_its_process()
    {
        ComponentDefinition d = Only(Song());
        Assert.Equal("koda.notes.tripleNotes", d.Name);
        Assert.Equal("local:koda.notes.tripleNotes", d.Id);
        Assert.Equal("1.0.0", d.Version.ToString());
        ComponentPort input = Assert.Single(d.Inputs);
        Assert.True(input.Optional);
        Assert.Equal("localNotes", input.Fallback);
        Assert.Equal(["localNotes", "copies", "spacing"], d.Properties.Select(p => p.Name));
        Assert.Equal("jam", d.Property("localNotes")!.Editor);
        ComponentProperty copies = d.Property("copies")!;
        Assert.Equal("3", copies.Default);
        Assert.Equal(["MinValue", "MaxValue", "Step"], copies.Attributes.Select(a => a.Name));
        Assert.Equal("Repeats the notes.", d.Doc?.Summary);
        Assert.Equal("notes", d.Output!.Name);
        Assert.Contains("emit n |> shift(i * spacing)", string.Join('\n', d.ProcessBody), StringComparison.Ordinal);
        Assert.Equal("__c_local_koda_notes_tripleNotes_1_0_0", d.HelperName);
    }

    [Fact]
    public void A_good_component_and_its_instance_have_no_diagnostics_of_the_component_system()
    {
        AmpleDocument doc = Parser.Parse(Song(), Corpus.Spec);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code.StartsWith("AMP22", StringComparison.Ordinal));
        BlockNode instance = doc.Blocks.First(b => b.Name == "introTriple");
        Assert.Equal("koda.notes.tripleNotes", instance.Type);
        Assert.True((doc.Spec ?? Corpus.Spec).Templates["koda.notes.tripleNotes"].Component);
    }

    [Fact]
    public void The_checks_of_the_contract_name_what_is_wrong()
    {
        string Codes(string component) => string.Join(' ', Parser.Parse(Header + component, Corpus.Spec).Diagnostics.Where(d => d.Code.StartsWith("AMP22", StringComparison.Ordinal)).Select(d => d.Code).Distinct());
        Assert.Contains("AMP2206", Codes(Triple.Replace("property spacing: Duration = 1bar", "#[MinValue(0)]\n  property spacing: Bool = true", StringComparison.Ordinal)));   // MinValue does not apply to Bool
        Assert.Contains("AMP2205", Codes(Triple.Replace("#[MinValue(1)", "#[MinValu(1)", StringComparison.Ordinal)));
        Assert.Contains("AMP2202", Codes(Triple.Replace("Int = 3", "Whatever = 3", StringComparison.Ordinal)));
        Assert.Contains("AMP2201", Codes(Triple.Replace("property spacing:", "property copies:", StringComparison.Ordinal)));
        Assert.Contains("AMP2208", Codes(Triple.Replace("  output notes: Notes\n", string.Empty, StringComparison.Ordinal)));
        Assert.Contains("AMP2209", Codes(Triple.Replace("koda.notes.tripleNotes", "drums", StringComparison.Ordinal)));   // a native name is never shadowed
        Assert.Contains("AMP2207", Codes(Triple.Replace("MaxValue(16)", "MaxValue(0)", StringComparison.Ordinal)));   // the lowest is above the highest
        Assert.Contains("AMP2204", Codes(Triple.Replace("Int = 3", "Int = 99", StringComparison.Ordinal)));   // the default is outside its range
        Assert.Contains("AMP2226", Codes(Triple.Replace("fallback localNotes", "fallback nothing", StringComparison.Ordinal)));
    }

    [Fact]
    public void An_instance_is_checked_against_the_contract_of_its_component()
    {
        string Codes(string instance) => string.Join(' ', Parser.Parse(Song(instances: instance), Corpus.Spec).Diagnostics.Where(d => d.Code == "AMP2218").Select(d => d.Code));
        Assert.Equal("AMP2218", Codes(Instance.Replace("copies 3", "copies 40", StringComparison.Ordinal)));   // above MaxValue
        Assert.Equal("AMP2218", Codes(Instance.Replace("spacing 1bar", "spacing sunny", StringComparison.Ordinal)));   // not a Duration
        Assert.Equal(string.Empty, Codes(Instance));
    }

    [Fact]
    public void An_instance_makes_the_notes_of_its_process_and_three_instances_share_one_definition()
    {
        string two = Instance + "\nkoda.notes.tripleNotes second at 5 for 3 {\n  copies 2\n  from intro.notes -> source\n}\n";
        ExpandedSong expanded = ModuleExpander.Expand(Song(instances: two), Corpus.Spec, _ => null);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(expanded.Text, @"notes __c_local_koda_notes_tripleNotes_1_0_0\("));   // both instances call the one helper (the parser adds it once)
        Assert.Contains("midi-clip introTriple at 2 for 3 {", expanded.Text, StringComparison.Ordinal);
        Assert.Contains("notes __c_local_koda_notes_tripleNotes_1_0_0(intro.notes, true, emptyNotes(), 3, 1bar)", expanded.Text, StringComparison.Ordinal);
        Assert.Equal(6, Notes(expanded.Text, "introTriple"));   // two notes, three copies
        Assert.Equal(4, Notes(expanded.Text, "second"));   // two notes, two copies
        ComponentUsage usage = Assert.Single(expanded.Components!);
        Assert.Equal("koda.notes.tripleNotes", usage.Name);
        Assert.Equal(SealState.Unsealed, usage.Seal);
    }

    [Fact]
    public void An_input_that_is_not_connected_uses_its_fallback_notes_and_an_instance_with_no_fallback_gets_empty_notes()
    {
        string local = "koda.notes.tripleNotes alone at 2 for 3 {\n  copies 3\n  localNotes 1:0:1:64:100\n}\n";
        ExpandedSong expanded = ModuleExpander.Expand(Song(instances: local), Corpus.Spec, _ => null);
        Assert.Contains("(1:0:1:64:100, false, 1:0:1:64:100, 3, 1bar)", expanded.Text.Replace("emptyNotes()", "emptyNotes()", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(3, Notes(expanded.Text, "alone"));
        string none = "koda.notes.tripleNotes nothing at 2 for 3 {\n}\n";
        ExpandedSong empty = ModuleExpander.Expand(Song(instances: none), Corpus.Spec, _ => null);
        Assert.Equal(0, Notes(empty.Text, "nothing"));
    }

    [Fact]
    public void A_consumer_that_reads_the_output_of_an_instance_gets_its_notes()
    {
        string consumer = Instance + "\nmidi-clip after at 6 for auto {\n  notes introTriple.notes |> transpose(+12)\n}\n";
        ExpandedSong expanded = ModuleExpander.Expand(Song(instances: consumer), Corpus.Spec, _ => null);
        Assert.Equal(6, Notes(expanded.Text, "after"));
    }

    [Fact]
    public void The_semantic_hash_ignores_the_documentation_and_the_presentation_and_follows_the_process_and_the_contract()
    {
        static string Semantic(ComponentDefinition d) => ComponentHashing.Semantic(d, []);
        ComponentDefinition basis = Only(Song());
        ComponentDefinition described = Only(Song(Triple.Replace("/** Repeats the notes. */", "/** Something else entirely. */", StringComparison.Ordinal).Replace("component koda", "#[DisplayName(\"Triple\"), KodaFact(\"Works best with motifs.\")]\ncomponent koda", StringComparison.Ordinal)));
        Assert.Equal(Semantic(basis), Semantic(described));   // R-B: a better description rebuilds nothing
        Assert.NotEqual(ComponentHashing.Payload(basis, []), ComponentHashing.Payload(described, []));   // the seal still sees it
        Assert.NotEqual(Semantic(basis), Semantic(Only(Song(Triple.Replace("shift(i * spacing)", "shift(i * spacing * 2)", StringComparison.Ordinal)))));
        Assert.NotEqual(Semantic(basis), Semantic(Only(Song(Triple.Replace("Int = 3", "Int = 4", StringComparison.Ordinal)))));
        Assert.Equal(Semantic(basis), Semantic(Only(Song(Triple.Replace("    let n =", "    # a comment\n    let n =", StringComparison.Ordinal)))));   // comments are not code
    }

    [Fact]
    public void A_seal_is_valid_until_the_component_changes_and_a_changed_one_counts_as_unsealed()
    {
        ComponentDefinition basis = Only(Song());
        SealCheck now = ComponentHashing.Check(basis, []);
        Assert.Equal(SealState.Unsealed, now.State);
        string sealed1 = Triple.TrimEnd().TrimEnd('}').TrimEnd() + "\n" + string.Join('\n', ComponentHashing.SealLines(now.Payload, now.Semantic)) + "\n}\n\n";
        ComponentDefinition withSeal = Only(Song(sealed1));
        Assert.Equal(SealState.Valid, ComponentHashing.Check(withSeal, []).State);
        ComponentDefinition changed = Only(Song(sealed1.Replace("shift(i * spacing)", "shift(i * spacing * 2)", StringComparison.Ordinal)));
        SealCheck check = ComponentHashing.Check(changed, []);
        Assert.Equal(SealState.Invalid, check.State);
        Assert.Contains("sealed version is immutable", check.Reason, StringComparison.Ordinal);
        SealCheck docOnly = ComponentHashing.Check(Only(Song(sealed1.Replace("Repeats the notes.", "Repeats them.", StringComparison.Ordinal))), []);
        Assert.Equal(SealState.Invalid, docOnly.State);
        Assert.Equal(withSeal.Seal!.Semantic, docOnly.Semantic);   // the semantic hash did not move
    }
}
