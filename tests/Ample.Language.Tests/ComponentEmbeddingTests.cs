namespace Ample.Language.Tests;

/// <summary>Embedded modules (docs/language/19-components.md, "Embedding and extraction"): reading them, embedding, using the template source, extracting and detaching.</summary>
public sealed class ComponentEmbeddingTests
{
    private const string Header = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n";

    private const string Twice = "component demo.fx.twice version 1.0.0 {\n  id \"com.demo.twice\"\n  input source: Notes\n  property copies: Int = 2\n  output notes: Notes\n  process {\n    for i in 0..copies {\n      emit source |> shift(i * 1bar)\n    }\n  }\n}\n";

    private const string Intro = "midi-clip intro at 1 for 1 {\n  notes 1:0:1:60:100\n}\n\n";

    private const string Instance = "demo.fx.twice doubled at 2 for 3 {\n  from intro.notes -> source\n}\n";

    private static string Sealed()
    {
        ComponentEditResult sealedResult = ComponentEdits.Seal(Twice, "demo.fx.twice", null, Corpus.Spec);
        Assert.True(sealedResult.Ok, sealedResult.Message);
        return sealedResult.Text;
    }

    private static ComponentDefinition Template(string text) => Assert.Single(Parser.Parse(text, Corpus.Spec, new ParseOptions { RequireProjectLine = false }).Components);

    private static string SongWithEmbedded(string components) =>
        Header + "embedded module fx {\n  id \"com.demo.fx\"\n  version \"1.0.0\"\n\n" + string.Join('\n', components.TrimEnd().Split('\n').Select(l => "  " + l)) + "\n}\n\n" + Intro + Instance;

    [Fact]
    public void An_embedded_module_is_read_with_its_components_and_leaves_no_error_behind()
    {
        AmpleDocument doc = Parser.Parse(SongWithEmbedded(Sealed()), Corpus.Spec);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        EmbeddedModule module = Assert.Single(doc.EmbeddedModules);
        Assert.Equal("fx", module.Name);
        Assert.Equal("com.demo.fx", module.Id);
        Assert.Equal("1.0.0", module.Version);
        Assert.Equal(["demo.fx.twice"], module.Components);
        Assert.Equal("com.demo.twice", Assert.Single(doc.Components).Id);
    }

    [Fact]
    public void An_embedded_component_builds_like_the_same_component_written_in_the_song()
    {
        AmpleDocument plain = Parser.Parse(Header + Twice + "\n" + Intro + Instance, Corpus.Spec);
        AmpleDocument embedded = Parser.Parse(SongWithEmbedded(Twice), Corpus.Spec);
        Assert.Equal(plain.Diagnostics.Count(d => d.Severity == Severity.Error), embedded.Diagnostics.Count(d => d.Severity == Severity.Error));
        Assert.Equal(ComponentHashing.Semantic(plain.Components[0], []), ComponentHashing.Semantic(embedded.Components[0], []));
    }

    [Fact]
    public void Embedding_writes_a_module_once_and_a_second_embed_says_it_is_there_already()
    {
        ComponentDefinition template = Template(Sealed());
        string song = Header + Intro + Instance;
        ComponentEditResult first = ComponentEmbedding.Embed(song, ComponentEmbedding.Closure([template], [template]), "fx", "com.demo.fx", "1.0.0", Corpus.Spec);
        Assert.True(first.Ok, first.Message);
        AmpleDocument after = Parser.Parse(first.Text, Corpus.Spec);
        Assert.DoesNotContain(after.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Equal(template.Seal!.Payload, Assert.Single(after.Components).Seal!.Payload);
        ComponentEditResult second = ComponentEmbedding.Embed(first.Text, [template], "fx", null, null, Corpus.Spec);
        Assert.False(second.Ok);
        Assert.Contains("embedded already", second.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Embedding_is_refused_when_the_song_has_the_same_version_with_other_code()
    {
        ComponentDefinition template = Template(Sealed());
        string other = SongWithEmbedded(Twice.Replace("i * 1bar", "i * 2bars", StringComparison.Ordinal));
        ComponentEditResult result = ComponentEmbedding.Embed(other, [template], "fx2", null, null, Corpus.Spec);
        Assert.False(result.Ok);
        Assert.Contains("other code", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_template_source_is_used_again_only_for_exactly_the_same_sealed_code()
    {
        string sealedText = Sealed();
        ComponentDefinition template = Template(sealedText);
        ComponentEditResult same = ComponentEmbedding.UseTemplate(SongWithEmbedded(sealedText), template, Corpus.Spec);
        Assert.True(same.Ok, same.Message);
        AmpleDocument after = Parser.Parse(same.Text, Corpus.Spec);
        Assert.Empty(after.Components);
        Assert.Empty(after.EmbeddedModules);   // the module was empty, so it is gone
        ComponentEditResult different = ComponentEmbedding.UseTemplate(SongWithEmbedded(Twice.Replace("i * 1bar", "i * 2bars", StringComparison.Ordinal)), template, Corpus.Spec);
        Assert.False(different.Ok);
        Assert.Contains("not the same code", different.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Extracting_keeps_identity_and_seal_and_makes_the_song_import_the_file()
    {
        string sealedText = Sealed();
        ComponentExtraction result = ComponentEmbedding.Extract(SongWithEmbedded(sealedText), "demo.fx.twice", null, "components/fx.amplec", Corpus.Spec);
        Assert.True(result.Ok, result.Message);
        ComponentDefinition inFile = Template(result.FileText);
        Assert.Equal(Template(sealedText).Seal!.Payload, inFile.Seal!.Payload);
        AmpleDocument song = Parser.Parse(result.SongText, Corpus.Spec);
        Assert.Empty(song.Components);
        Assert.Equal("components/fx.amplec", Assert.Single(song.ComponentImports).Target);
    }

    [Fact]
    public void Detaching_a_sealed_embedded_component_removes_its_seal_and_it_still_parses()
    {
        ComponentEditResult result = ComponentEmbedding.Detach(SongWithEmbedded(Sealed()), "demo.fx.twice", null, Corpus.Spec);
        Assert.True(result.Ok, result.Message);
        AmpleDocument doc = Parser.Parse(result.Text, Corpus.Spec);
        Assert.Null(Assert.Single(doc.Components).Seal);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
    }

    [Fact]
    public void The_payload_of_a_module_needs_every_component_sealed()
    {
        Assert.Equal(string.Empty, ComponentEmbedding.ModulePayload([Template(Twice)]));
        Assert.StartsWith("sha256:", ComponentEmbedding.ModulePayload([Template(Sealed())]), StringComparison.Ordinal);
    }

    [Fact]
    public void Adding_an_import_writes_only_the_import_and_with_a_block_an_instance_with_a_free_name()
    {
        ComponentImportAdd only = ComponentEmbedding.AddImport(Header + Intro, "components/twice.amplec", null, false, Corpus.Spec);
        Assert.True(only.Ok);
        Assert.Equal(["import \"components/twice.amplec\""], only.Added);
        Assert.Contains("import \"components/twice.amplec\"", only.SongText, StringComparison.Ordinal);
        Assert.Null(only.Block);

        ComponentImportAdd withBlock = ComponentEmbedding.AddImport(Header + Intro, "components/twice.amplec", "demo.fx.twice", true, Corpus.Spec);
        Assert.Equal("twice", withBlock.Block);
        Assert.Contains("demo.fx.twice twice {", withBlock.SongText, StringComparison.Ordinal);

        // again: the import is there, the name is taken, so the next one is twice2
        ComponentImportAdd again = ComponentEmbedding.AddImport(withBlock.SongText, "components/twice.amplec", "demo.fx.twice", true, Corpus.Spec);
        Assert.Equal("twice2", again.Block);
        Assert.Single(again.Added, l => l.StartsWith("demo.fx.twice twice2", StringComparison.Ordinal));
        Assert.DoesNotContain(again.Added, l => l.StartsWith("import", StringComparison.Ordinal));

        // nothing to do: the song imports the file and no block is asked for
        Assert.Empty(ComponentEmbedding.AddImport(withBlock.SongText, "components/twice.amplec", null, false, Corpus.Spec).Added);
    }

    [Fact]
    public void Tags_are_a_component_attribute_that_is_not_part_of_the_semantic_hash()
    {
        string tagged = Twice.Replace("component demo.fx.twice", "#[Tags(\"echo\", \"space\")]\ncomponent demo.fx.twice", StringComparison.Ordinal);
        Assert.DoesNotContain(Parser.Parse(Header + tagged, Corpus.Spec).Diagnostics, d => d.Code is "AMP2205" or "AMP2206" or "AMP2207");
        Assert.Equal(ComponentHashing.Semantic(Template(Twice), []), ComponentHashing.Semantic(Template(tagged), []));
        // on a property it is refused: tags belong to a component
        string wrong = Twice.Replace("  property copies", "  #[Tags(\"x\")]\n  property copies", StringComparison.Ordinal);
        Assert.Contains(Parser.Parse(Header + wrong, Corpus.Spec).Diagnostics, d => d.Code == "AMP2206");
    }

    [Fact]
    public void A_doc_comment_above_the_attributes_is_the_description_of_the_component()
    {
        string text = "/** Repeats the notes. */\n#[DisplayName(\"Twice\")]\n#[Tags(\"repeat\")]\n" + Twice;
        ComponentDefinition d = Template(text);
        Assert.Equal("Repeats the notes.", d.Doc?.Summary);
    }

    [Fact]
    public void An_alias_import_writes_as_alias_and_the_instance_name_starts_with_the_alias()
    {
        ComponentImportAdd result = ComponentEmbedding.AddImport(Header + Intro, "components/twice.amplec", "demo.fx.twice", true, Corpus.Spec, "twiceCopy");
        Assert.Contains("import \"components/twice.amplec\" as twiceCopy", result.SongText, StringComparison.Ordinal);
        Assert.Contains("twiceCopy.fx.twice twice {", result.SongText, StringComparison.Ordinal);
    }
}
