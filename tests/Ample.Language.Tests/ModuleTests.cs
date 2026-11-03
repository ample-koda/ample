namespace Ample.Language.Tests;

[Trait("Category", "Modules")]
public sealed class ModuleTests
{
    private const string Groove = "# A drum and bass groove.\nmodule groove version 1.2\nparam kit kit/tape\ninput chords\noutput audio mix.mix\n\nbass bass {\n  from in.chords -> notes\n}\ndrums drums {\n  kit $kit\n}\nmixer mix {\n  from drums.audio -> audio1\n  from bass.audio -> audio2\n}\n";

    private const string Song = "composition \"demo\" { ample 1  tempo 92  master-seed 424242 }\nmidi-clip chords {\n  chords Am F C G\n}\nuse groove@1.0 as intro {\n  kit kit/808\n  from chords.notes -> chords\n}\nmaster master {\n  from intro.audio -> audio\n}\n";

    private static ModuleSource? Find(string name) => name switch
    {
        "groove" => new ModuleSource("groove", Groove, "test"),
        "twice" => new ModuleSource("twice", "module twice version 1.0\ninput chords\noutput audio a.audio\nuse groove as a {\n  from in.chords -> chords\n}\nuse groove as b {\n  from in.chords -> chords\n}\n", "test"),
        "loop" => new ModuleSource("loop", "module loop version 1.0\nuse loop as again {\n}\n", "test"),
        "broken" => new ModuleSource("broken", "module broken\nparam\n", "test"),
        _ => null,
    };

    private static ExpandedSong Expand(string song) => ModuleExpander.Expand(song, Corpus.Spec, Find);

    [Fact]
    public void A_use_is_replaced_by_the_blocks_of_its_module_named_by_instance_and_references_follow_the_output()
    {
        ExpandedSong e = Expand(Song);
        Assert.Empty(e.Diagnostics);
        Assert.Contains("bass \"intro/bass\" {", e.Text, StringComparison.Ordinal);
        Assert.Contains("  kit kit/808", e.Text, StringComparison.Ordinal);
        Assert.Contains("  from chords.notes -> notes", e.Text, StringComparison.Ordinal);
        Assert.Contains("  from \"intro/mix\".audio -> audio", e.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("use groove", e.Text, StringComparison.Ordinal);
        AmpleDocument doc = Parser.Parse(e.Text, Corpus.Spec);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Equal(["chords", "intro/bass", "intro/drums", "intro/mix", "master"], doc.Blocks.Select(b => b.Name).Order(StringComparer.Ordinal));
        Assert.Equal("groove", e.Used.Single().Name);
        Assert.Equal("1.2", e.Used.Single().Version);
        Assert.StartsWith("sha256:", e.Used.Single().Hash, StringComparison.Ordinal);
        Assert.Equal(e.Text.Split('\n').Length, e.LineOrigin.Count);
    }

    [Fact]
    public void A_module_body_does_not_require_a_project_line()
    {
        AmpleDocument doc = Parser.Parse(ModuleDefinition.Parse(Groove).Body, Corpus.Spec, new ParseOptions { RequireProjectLine = false });
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == "AMP1001");
        Assert.Contains(doc.Blocks, b => b.Name == "bass");
    }

    [Fact]
    public void The_default_of_a_parameter_applies_and_the_expansion_is_deterministic()
    {
        string withDefault = Song.Replace("  kit kit/808\n", string.Empty, StringComparison.Ordinal);
        Assert.Contains("  kit kit/tape", Expand(withDefault).Text, StringComparison.Ordinal);
        Assert.Equal(Expand(Song).Text, Expand(Song).Text);
    }

    [Fact]
    public void The_same_module_used_twice_gives_blocks_with_different_names_and_so_different_seeds()
    {
        string two = Song.Replace("master master", "use groove as outro {\n  from chords.notes -> chords\n}\nmaster master", StringComparison.Ordinal);
        AmpleDocument doc = Parser.Parse(Expand(two).Text, Corpus.Spec);
        Assert.Contains(doc.Blocks, b => b.Name == "intro/drums");
        Assert.Contains(doc.Blocks, b => b.Name == "outro/drums");
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
    }

    [Fact]
    public void Modules_can_use_modules_and_names_carry_the_whole_path()
    {
        string song = "composition \"demo\" { ample 1  tempo 92  master-seed 1 }\nmidi-clip chords {\n}\nuse twice as verse {\n  from chords.notes -> chords\n}\nmaster master {\n  from verse.audio -> audio\n}\n";
        ExpandedSong e = Expand(song);
        Assert.Empty(e.Diagnostics);
        AmpleDocument doc = Parser.Parse(e.Text, Corpus.Spec);
        Assert.Contains(doc.Blocks, b => b.Name == "verse/a/drums");
        Assert.Contains(doc.Blocks, b => b.Name == "verse/b/mix");
        Assert.Contains("  from \"verse/a/mix\".audio -> audio", e.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
    }

    [Theory]
    [InlineData("use nothing as x {\n}\n", "AMP5320")]
    [InlineData("use groove@2.0 as x {\n  from chords.notes -> chords\n}\n", "AMP5321")]
    [InlineData("use groove as x {\n  volume 3\n  from chords.notes -> chords\n}\n", "AMP5322")]
    [InlineData("use groove as x {\n}\n", "AMP5323")]
    [InlineData("use groove as x {\n  from chords.notes -> nothing\n}\n", "AMP5323")]
    [InlineData("use broken as x {\n}\n", "AMP5326")]
    [InlineData("use loop as x {\n}\n", "AMP5327")]
    public void Problems_with_a_module_or_its_use_are_reported_at_the_use_with_their_code(string use, string code)
    {
        string song = "composition \"demo\" { ample 1  tempo 92  master-seed 1 }\nmidi-clip chords {\n}\n" + use;
        ExpandedSong e = Expand(song);
        Assert.Contains(e.Diagnostics, d => d.Code == code && d.Line == 3);
    }

    [Fact]
    public void A_missing_module_output_and_an_unknown_placeholder_are_reported()
    {
        ExpandedSong e = Expand(Song.Replace("intro.audio", "intro.video", StringComparison.Ordinal));
        Assert.Contains(e.Diagnostics, d => d.Code == "AMP5325");
        ModuleSource Bad(string n) => new(n, "module bad version 1.0\noutput audio d.audio\ndrums d {\n  kit $nothing\n}\n", "test");
        ExpandedSong p = ModuleExpander.Expand("composition \"d\" { ample 1  tempo 92  master-seed 1 }\nuse bad as x {\n}\n", Corpus.Spec, Bad);
        Assert.Contains(p.Diagnostics, d => d.Code == "AMP5324");
    }

    [Fact]
    public void Version_pins_take_the_same_major_and_not_older()
    {
        Assert.True(ModuleDefinition.Fits(string.Empty, "3.1"));
        Assert.True(ModuleDefinition.Fits("1.0", "1.0"));
        Assert.True(ModuleDefinition.Fits("1.0", "1.2"));
        Assert.True(ModuleDefinition.Fits("1.2", "1.2.1"));
        Assert.False(ModuleDefinition.Fits("1.3", "1.2"));
        Assert.False(ModuleDefinition.Fits("1.0", "2.0"));
    }

    [Fact]
    public void The_editor_accepts_use_statements_without_errors_and_a_song_without_uses_is_unchanged()
    {
        AmpleDocument doc = Parser.Parse(Song, Corpus.Spec);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        UseNode use = Assert.Single(doc.Uses);
        Assert.Equal("groove", use.Module);
        Assert.Equal("1.0", use.Version);
        Assert.Equal("intro", use.Instance);
        Assert.Equal("kit", use.Parameters.Single().Name);
        Assert.Equal("chords", use.Froms.Single().Input);

        string plain = "composition \"demo\" { ample 1  tempo 92  master-seed 1 }\nmidi-clip chords {\n}\n";
        Assert.Same(plain, Expand(plain).Text);
        Assert.Equal(Song, Formatter.Format(Song, Corpus.Spec));
    }

    [Fact]
    public void A_bad_use_line_and_a_name_clash_are_static_errors()
    {
        AmpleDocument bad = Parser.Parse("composition \"demo\" { ample 1  tempo 92  master-seed 1 }\nuse groove {\n}\n", Corpus.Spec);
        Assert.Contains(bad.Diagnostics, d => d.Code == "AMP1110");
        AmpleDocument clash = Parser.Parse("composition \"demo\" { ample 1  tempo 92  master-seed 1 }\nmidi-clip chords {\n}\nuse groove as chords {\n}\n", Corpus.Spec);
        Assert.Contains(clash.Diagnostics, d => d.Code == "AMP2002");
    }
}
