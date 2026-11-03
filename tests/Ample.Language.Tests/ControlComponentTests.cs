namespace Ample.Language.Tests;

/// <summary>A component that hands out a control next to its sound (plan: control lanes, C05).</summary>
public sealed class ControlComponentTests
{
    private const string Header = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n";

    private const string Traffic = "component koda.fx.traffic version 1.0.0 current {\n  id \"demo.koda.fx.traffic\"\n  input audio: Audio\n  output audio: Audio\n  output traffic: Control\n  process {\n    emit audio -> audio\n    emit audio |> follow(band=\"low\") -> traffic\n  }\n}\n";

    [Fact]
    public void A_component_can_have_a_control_output_made_by_follow()
    {
        ParseOptions options = new() { RequireProjectLine = false };
        ComponentDefinition d = Assert.Single(Parser.Parse(Traffic, Corpus.Spec, options).Components);
        TemplateSpec template = ComponentChecker.TemplateOf(d);
        Assert.Equal("audio", template.Outputs.First(o => o.Name == "audio").Type);
        Assert.Equal("control", template.Outputs.First(o => o.Name == "traffic").Type);
        AmpleDocument doc = Parser.Parse(Header + Traffic, Corpus.Spec);
        Assert.DoesNotContain(doc.Diagnostics, x => x.Severity == Severity.Error && x.Code.StartsWith("AMP22", StringComparison.Ordinal));
        Assert.Equal("level", AudioProcessors.Catalog(Corpus.Spec)["follow"].OutputPort);
        Assert.Equal("out", AudioProcessors.Catalog(Corpus.Spec)["delay"].OutputPort);
    }

    [Fact]
    public void Map_is_a_stage_after_follow_and_gives_the_control_a_unit()
    {
        string text = "component koda.fx.swell version 1.0.0 current {\n  id \"demo.koda.fx.swell\"\n  input audio: Audio\n  output audio: Audio\n  output cutoff: Control<Hz>\n  process {\n    emit audio -> audio\n    emit audio |> follow(band=\"low\") |> map(unit=\"Hz\", out_high=4000) -> cutoff\n  }\n}\n";
        AmpleDocument doc = Parser.Parse(Header + text, Corpus.Spec);
        Assert.DoesNotContain(doc.Diagnostics, x => x.Severity == Severity.Error && x.Code.StartsWith("AMP22", StringComparison.Ordinal));
        Assert.Equal("level", AudioProcessors.Catalog(Corpus.Spec)["map"].OutputPort);
    }

    [Fact]
    public void Curve_starts_a_control_without_audio_and_fire_turns_a_control_into_notes_for_an_instrument()
    {
        string curve = "component koda.fx.demo version 1.0.0 current {\n  id \"demo.koda.fx.demo\"\n  input audio: Audio\n  output audio: Audio\n  output sweep: Control\n  process {\n    emit audio -> audio\n    emit curve(value=\"auto(1:0, 4:1000)\") -> sweep\n  }\n}\n";
        AmpleDocument a = Parser.Parse(Header + curve, Corpus.Spec);
        Assert.DoesNotContain(a.Diagnostics, x => x.Severity == Severity.Error && x.Code.StartsWith("AMP22", StringComparison.Ordinal));
        Assert.True(AudioProcessors.Catalog(Corpus.Spec)["curve"].Source);
        string fire = "component koda.fx.demo version 1.0.0 current {\n  id \"demo.koda.fx.demo\"\n  input audio: Audio\n  output audio: Audio\n  process {\n    emit audio |> follow(band=\"low\") |> fire(note=36) |> piano() -> audio\n  }\n}\n";
        AmpleDocument b = Parser.Parse(Header + fire, Corpus.Spec);
        Assert.DoesNotContain(b.Diagnostics, x => x.Severity == Severity.Error && x.Code.StartsWith("AMP22", StringComparison.Ordinal));
        Assert.True(AudioProcessors.Catalog(Corpus.Spec)["fire"].Notes);
        Assert.Equal("notes", AudioProcessors.Catalog(Corpus.Spec)["fire"].OutputPort);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(6, 6)]
    public void Repeat_makes_as_many_copies_as_the_property_says_and_spreads_them_over_the_length(int cars, int copies)
    {
        string text = "component koda.fx.street version 1.0.0 current {\n  id \"demo.koda.fx.street\"\n  input bed: Audio\n  input car: Audio\n  property cars: Int = CARS\n  property delay: Duration = 2bar\n  property length: Duration = 24bar\n  output audio: Audio\n  process {\n    emit mix(bed, car |> repeat(times=cars, delay=delay, length=length)) -> audio\n  }\n}\n";
        ComponentDefinition d = Assert.Single(Parser.Parse(Header + text.Replace("CARS", cars.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal), Corpus.Spec, new ParseOptions { RequireProjectLine = false }).Components);
        AudioGraph graph = AudioGraphBuilder.Build(d, Corpus.Spec, new Dictionary<string, string>(StringComparer.Ordinal) { ["cars"] = cars.ToString(System.Globalization.CultureInfo.InvariantCulture), ["delay"] = "2bar", ["length"] = "24bar" }, 100);
        Assert.DoesNotContain(graph.Issues, i => i.Severity == Severity.Error);
        AudioNode[] shifts = [.. graph.Nodes.Where(n => n.Processor == "shift")];
        Assert.Equal(copies, shifts.Length);   // 2 bars of delay: every copy is shifted, also the first
        double[] steps = [.. shifts.Select(n => double.Parse(n.Parameters.First(p => p.Name == "seconds").Value, System.Globalization.CultureInfo.InvariantCulture))];
        Assert.Equal(4.8, steps[0], 0.01);   // 2 bars at 100 bpm: 4.8 seconds before the first car
        if (copies > 1) Assert.Equal((24 - 2) * 4 * 60.0 / 100 / cars, steps[1], 0.01);   // then every copy moves on by its share of the rest of the length
    }

    [Fact]
    public void A_long_distance_is_a_chain_of_shifts_none_of_them_over_the_limit_of_30_seconds_and_a_mix_of_two_sounds_is_a_valid_bus()
    {
        string text = "component koda.fx.street version 1.0.0 current {\n  id \"demo.koda.fx.street\"\n  input bed: Audio\n  input car: Audio\n  property cars: Int = 3\n  output audio: Audio\n  process {\n    emit mix(bed, car |> repeat(times=cars, delay=0bar, length=72bar)) -> audio\n  }\n}\n";
        ComponentDefinition d = Assert.Single(Parser.Parse(Header + text, Corpus.Spec, new ParseOptions { RequireProjectLine = false }).Components);
        AudioGraph graph = AudioGraphBuilder.Build(d, Corpus.Spec, new Dictionary<string, string>(StringComparer.Ordinal) { ["cars"] = "3" }, 100);
        Assert.DoesNotContain(graph.Issues, i => i.Severity == Severity.Error);
        double[] steps = [.. graph.Nodes.Where(x => x.Processor == "shift").Select(x => double.Parse(x.Parameters.First(p => p.Name == "seconds").Value, System.Globalization.CultureInfo.InvariantCulture))];
        Assert.All(steps, step => Assert.InRange(step, -30, 30));
        Assert.Equal(2 * 57.6, steps.Sum(), 0.01);   // 72 bars at 100 bpm are 172.8 seconds: the cars are at 0, 57.6 and 115.2
        AudioNode mix = graph.Nodes.First(x => x.Processor == "mix");
        Assert.Equal("4", mix.Parameters.First(p => p.Name == "inputs").Value);   // a bus has 4 to 16 inputs
    }

    [Fact]
    public void Trim_is_a_stage_before_repeat_so_a_replaced_recording_still_fits()
    {
        string text = "component koda.fx.street version 1.0.0 current {\n  id \"demo.koda.fx.street\"\n  input bed: Audio\n  input car: Audio\n  property cars: Int = 3\n  output audio: Audio\n  process {\n    emit mix(bed, car |> trim(sections=all, fadeout=300ms) |> repeat(times=cars, delay=0bar, length=24bar)) -> audio\n  }\n}\n";
        ComponentDefinition d = Assert.Single(Parser.Parse(Header + text, Corpus.Spec, new ParseOptions { RequireProjectLine = false }).Components);
        AudioGraph graph = AudioGraphBuilder.Build(d, Corpus.Spec, new Dictionary<string, string>(StringComparer.Ordinal) { ["cars"] = "3" }, 100);
        Assert.DoesNotContain(graph.Issues, i => i.Severity == Severity.Error);
        AudioNode trim = Assert.Single(graph.Nodes, x => x.Processor == "trim");
        Assert.Equal("all", trim.Parameters.First(p => p.Name == "sections").Value);
    }

    [Fact]
    public void A_region_output_of_a_component_connects_to_a_region_input_and_merges_back()
    {
        string text = Header + "component koda.fx.street version 1.0.0 current {\n  id \"demo.koda.fx.street\"\n  input bed: Audio\n  input car: Audio\n  property cars: Int = 3\n  output audio: Audio\n  output road: Region\n  process {\n    let passes = car |> repeat(times=cars, delay=0bar, length=24bar)\n    emit mix(bed, passes) -> audio\n    emit passes -> road\n  }\n}\n" + "import-audio r at 1 for 4 {\n  file samples/r.wav\n}\n\nimport-audio c at 1 for 4 {\n  file samples/c.wav\n}\n\nkoda.fx.street scene at 1 for 24 {\n  from r.audio -> bed\n  from c.audio -> car\n}\n\nvolume carFade {\n  gain fadeout(23, 2)\n  from scene.road -> region\n}\n\nregion-merge back {\n  from scene.audio -> body\n  from carFade.region -> region\n}\n";
        ExpandedSong expanded = ModuleExpander.Expand(text, Corpus.Spec, _ => null);
        Assert.Contains("-> region", expanded.Text, StringComparison.Ordinal);
        AmpleDocument doc = Parser.Parse(expanded.Text, Corpus.Spec);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error && d.Code is "AMP2007" or "AMP2006" or "AMP2005");
        Assert.Equal("region", ComponentChecker.TemplateOf(Assert.Single(Parser.Parse(text, Corpus.Spec).Components)).Outputs.First(o => o.Name == "road").Type);
    }

    [Theory]
    [InlineData("if(on, -18dB, -60dB)", "true", "-18dB")]
    [InlineData("if(on, -18dB, -60dB)", "false", "-60dB")]
    [InlineData("-60dB + 42dB * on", "true", "-18dB")]
    [InlineData("int(2.7) + on", "false", "2")]
    public void A_bool_is_1_or_0_in_a_calculation_and_if_picks_one_of_two(string expression, string on, string expected)
    {
        Assert.Equal(expected, ComponentMigrations.EvaluateScoped(expression, new Dictionary<string, string>(StringComparer.Ordinal) { ["on"] = on }, out _));
    }

    [Theory]
    [InlineData("true", "1", "volume")]
    [InlineData("false", "1", "silence 10s")]
    [InlineData("false", "0", "silence auto")]
    public void An_if_takes_one_branch_and_silence_is_a_source(string tone, string level, string expected)
    {
        ComponentDefinition d = Assert.Single(Parser.Parse(Header + "component koda.fx.pick version 1.0.0 current {\n  id \"demo.koda.fx.pick\"\n  input sad: Audio { optional true }\n  property tone: Bool = true\n  property level: Int = 1\n  output audio: Audio\n  process {\n    if (tone) {\n      let bed = sad |> gain(-6dB)\n    } else if (level) {\n      let bed = silence(10s)\n    } else {\n      let bed = silence()\n    }\n    emit bed -> audio\n  }\n}\n", Corpus.Spec, new ParseOptions { RequireProjectLine = false }).Components);
        AudioGraph graph = AudioGraphBuilder.Build(d, Corpus.Spec, new Dictionary<string, string>(StringComparer.Ordinal) { ["tone"] = tone, ["level"] = level }, 100);
        Assert.DoesNotContain(graph.Issues, i => i.Severity == Severity.Error);
        AudioNode only = Assert.Single(graph.Nodes);
        string text = only.Template == "silence" ? "silence " + (only.Parameters.FirstOrDefault(p => p.Name == "length").Value ?? "auto") : only.Template;   // the length as written; without one the block is as long as its block (the default auto, read by the renderer)
        Assert.Equal(expected, text);
    }

    [Fact]
    public void Repeat_refuses_what_it_cannot_do()
    {
        string many = "component koda.fx.street version 1.0.0 current {\n  id \"demo.koda.fx.street\"\n  input bed: Audio\n  input car: Audio\n  property cars: Int = 4\n  property delay: Duration = 2bar\n  property length: Duration = 24bar\n  output audio: Audio\n  process {\n    emit mix(bed, car |> repeat(times=cars, delay=delay, length=length)) -> audio\n  }\n}\n";
        ComponentDefinition d = Assert.Single(Parser.Parse(Header + many, Corpus.Spec, new ParseOptions { RequireProjectLine = false }).Components);
        AudioGraph tooMany = AudioGraphBuilder.Build(d, Corpus.Spec, new Dictionary<string, string>(StringComparer.Ordinal) { ["cars"] = "40", ["delay"] = "2bar", ["length"] = "24bar" }, 100);
        Assert.Contains(tooMany.Issues, i => i.Message.Contains("1 to 16", StringComparison.Ordinal));
        AudioGraph shortLength = AudioGraphBuilder.Build(d, Corpus.Spec, new Dictionary<string, string>(StringComparer.Ordinal) { ["cars"] = "3", ["delay"] = "30bar", ["length"] = "24bar" }, 100);
        Assert.Contains(shortLength.Issues, i => i.Message.Contains("longer than delay", StringComparison.Ordinal));
    }

    [Fact]
    public void A_typed_control_port_carries_its_unit()
    {
        Assert.True(ComponentChecker.IsControlType("Control"));
        Assert.True(ComponentChecker.IsControlType("Control<Hz>"));
        Assert.False(ComponentChecker.IsControlType("Control<furlong>"));
        Assert.False(ComponentChecker.IsControlType("Audio"));
    }
}
