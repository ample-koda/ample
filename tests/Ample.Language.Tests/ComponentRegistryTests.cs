namespace Ample.Language.Tests;

/// <summary>What a song can see of components: files, namespaces, aliases, reexports, versions and the conflicts of putting them together.</summary>
public sealed class ComponentRegistryTests
{
    private const string Header = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n";

    private static string Comp(string path, string version, string id = "", bool current = false, string process = "for i in 0..copies {\n      emit source |> shift(i * 1bar)\n    }") =>
        $"component {path} version {version}{(current ? " current" : string.Empty)} {{\n{(id.Length > 0 ? $"  id \"{id}\"\n" : string.Empty)}  input source: Notes\n  property copies: Int = 2\n  output notes: Notes\n  process {{\n    {process}\n  }}\n}}\n\n";

    private static ComponentRegistryResult Resolve(string song, Dictionary<string, string> files) => ComponentRegistry.Resolve(Header + song, "song", t => files.GetValueOrDefault(t), Corpus.Spec);

    private static string[] Codes(ComponentRegistryResult r) => [.. r.Issues.Select(i => i.Code)];

    [Fact]
    public void An_import_makes_the_components_of_a_file_visible_under_the_paths_the_file_exports()
    {
        ComponentRegistryResult r = Resolve("import \"fx.amplec\"\n", new() { ["fx.amplec"] = Comp("koda.fx.canonMaker", "1.0.0") + Comp("koda.fx.tripleNotes", "2.1.0") });
        Assert.Empty(r.Issues);
        Assert.Equal(["koda.fx.canonMaker", "koda.fx.tripleNotes"], r.Visible.Select(v => v.Name).Order(StringComparer.Ordinal));
        ComponentSource source = Assert.Single(r.Sources);
        Assert.Equal("fx.amplec", source.Origin);
        Assert.Equal(2, source.Definitions.Count);
    }

    [Fact]
    public void An_alias_replaces_the_root_of_the_paths_and_the_canonical_id_stays()
    {
        ComponentRegistryResult r = Resolve("import \"fx.amplec\" as custom\n", new() { ["fx.amplec"] = Comp("koda.fx.canonMaker", "1.0.0", "demo.koda.fx.canon-maker") });
        ComponentDefinition d = Assert.Single(r.Visible);
        Assert.Equal("custom.fx.canonMaker", d.Name);
        Assert.Equal("koda.fx.canonMaker", d.DeclaredName);
        Assert.Equal("demo.koda.fx.canon-maker", d.Id);   // I1: the exported name is not the identity
        Assert.Equal(d.HelperName, Assert.Single(ComponentRegistryHelper.Visible("import \"fx.amplec\"\n", new() { ["fx.amplec"] = Comp("koda.fx.canonMaker", "1.0.0", "demo.koda.fx.canon-maker") })).HelperName);   // one helper whatever the path
    }

    [Fact]
    public void Several_files_can_add_to_one_namespace_and_the_last_import_never_wins()
    {
        Dictionary<string, string> files = new() { ["a.amplec"] = Comp("koda.fx.one", "1.0.0"), ["b.amplec"] = Comp("koda.fx.two", "1.0.0"), ["c.amplec"] = Comp("koda.fx.one", "1.0.0", "other.component") };
        ComponentRegistryResult merged = Resolve("import \"a.amplec\"\nimport \"b.amplec\"\n", files);
        Assert.Empty(merged.Issues);
        Assert.Equal(["koda.fx.one", "koda.fx.two"], merged.Visible.Select(v => v.Name).Order(StringComparer.Ordinal));
        ComponentRegistryResult clash = Resolve("import \"a.amplec\"\nimport \"c.amplec\"\n", files);
        Assert.Contains("AMP2213", Codes(clash));   // the same path, two different components: nothing is chosen silently
        Assert.DoesNotContain(clash.Visible, v => v.Name == "koda.fx.one");
    }

    [Fact]
    public void The_same_component_from_two_places_is_one_and_the_same_version_with_another_content_is_a_conflict()
    {
        Dictionary<string, string> files = new() { ["a.amplec"] = Comp("koda.fx.one", "1.0.0"), ["copy.amplec"] = Comp("koda.fx.one", "1.0.0"), ["changed.amplec"] = Comp("koda.fx.one", "1.0.0", process: "emit source") };
        ComponentRegistryResult same = Resolve("import \"a.amplec\"\nimport \"copy.amplec\"\n", files);
        Assert.Empty(same.Issues);
        Assert.Single(same.Visible);
        Assert.Contains("AMP2212", Codes(Resolve("import \"a.amplec\"\nimport \"changed.amplec\"\n", files)));
    }

    [Fact]
    public void Versions_of_one_component_coexist_and_two_current_versions_are_refused()
    {
        Dictionary<string, string> files = new() { ["v.amplec"] = Comp("koda.fx.one", "1.8.0") + Comp("koda.fx.one", "3.2.0") + Comp("koda.fx.one", "4.0.0-beta.1"), ["two.amplec"] = Comp("koda.fx.one", "1.0.0", current: true) + Comp("koda.fx.one", "2.0.0", current: true) };
        ComponentRegistryResult r = Resolve("import \"v.amplec\"\n", files);
        Assert.Empty(r.Issues);
        ComponentFamily family = Assert.Single(r.Families);
        Assert.Equal(["4.0.0-beta.1", "3.2.0", "1.8.0"], family.Versions.Select(v => v.Version.ToString()));
        Assert.Equal("3.2.0", family.Default.Version.ToString());   // not the pre-release
        Assert.Contains("AMP2214", Codes(Resolve("import \"two.amplec\"\n", files)));
    }

    [Fact]
    public void A_reexport_offers_a_symbol_or_a_namespace_again_and_keeps_the_canonical_id()
    {
        Dictionary<string, string> files = new()
        {
            ["common.amplec"] = Comp("acme.effects.echo", "1.0.0", "com.acme.echo") + Comp("acme.effects.delay", "1.0.0", "com.acme.delay") + Comp("acme.clamp", "1.0.0", "com.acme.clamp"),
            ["facade.amplec"] = "import \"common.amplec\" as common\n\nreexport common.clamp as koda.clamp\nreexport common.effects.* as koda.fx.*\n",
        };
        ComponentRegistryResult r = Resolve("import \"facade.amplec\"\n", files);
        Assert.Empty(r.Issues);
        Assert.Equal(["koda.clamp", "koda.fx.delay", "koda.fx.echo"], r.Visible.Select(v => v.Name).Order(StringComparer.Ordinal));
        Assert.Equal("com.acme.echo", r.Visible.First(v => v.Name == "koda.fx.echo").Id);
        Assert.Contains("AMP2217", Codes(Resolve("import \"bad.amplec\"\n", new() { ["bad.amplec"] = "reexport nothing.here as koda.x\n" })));
    }

    [Fact]
    public void A_missing_file_and_a_circle_of_imports_are_reported()
    {
        Assert.Contains("AMP2216", Codes(Resolve("import \"nowhere.amplec\"\n", [])));
        Dictionary<string, string> circle = new() { ["a.amplec"] = "import \"b.amplec\"\n" + Comp("koda.fx.one", "1.0.0"), ["b.amplec"] = "import \"a.amplec\"\n" };
        Assert.Contains("AMP2217", Codes(Resolve("import \"a.amplec\"\n", circle)));
    }

    [Fact]
    public void An_instance_of_an_imported_component_is_expanded_with_the_file_it_comes_from()
    {
        Dictionary<string, string> files = new() { ["fx.amplec"] = Comp("koda.fx.twice", "1.0.0") };
        ComponentRegistryResult r = Resolve("import \"fx.amplec\"\n", files);
        string song = Header + "import \"fx.amplec\"\n\nmidi-clip intro at 1 for 1 {\n  notes 1:0:1:60:100\n}\n\nkoda.fx.twice a at 2 for 3 {\n  copies 2\n  from intro.notes -> source\n}\n";
        ExpandedSong expanded = ModuleExpander.Expand(song, Corpus.Spec, _ => null, r.Sources);
        Assert.Contains("component koda.fx.twice version 1.0.0", expanded.Text, StringComparison.Ordinal);   // the file comes along
        Assert.Contains("midi-clip a at 2 for 3 {", expanded.Text, StringComparison.Ordinal);
        AmpleDocument doc = Parser.Parse(expanded.Text, Corpus.Spec, new ParseOptions { ApplyNoteFlow = true });
        Assert.Equal(2, doc.Blocks.First(b => b.Name == "a").Parameters.First(p => p.Name == "notes").Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        ComponentUsage usage = Assert.Single(expanded.Components!);
        Assert.StartsWith("sha256:", usage.Semantic, StringComparison.Ordinal);
    }

    [Fact]
    public void An_instance_that_asks_for_a_version_gets_that_version_and_one_that_does_not_exist_is_an_error()
    {
        Dictionary<string, string> files = new() { ["v.amplec"] = Comp("koda.fx.one", "1.8.0", process: "emit source") + Comp("koda.fx.one", "3.2.0") };
        ComponentRegistryResult r = Resolve("import \"v.amplec\"\n", files);
        string Song(string version) => Header + "import \"v.amplec\"\n\nmidi-clip intro at 1 for 1 {\n  notes 1:0:1:60:100\n}\n\nkoda.fx.one a stable" + version + " at 2 for 3 {\n  from intro.notes -> source\n}\n";
        Assert.Contains("one_1_8_0", ModuleExpander.Expand(Song("@1.8"), Corpus.Spec, _ => null, r.Sources).Text, StringComparison.Ordinal);
        Assert.Contains("one_3_2_0", ModuleExpander.Expand(Song(string.Empty), Corpus.Spec, _ => null, r.Sources).Text, StringComparison.Ordinal);   // the default is the highest release
        Assert.Contains(ModuleExpander.Expand(Song("@9"), Corpus.Spec, _ => null, r.Sources).Diagnostics, d => d.Code == "AMP2215");
    }
}

internal static class ComponentRegistryHelper
{
    public static IReadOnlyList<ComponentDefinition> Visible(string song, Dictionary<string, string> files) =>
        ComponentRegistry.Resolve("composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n" + song, "song", t => files.GetValueOrDefault(t), Corpus.Spec).Visible;
}
