namespace Ample.Language.Tests;

/// <summary>The components that ship with AMPLE (content/components): every song sees them by name, without an import line.</summary>
public sealed class BuiltInComponentsTests
{
    private const string Header = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n";

    [Fact]
    public void The_voice_components_are_embedded_and_every_file_parses_without_a_problem()
    {
        Assert.Contains("voice-clean.amplec", BuiltInComponents.Files);
        Assert.Contains("quick-trim.amplec", BuiltInComponents.Files);
        foreach (string file in BuiltInComponents.Files)
        {
            string text = BuiltInComponents.Read(BuiltInComponents.Prefix + file)!;
            ComponentFile parsed = ComponentReader.Read(text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'), Comments.DocsOf(text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')), file);
            Assert.DoesNotContain(parsed.Issues, i => i.Severity == Severity.Error);
            Assert.All(parsed.Definitions, d => Assert.StartsWith(BuiltInComponents.IdPrefix, d.Id, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_compiled_components_are_the_files_of_content_components()
    {
        string dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "content", "components"))) dir = Path.GetDirectoryName(dir) ?? throw new DirectoryNotFoundException("content/components");
        string[] files = [.. Directory.GetFiles(Path.Combine(dir, "content", "components"), "*.amplec").Select(Path.GetFileName).Order(StringComparer.Ordinal)!];
        Assert.Equal(files, BuiltInComponents.Files.Order(StringComparer.Ordinal));   // else: node tools/gen-builtin-components.mjs
        foreach (string file in files)
            Assert.Equal(File.ReadAllText(Path.Combine(dir, "content", "components", file)).Replace("\r\n", "\n", StringComparison.Ordinal), BuiltInComponents.Read(file));
    }

    [Fact]
    public void A_song_that_names_a_built_in_component_gets_it_without_an_import_line()
    {
        string song = Header + "import-audio rec {\n  file rec.wav\n}\nample.voice.clean clean {\n  from rec.audio -> audio\n}\n";
        ComponentRegistryResult registry = ComponentRegistry.Resolve(song, "song", _ => null, Corpus.Spec);
        Assert.Contains(registry.Visible, c => c.Name == "ample.voice.clean");
        Assert.DoesNotContain(registry.Issues, i => i.Severity == Severity.Error);
    }

    [Fact]
    public void A_song_that_does_not_name_one_sees_none()
    {
        ComponentRegistryResult registry = ComponentRegistry.Resolve(Header + "midi-clip melody at 1 for 1 {\n  notes 1:0:1:60:100\n}\n", "song", _ => null, Corpus.Spec);
        Assert.Empty(registry.Visible);
    }
}
