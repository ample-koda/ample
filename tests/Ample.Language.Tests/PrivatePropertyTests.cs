namespace Ample.Language.Tests;

/// <summary>
/// WP-P1 of plan/diagnostics/PLAN.md (11g, 11h H.5): <c>private property</c> in a Notes component. Public by default, private by choice: a private property is a value of the definition that only its own process
/// reads; it is never a parameter of an instance, never in the catalog, and only the ones the music reads are evaluated, hashed as content and lowered.
/// </summary>
public sealed class PrivatePropertyTests
{
    private const string Header = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n";

    private const string Intro = "midi-clip intro at 1 for 1 {\n  notes 1:0:1:60:100 1:1:1:62:100\n}\n\n";

    private const string Shifter =
        "component koda.notes.shifter version 1.0.0 {\n  input source: Notes\n\n  property steps: Int = 2\n\n  private property halfSteps: Int = steps * 1\n  private property semitones: Int = halfSteps + 5\n" +
        "  private property modelVersion: String = \"v3\"\n\n  output notes: Notes\n\n  process {\n    emit source |> transpose(semitones)\n  }\n}\n\n";

    private static string Instance(string lines = "  steps 3\n") => "koda.notes.shifter up at 2 for 1 {\n" + lines + "  from intro.notes -> source\n}\n";

    private static string Song(string component = Shifter, string instance = "") => Header + component + Intro + (instance.Length == 0 ? Instance() : instance);

    private static ComponentDefinition Only(string component) => Assert.Single(Parser.Parse(Header + component, Corpus.Spec, new ParseOptions { RequireProjectLine = false }).Components);

    private static string[] Codes(string component) => [.. Parser.Parse(Header + component, Corpus.Spec).Diagnostics.Where(d => d.Code.StartsWith("AMP22", StringComparison.Ordinal) || d.Code == "AMP2060").Select(d => d.Code)];

    private static string NotesOf(string song, string block)
    {
        ExpandedSong expanded = ModuleExpander.Expand(song, Corpus.Spec, _ => null);
        AmpleDocument doc = Parser.Parse(expanded.Text, Corpus.Spec, new ParseOptions { ApplyNoteFlow = true });
        return doc.Blocks.First(b => b.Name == block).Parameters.First(p => p.Name == "notes").Value;
    }

    [Fact]
    public void A_private_property_is_parsed_and_is_not_a_public_property()
    {
        ComponentDefinition d = Only(Shifter);
        Assert.Equal(["halfSteps", "semitones", "modelVersion"], d.PrivateProperties.Select(p => p.Name));
        Assert.Equal(["steps"], d.Properties.Select(p => p.Name));
        Assert.Equal(["steps"], ComponentChecker.TemplateOf(d).Parameters.Keys);   // the Studio, the catalog and the checker of instances see only this
        Assert.Empty(Codes(Shifter));
    }

    [Fact]
    public void The_music_reads_the_private_properties_and_every_instance_derives_them_from_its_own_public_settings()
    {
        Assert.Contains(":68:", NotesOf(Song(), "up"), StringComparison.Ordinal);   // 3 + 5 = 8 semitones above 60
        string two = Instance() + "\nkoda.notes.shifter low at 4 for 1 {\n  steps 0\n  from intro.notes -> source\n}\n";
        Assert.Contains(":68:", NotesOf(Song(instance: two), "up"), StringComparison.Ordinal);
        Assert.Contains(":65:", NotesOf(Song(instance: two), "low"), StringComparison.Ordinal);   // 0 + 5
    }

    [Fact]
    public void Only_the_private_properties_that_the_music_reads_are_lowered_and_in_order_on_the_line_of_the_head()
    {
        string helper = ComponentHelper.Text(Only(Shifter));
        Assert.Contains("let Int halfSteps = steps * 1; let Int semitones = halfSteps + 5;", helper.Split('\n')[0], StringComparison.Ordinal);   // semitones reads halfSteps: both are used, on the first line of the helper
        Assert.DoesNotContain("modelVersion", helper, StringComparison.Ordinal);   // metadata is never lowered, so never run
    }

    [Fact]
    public void An_instance_cannot_set_a_private_property_and_the_error_does_not_offer_it()
    {
        AmpleDocument doc = Parser.Parse(Song(instance: Instance("  steps 3\n  semitones 1\n")), Corpus.Spec);
        Diagnostic error = Assert.Single(doc.Diagnostics, d => d.Code == "AMP2261");
        Assert.Contains("private property", error.Message, StringComparison.Ordinal);
        Assert.Contains("steps", error.Hint, StringComparison.Ordinal);   // it names the public ones
        Assert.DoesNotContain("halfSteps", error.Hint, StringComparison.Ordinal);   // and does not offer a private one
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == "AMP2003" && d.Message.Contains("semitones", StringComparison.Ordinal));   // one error for one cause
    }

    [Fact]
    public void The_expansion_keeps_the_problems_of_the_instance_lines_so_check_and_build_still_report_them()
    {
        // the expanded song replaces the lines of the instance; their errors would be lost (ample check said nothing, #745)
        ComponentExpansion expansion = ComponentExpander.Expand(Song(instance: Instance("  steps 3\n  semitones 1\n  bogus 4\n")), Corpus.Spec);
        Assert.Contains(expansion.Diagnostics, d => d.Code == "AMP2261" && d.Message.Contains("semitones", StringComparison.Ordinal) && d.Severity == Severity.Error);
        Assert.Contains(expansion.Diagnostics, d => d.Message.Contains("bogus", StringComparison.Ordinal) && d.Severity == Severity.Error);
        Assert.DoesNotContain(ComponentExpander.Expand(Song(), Corpus.Spec).Diagnostics, d => d.Severity == Severity.Error);   // a correct instance gives none
    }

    [Theory]
    [InlineData("  private property hour: Int = {hour: 3}.hour\n")]   // the field of a record is not the property of that name
    [InlineData("  private property seed: Int = Int(round(seed=2))\n")]   // the label of a named argument is not a use
    public void A_name_that_only_looks_like_the_property_is_not_a_use_of_it(string line)
    {
        string component = Shifter.Replace("  output notes: Notes", line + "  output notes: Notes", StringComparison.Ordinal);
        Assert.DoesNotContain("AMP2262", Codes(component));
    }

    [Theory]
    [InlineData("  private property gain: Int\n", "AMP2260")]   // no value: nobody can give it one
    [InlineData("  private property steps: Int = 1\n", "AMP2201")]   // the name of a public property
    [InlineData("  private property again: Int = later + 1\n  private property later: Int = 2\n", "AMP2262")]   // used before it is defined
    public void A_private_property_with_a_mistake_gets_a_clear_error(string line, string code)
    {
        string component = Shifter.Replace("  output notes: Notes", line + "  output notes: Notes", StringComparison.Ordinal);
        Assert.Contains(code, Codes(component));
    }

    [Fact]
    public void A_private_property_is_not_available_in_an_audio_component_yet()
    {
        string audio = "component koda.fx.hush version 1.0.0 {\n  input audio: Audio\n  private property level: Gain = -3dB\n  output audio: Audio\n  process {\n    emit audio |> gain(level)\n  }\n}\n\n";
        Assert.Contains("AMP2263", Codes(audio));
    }

    [Fact]
    public void A_property_without_private_keeps_its_old_hashes_exactly()
    {
        string plain = Shifter.Replace("  private property halfSteps: Int = steps * 1\n  private property semitones: Int = halfSteps + 5\n  private property modelVersion: String = \"v3\"\n", string.Empty, StringComparison.Ordinal)
            .Replace("transpose(semitones)", "transpose(steps + 5)", StringComparison.Ordinal);
        ComponentDefinition d = Only(plain);
        Assert.Empty(d.PrivateProperties);
        Assert.DoesNotContain("private", ComponentHashing.SemanticText(d, []), StringComparison.Ordinal);
        Assert.DoesNotContain("private", ComponentHashing.SemanticText(d, []), StringComparison.Ordinal);   // no private line: every hash and seal that exists stays as it was
    }

    [Fact]
    public void A_private_property_the_music_reads_is_content_and_one_it_does_not_read_is_metadata_for_the_seal_only()
    {
        ComponentDefinition basis = Only(Shifter);
        string semantic = ComponentHashing.SemanticText(basis, []);
        Assert.Contains("private-property=semitones:Int:halfSteps + 5", semantic, StringComparison.Ordinal);
        Assert.Contains("private-property=halfSteps:Int:steps * 1", semantic, StringComparison.Ordinal);   // read through semitones
        Assert.DoesNotContain("modelVersion", semantic, StringComparison.Ordinal);

        ComponentDefinition metadataChanged = Only(Shifter.Replace("\"v3\"", "\"v4\"", StringComparison.Ordinal));
        Assert.Equal(ComponentHashing.Semantic(basis, []), ComponentHashing.Semantic(metadataChanged, []));   // no render, no pin change
        Assert.NotEqual(ComponentHashing.Payload(basis, []), ComponentHashing.Payload(metadataChanged, []));   // but the seal sees it

        ComponentDefinition musicChanged = Only(Shifter.Replace("halfSteps + 5", "halfSteps + 6", StringComparison.Ordinal));
        Assert.NotEqual(ComponentHashing.Semantic(basis, []), ComponentHashing.Semantic(musicChanged, []));
    }

    [Fact]
    public void A_seal_is_voided_by_a_change_of_a_private_property_even_when_the_music_stays_the_same()
    {
        ComponentDefinition basis = Only(Shifter);
        SealCheck now = ComponentHashing.Check(basis, []);
        string sealedText = Shifter.TrimEnd().TrimEnd('}').TrimEnd() + "\n" + string.Join('\n', ComponentHashing.SealLines(now.Payload, now.Semantic)) + "\n}\n\n";
        Assert.Equal(SealState.Valid, ComponentHashing.Check(Only(sealedText), []).State);
        SealCheck changed = ComponentHashing.Check(Only(sealedText.Replace("\"v3\"", "\"v4\"", StringComparison.Ordinal)), []);
        Assert.Equal(SealState.Invalid, changed.State);
        Assert.Equal(now.Semantic, changed.Semantic);
    }

    [Fact]
    public void The_formatter_keeps_a_private_property_and_is_stable()
    {
        string once = Formatter.Format(Header + Shifter + Intro + Instance(), Corpus.Spec);
        Assert.Contains("  private property halfSteps: Int = steps * 1\n", once, StringComparison.Ordinal);
        Assert.Equal(once, Formatter.Format(once, Corpus.Spec));
    }

    private const string Phase = "dynamic helper phase() -> Int { return currentTime().minute % 8 }\n\n";

    [Fact]
    public void An_unused_private_property_with_a_volatile_value_is_never_run_and_a_used_one_is_an_ordinary_let()
    {
        string unused = Shifter.Replace("  private property modelVersion: String = \"v3\"\n", "  private property inspectedAt: Int = phase()\n", StringComparison.Ordinal);
        ComponentDefinition definition = Assert.Single(Parser.Parse(Header + Phase + unused, Corpus.Spec, new ParseOptions { RequireProjectLine = false }).Components);
        Assert.DoesNotContain("inspectedAt", ComponentHelper.Text(definition), StringComparison.Ordinal);   // not lowered: no volatile read, no capability, no recipe input
        AmpleDocument doc = Parser.Parse(Header + Phase + unused + Intro + Instance(), Corpus.Spec, new ParseOptions { ApplyNoteFlow = true });
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);

        string used = unused.Replace("transpose(semitones)", "transpose(semitones + inspectedAt)", StringComparison.Ordinal);
        ComponentDefinition withUse = Assert.Single(Parser.Parse(Header + Phase + used, Corpus.Spec, new ParseOptions { RequireProjectLine = false }).Components);
        Assert.Contains("let Int inspectedAt = phase();", ComponentHelper.Text(withUse), StringComparison.Ordinal);   // read by the music: evaluated like a let, so the dynamic rules apply to it
    }
}
