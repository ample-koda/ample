namespace Ample.Language.Tests;

/// <summary>Audio components (plan/components/audio): the Audio type, the processor catalog, the audio process as a graph, lowering to blocks, hashes and seals, migration of ports, the editor.</summary>
public sealed class ComponentAudioTests
{
    private const string Header = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n";

    private const string Echo = "component koda.fx.dreamEcho version 1.0.0 current {\n  id \"demo.koda.fx.dream-echo\"\n  input audio: Audio\n  #[MinValue(0%), MaxValue(90%)]\n  property feedback: Percent = 30%\n  property delayTime: Duration = 0.25beat\n  property room: Percent = 40%\n  output audio: Audio\n  process {\n    emit audio\n      |> delay(time=delayTime, feedback=feedback)\n      |> reverb(size=room)\n  }\n}\n\n";

    private const string Source = "midi-clip melody at 1 for 1 {\n  notes 1:0:1:60:100\n}\n\npiano lead at 1 for 1 {\n  from melody.notes -> notes\n}\n\n";

    private const string Instance = "koda.fx.dreamEcho space {\n  feedback 28%\n  room 45%\n  from lead.audio -> audio\n}\n";

    private static ComponentDefinition Only(string text) => Assert.Single(Parser.Parse(text, Corpus.Spec, new ParseOptions { RequireProjectLine = false }).Components);

    private static string Process(string body, string properties = "") => $"component koda.fx.test version 1.0.0 {{\n  input audio: Audio\n  {properties}\n  output audio: Audio\n  process {{\n{body}\n  }}\n}}\n";

    private static string[] Codes(string component) => [.. Parser.Parse(Header + component, Corpus.Spec).Diagnostics.Where(d => d.Code.StartsWith("AMP22", StringComparison.Ordinal) || d.Code.StartsWith("AMP2001", StringComparison.Ordinal)).Select(d => d.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static AudioGraph Delayed(string component) => AudioGraphBuilder.Build(Only(component), Corpus.Spec.WithLatency("limiter", 5), new Dictionary<string, string>(StringComparer.Ordinal), 100);

    private static AudioGraph Graph(string component, string properties = "") => AudioGraphBuilder.Build(Only(component), Corpus.Spec, new Dictionary<string, string>(StringComparer.Ordinal), 100);

    [Fact]
    public void An_audio_contract_parses_and_its_ports_are_audio_streams()
    {
        ComponentDefinition d = Only(Echo);
        Assert.True(d.IsAudio);
        TemplateSpec template = ComponentChecker.TemplateOf(d);
        Assert.Equal("audio", template.Inputs[0].Type);
        Assert.Equal("audio", template.Outputs[0].Type);
        Assert.DoesNotContain(Parser.Parse(Header + Echo, Corpus.Spec).Diagnostics, x => x.Severity == Severity.Error);
    }

    [Fact]
    public void A_notes_output_next_to_audio_ports_is_refused_notes_may_only_go_in()
    {
        string mixed = "component koda.fx.mixed version 1.0.0 {\n  input audio: Audio\n  output notes: Notes\n  output audio: Audio\n  process {\n    emit audio\n  }\n}\n";
        Assert.Contains("AMP2230", Codes(mixed));   // a Notes output next to Audio ports
        Assert.DoesNotContain("AMP2230", Codes("component koda.fx.in version 1.0.0 {\n  input notes: Notes\n  output audio: Audio\n  process {\n    emit notes |> piano()\n  }\n}\n"));   // Notes in, audio out is the point
    }

    [Fact]
    public void The_pipeline_is_typed_by_what_is_in_front_of_it()
    {
        Assert.Contains("AMP2234", Codes(Process("    emit audio |> transpose(+12)")));   // a Notes operation on Audio
        Assert.Contains("AMP2232", Codes(Process("    emit reverb(size=50%)")));   // a processor without audio in front
        Assert.Contains("AMP2234", Codes(Process("    emit audio |> wobble(depth=3)")));   // not a processor
    }

    [Fact]
    public void The_arguments_have_the_unit_of_the_parameter()
    {
        Assert.Contains("AMP2232", Codes(Process("    emit audio |> delay(time=50%)")));   // a Percent for a time
        Assert.Contains("AMP2232", Codes(Process("    emit audio |> compressor(threshold=250ms)")));   // a time for a gain
        Assert.Contains("AMP2235", Codes(Process("    emit audio |> delay(wobble=1)")));   // not a parameter
        Assert.Empty(Codes(Process("    emit audio |> delay(time=250ms, feedback=30%)")));
    }

    [Fact]
    public void Beats_become_milliseconds_with_the_tempo_and_a_property_can_be_calculated_with()
    {
        AudioGraph g = Graph(Process("    emit audio |> delay(time=0.5beat, feedback=space * 0.5)", "property space: Percent = 40%"));
        Assert.Empty(g.Issues);
        AudioNode delay = Assert.Single(g.Nodes);
        Assert.Contains(("time", "300 ms"), delay.Parameters);   // half a beat at 100 BPM
        Assert.Contains(("feedback", "20%"), delay.Parameters);
    }

    [Fact]
    public void A_name_must_be_defined_before_it_is_used_so_a_graph_cannot_loop()
    {
        Assert.Contains("AMP2231", Codes(Process("    let a = b |> delay(time=100ms)\n    let b = audio\n    emit a")));
        Assert.Contains("AMP2231", Codes(Process("    emit signal")));
    }

    [Fact]
    public void Every_output_must_be_given_and_the_graph_has_a_limit()
    {
        Assert.Contains("AMP2233", Codes(Process("    let unused = audio")));
        string big = string.Join("\n", Enumerable.Range(0, 70).Select(i => "    let a" + i + " = " + (i == 0 ? "audio" : "a" + (i - 1)) + " |> pan(0)")) + "\n    emit a69";
        Assert.Contains("AMP2233", Codes(Process(big)));
    }

    [Fact]
    public void A_wet_dry_component_is_a_real_subgraph_with_a_bus()
    {
        string wetDry = Process("    let dry = audio\n    let wet = audio |> delay(time=200ms) |> reverb(size=50%)\n    emit mix(dry |> gain(-3dB), wet |> gain(-8dB))");
        AudioGraph g = Graph(wetDry);
        Assert.Empty(g.Issues);
        Assert.Equal(["delay1", "reverb1", "gain1", "gain2", "mix1"], g.Nodes.Select(n => n.Id));
        AudioNode mix = g.Nodes[^1];
        Assert.Equal("bus", mix.Template);
        Assert.Equal(["in1", "in2"], mix.Inputs.Select(i => i.Port));
        Assert.Equal("node:mix1", g.Outputs["audio"]);
    }

    [Fact]
    public void A_second_audio_input_is_wired_by_name()
    {
        string pump = "component koda.fx.pump version 1.0.0 {\n  input audio: Audio\n  input sidechain: Audio\n  output audio: Audio\n  process {\n    emit audio |> compressor(threshold=-20dB, key=sidechain)\n  }\n}\n";
        AudioGraph g = Graph(pump);
        Assert.Empty(g.Issues);
        Assert.Contains(("key", "in:sidechain"), g.Nodes[0].Inputs);
    }

    [Fact]
    public void An_instance_becomes_ordinary_blocks_that_are_wired_through_the_normal_graph()
    {
        string song = Header + Echo + Source + Instance + "\nlimiter safety {\n  from space.audio -> audio\n}\n";
        ExpandedSong expanded = ModuleExpander.Expand(song, Corpus.Spec, _ => null);
        Assert.Contains("delay space__delay1 {", expanded.Text, StringComparison.Ordinal);
        Assert.Contains("reverb space__reverb1 {", expanded.Text, StringComparison.Ordinal);
        Assert.Contains("from lead.audio -> audio", expanded.Text, StringComparison.Ordinal);
        Assert.Contains("from space__delay1.audio -> audio", expanded.Text, StringComparison.Ordinal);
        Assert.Contains("from space__reverb1.audio -> audio", expanded.Text, StringComparison.Ordinal);   // the limiter reads the last processor
        Assert.DoesNotContain("koda.fx.dreamEcho space", expanded.Text, StringComparison.Ordinal);
        AmpleDocument doc = Parser.Parse(expanded.Text, Corpus.Spec);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error && d.Code != "AMP2001");
    }

    [Fact]
    public void Several_instances_share_one_definition_and_get_their_own_blocks()
    {
        string second = "koda.fx.dreamEcho other {\n  room 80%\n  from lead.audio -> audio\n}\n";
        ExpandedSong expanded = ModuleExpander.Expand(Header + Echo + Source + Instance + "\n" + second, Corpus.Spec, _ => null);
        Assert.Contains("space__delay1", expanded.Text, StringComparison.Ordinal);
        Assert.Contains("other__delay1", expanded.Text, StringComparison.Ordinal);
        ComponentUsage usage = Assert.Single(expanded.Components!);   // one definition, one usage
        Assert.Equal("koda.fx.dreamEcho", usage.Name);
        Assert.Contains("size 80%", expanded.Text, StringComparison.Ordinal);
        Assert.Contains("size 45%", expanded.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Instances_can_feed_each_other()
    {
        string pump = "component koda.fx.gainer version 1.0.0 {\n  input audio: Audio\n  output audio: Audio\n  process {\n    emit audio |> gain(-6dB)\n  }\n}\n";
        string chained = "koda.fx.gainer after {\n  from space.audio -> audio\n}\n";
        ExpandedSong expanded = ModuleExpander.Expand(Header + Echo + pump + Source + Instance + "\n" + chained, Corpus.Spec, _ => null);
        Assert.Contains("from space__reverb1.audio -> audio", expanded.Text, StringComparison.Ordinal);
        Assert.Contains("volume after__gain1 {", expanded.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_semantic_hash_follows_the_processors_and_ignores_the_documentation()
    {
        ComponentDefinition plain = Only(Echo);
        ComponentDefinition documented = Only("/** Now with a description. */\n" + Echo.Replace("component", "#[DisplayName(\"Dream\")]\ncomponent", StringComparison.Ordinal));
        string Semantic(ComponentDefinition d) => ComponentHashing.Semantic(d, ComponentHashing.DependenciesOf(d, Corpus.Spec, () => []));
        Assert.Equal(Semantic(plain), Semantic(documented));
        ComponentDefinition changed = Only(Echo.Replace("reverb(size=room)", "reverb(size=room * 0.5)", StringComparison.Ordinal));
        Assert.NotEqual(Semantic(plain), Semantic(changed));
        Assert.Contains("processor=delay@1.0", ComponentHashing.DependenciesOf(plain, Corpus.Spec, () => []));
    }

    [Fact]
    public void A_sealed_audio_component_is_valid_and_a_changed_one_is_not()
    {
        ComponentEditResult sealedResult = ComponentEdits.Seal(Echo, "koda.fx.dreamEcho", null, Corpus.Spec);
        Assert.True(sealedResult.Ok, sealedResult.Message);
        ComponentDefinition d = Only(sealedResult.Text);
        Assert.Equal(SealState.Valid, ComponentHashing.Check(d, ComponentHashing.DependenciesOf(d, Corpus.Spec, () => [])).State);
        ComponentDefinition tampered = Only(sealedResult.Text.Replace("reverb(size=room)", "reverb(size=100%)", StringComparison.Ordinal));
        Assert.Equal(SealState.Invalid, ComponentHashing.Check(tampered, ComponentHashing.DependenciesOf(tampered, Corpus.Spec, () => [])).State);
    }

    [Fact]
    public void Preview_and_latency_are_derived_from_the_processors()
    {
        Assert.True(Graph(Echo).ClientPreview);
        // no built-in processor delays its signal (the limiter looks ahead over a whole file); one that declares a latency is counted
        Assert.Equal(0, Graph(Process("    emit audio |> limiter(ceiling=-1dB)")).LatencyMs);
        Assert.Equal(5, Delayed(Process("    emit audio |> limiter(ceiling=-1dB)")).LatencyMs);
        AudioGraph noPreview = Graph(Process("    emit audio |> denoise()"));
        Assert.False(noPreview.ClientPreview);
        Assert.Contains("denoise", noPreview.NoPreview);
    }

    [Fact]
    public void A_migration_can_rename_an_audio_port_and_a_musical_change_must_be_acknowledged()
    {
        string v2 = "component koda.fx.dreamEcho version 2.0.0 current {\n  id \"demo.koda.fx.dream-echo\"\n  input signal: Audio\n  property feedback: Percent = 30%\n  property delayTime: Duration = 0.25beat\n  property room: Percent = 40%\n  output audio: Audio\n  migrate 1.0.0 -> 2.0.0 {\n    impact musicalChange\n    message \"The echo is wider in version 2.\"\n    port input.audio -> input.signal\n  }\n  process {\n    emit signal |> delay(time=delayTime, feedback=feedback) |> reverb(size=room)\n  }\n}\n\n";
        string song = Header + Echo.Replace(" current", string.Empty, StringComparison.Ordinal) + v2 + Source + Instance.Replace("space {", "space pinned@1.0 {", StringComparison.Ordinal);
        MigrationPlan plan = ComponentMigrations.Plan(song, [], "space", "2.0.0", Corpus.Spec);
        Assert.True(plan.Ok, plan.Problem);
        Assert.True(plan.NeedsAcknowledgement);
        Assert.Contains("from lead.audio -> signal", plan.Text, StringComparison.Ordinal);
        Assert.Contains("space pinned@2.0.0 {", plan.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Completion_after_audio_pipe_offers_the_processors_and_inside_a_call_the_parameters()
    {
        string text = Header + Process("    emit audio |> ");
        AmpleDocument doc = Parser.Parse(text, Corpus.Spec);
        int line = text.Split('\n').ToList().FindIndex(l => l.Contains("emit audio |> ", StringComparison.Ordinal)) + 1;
        IReadOnlyList<CompletionItem> items = Queries.CompletionAt(doc, Corpus.Spec, line, text.Split('\n')[line - 1].Length + 1);
        Assert.Contains(items, i => i.Label == "delay");
        Assert.Contains(items, i => i.Label == "mix");
        Assert.DoesNotContain(items, i => i.Label == "transpose");
        string inside = Header + Process("    emit audio |> delay(");
        AmpleDocument doc2 = Parser.Parse(inside, Corpus.Spec);
        int line2 = inside.Split('\n').ToList().FindIndex(l => l.Contains("delay(", StringComparison.Ordinal)) + 1;
        Assert.Contains(Queries.CompletionAt(doc2, Corpus.Spec, line2, inside.Split('\n')[line2 - 1].Length + 1), i => i.Label == "time=");
    }

    [Fact]
    public void Hover_on_a_processor_in_the_process_gives_its_signature()
    {
        string text = Header + Echo;
        AmpleDocument doc = Parser.Parse(text, Corpus.Spec);
        int line = text.Split('\n').ToList().FindIndex(l => l.Contains("|> delay(", StringComparison.Ordinal)) + 1;
        int col = text.Split('\n')[line - 1].IndexOf("delay", StringComparison.Ordinal) + 2;
        string? hover = Queries.HoverAt(doc, Corpus.Spec, line, col);
        Assert.NotNull(hover);
        Assert.Contains("Audio |> delay(", hover, StringComparison.Ordinal);
        Assert.Contains("browser preview", hover, StringComparison.Ordinal);
    }

    [Fact]
    public void The_processor_catalog_comes_from_the_effect_blocks_and_has_gain_for_volume()
    {
        IReadOnlyDictionary<string, AudioProcessor> catalog = AudioProcessors.Catalog(Corpus.Spec);
        Assert.Equal("delay", catalog["delay"].Template);
        Assert.Equal("volume", catalog["gain"].Template);
        Assert.False(catalog.ContainsKey("volume"));
        Assert.Contains(catalog["compressor"].ExtraInputs, i => i == "key");
        Assert.Equal("Duration", catalog["delay"].Parameter("time")!.Type);
        Assert.Equal("Percent", catalog["delay"].Parameter("feedback")!.Type);
        Assert.False(catalog.ContainsKey("bus"));
        Assert.False(catalog.ContainsKey("master"));
    }

    [Fact]
    public void An_input_that_goes_straight_to_an_output_is_a_pass_through_the_reader_gets_what_feeds_it()
    {
        string bypass = "component koda.fx.bypass version 1.0.0 current {\n  id \"demo.koda.fx.bypass\"\n  input audio: Audio\n  output audio: Audio\n  process {\n    emit audio\n  }\n}\n\n";
        string song = Header + bypass + Source + "koda.fx.bypass thru {\n  from lead.audio -> audio\n}\n\nlimiter safety {\n  from thru.audio -> audio\n}\n";
        ExpandedSong expanded = ModuleExpander.Expand(song, Corpus.Spec, _ => null);
        Assert.DoesNotContain(expanded.Diagnostics, d => d.Code == "AMP2231");
        Assert.Contains("from lead.audio -> audio", expanded.Text, StringComparison.Ordinal);   // no block for the bypass, the limiter reads the piano
    }

    [Fact]
    public void Branches_with_different_latency_are_aligned_with_a_shift_before_they_meet()
    {
        AudioGraph g = Delayed(Process("    let dry = audio\n    let wet = audio |> limiter()\n    emit mix(dry, wet)"));
        Assert.Empty(g.Issues);
        AudioNode align = Assert.Single(g.Nodes, n => n.Template == "shift");
        Assert.Equal("align1", align.Id);
        Assert.Contains(("in", "in:audio"), align.Inputs);   // the dry branch is the short one
        Assert.True(g.LatencyMs > 0);
        AudioNode bus = Assert.Single(g.Nodes, n => n.Template == "bus");
        Assert.Contains(bus.Inputs, i => i.Source == "node:align1");
    }

    private const string Big = "component koda.fx.big version 1.0.0 current {\n  id \"demo.koda.fx.big\"\n  input audio: Audio\n  output audio: Audio\n  process {\n    emit audio |> koda.fx.dreamEcho(feedback=20%) |> gain(-3dB)\n  }\n}\n\n";

    [Fact]
    public void An_audio_component_can_call_another_and_its_graph_is_spliced_in_with_its_code_in_the_hash()
    {
        ComponentDefinition inner = Only(Echo);
        ComponentDefinition big = Only(Big);
        AudioGraph g = AudioGraphBuilder.Build(big, Corpus.Spec, new Dictionary<string, string>(StringComparer.Ordinal), 100, [inner, big]);
        Assert.Empty(g.Issues);
        Assert.Equal(["dreamEcho1_delay1", "dreamEcho1_reverb1", "gain1"], g.Nodes.Select(n => n.Id));
        Assert.Contains(("feedback", "20%"), g.Nodes[0].Parameters);   // the argument is the property of the nested component
        Assert.Contains(("in", "node:dreamEcho1_delay1"), g.Nodes[1].Inputs);
        Assert.Contains(g.Dependencies, x => x.StartsWith("component=koda.fx.dreamEcho@1.0.0:", StringComparison.Ordinal));
        // the nested code changes: the hash of the outer one changes
        ComponentDefinition changed = Only(Echo.Replace("30%", "35%", StringComparison.Ordinal));
        Assert.NotEqual(g.Dependencies, AudioGraphBuilder.Build(big, Corpus.Spec, new Dictionary<string, string>(StringComparer.Ordinal), 100, [changed, big]).Dependencies);
    }

    [Fact]
    public void A_nested_call_can_pin_the_version_of_the_component_it_calls()
    {
        ComponentDefinition inner = Only(Echo);
        ComponentDefinition pinned = Only(Big.Replace("koda.fx.dreamEcho(", "koda.fx.dreamEcho@1.0.0(", StringComparison.Ordinal));
        AudioGraph ok = AudioGraphBuilder.Build(pinned, Corpus.Spec, new Dictionary<string, string>(StringComparer.Ordinal), 100, [inner, pinned]);
        Assert.Empty(ok.Issues);
        ComponentDefinition wrong = Only(Big.Replace("koda.fx.dreamEcho(", "koda.fx.dreamEcho@2.0.0(", StringComparison.Ordinal));
        AudioGraph bad = AudioGraphBuilder.Build(wrong, Corpus.Spec, new Dictionary<string, string>(StringComparer.Ordinal), 100, [inner, wrong]);
        Assert.Contains(bad.Issues, i => i.Code == "AMP2248" && i.Message.Contains("No version", StringComparison.Ordinal));
    }

    [Fact]
    public void A_nested_call_that_circles_or_goes_too_deep_is_refused()
    {
        ComponentDefinition self = Only(Big.Replace("koda.fx.dreamEcho(feedback=20%)", "koda.fx.big()", StringComparison.Ordinal));
        AudioGraph g = AudioGraphBuilder.Build(self, Corpus.Spec, new Dictionary<string, string>(StringComparer.Ordinal), 100, [self]);
        Assert.Contains(g.Issues, i => i.Code == "AMP2247" && i.Message.Contains("calls itself", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_audio_components_that_call_each_other_are_a_circle_and_the_message_names_it()
    {
        const string a = "component koda.fx.a version 1.0.0 {\n  input audio: Audio\n  output audio: Audio\n  process {\n    emit audio |> koda.fx.b()\n  }\n}\n";
        string b = a.Replace("koda.fx.a ", "koda.fx.b ", StringComparison.Ordinal).Replace("koda.fx.b()", "koda.fx.a()", StringComparison.Ordinal);
        ComponentDefinition first = Only(a), second = Only(b);
        AudioGraph g = AudioGraphBuilder.Build(first, Corpus.Spec, new Dictionary<string, string>(StringComparer.Ordinal), 100, [first, second]);
        Assert.Contains(g.Issues, i => i.Code == "AMP2247" && i.Message.Contains("koda.fx.a -> koda.fx.b -> koda.fx.a", StringComparison.Ordinal));
    }

    [Fact]
    public void A_graph_can_run_live_in_the_browser_only_when_every_processor_has_a_real_time_chain()
    {
        AudioGraph live = Graph(Process("    let wet = audio |> delay(time=250ms)\n    emit mix(audio, wet |> reverb(size=50%))"));
        Assert.True(live.LivePreview);
        Assert.Empty(live.NoLive);
        AudioGraph limited = Graph(Process("    emit audio |> delay(time=250ms) |> bitcrusher()"));
        Assert.False(limited.LivePreview);
        Assert.Equal(["bitcrusher"], limited.NoLive);
    }

    private const string Voice = "component koda.fx.voice version 1.0.0 current {\n  id \"demo.koda.fx.voice\"\n  input notes: Notes\n  #[MinValue(0%), MaxValue(100%)]\n  property room: Percent = 30%\n  output audio: Audio\n  process {\n    emit notes |> piano() |> reverb(size=room)\n  }\n}\n\n";

    [Fact]
    public void Notes_go_in_and_sound_comes_out_an_instrument_stands_between()
    {
        Assert.Empty(Codes(Voice));
        AudioGraph g = Graph(Voice);
        Assert.Empty(g.Issues);
        Assert.Equal(["piano1", "reverb1"], g.Nodes.Select(n => n.Id));
        Assert.Contains(("notes", "in:notes"), g.Nodes[0].Inputs);   // the instrument reads the Notes input
        Assert.Equal("audio", g.Nodes[0].OutputPort);
        Assert.Contains(("in", "node:piano1"), g.Nodes[1].Inputs);   // the effect reads the sound of the instrument
        Assert.Contains("processor=piano@1.0", g.Dependencies);
    }

    [Fact]
    public void Notes_are_not_audio_and_audio_is_not_notes()
    {
        Assert.Contains("AMP2232", Codes(Voice.Replace("emit notes |> piano() |> reverb(size=room)", "emit notes", StringComparison.Ordinal)));   // notes straight to an output
        Assert.Contains("AMP2232", Codes(Voice.Replace("notes |> piano() |> reverb(size=room)", "notes |> reverb(size=room)", StringComparison.Ordinal)));   // an effect on notes
        Assert.Contains("AMP2232", Codes(Voice.Replace("notes |> piano() |> reverb(size=room)", "mix(notes, notes |> piano())", StringComparison.Ordinal)));   // notes in a mix
        string twoInputs = Voice.Replace("input notes: Notes\n", "input notes: Notes\n  input audio: Audio\n", StringComparison.Ordinal).Replace("emit notes |> piano() |> reverb(size=room)", "emit audio |> piano()", StringComparison.Ordinal);
        Assert.Contains("AMP2232", Codes(twoInputs));   // an instrument on audio
    }

    [Fact]
    public void An_instance_with_notes_becomes_an_instrument_block_with_the_placement_of_the_instance_wired_to_the_notes_source()
    {
        string song = Header + Voice + "midi-clip melody at 1 for 2 {\n  notes 1:0:1:60:100 1:1:1:64:100\n}\n\nkoda.fx.voice lead at 1 for 2 {\n  room 40%\n  from melody.notes -> notes\n}\n\nlimiter safety {\n  from lead.audio -> audio\n}\n";
        ExpandedSong expanded = ModuleExpander.Expand(song, Corpus.Spec, _ => null);
        Assert.DoesNotContain(expanded.Diagnostics, d => d.Code is "AMP2218" or "AMP2231" or "AMP2232");
        Assert.Contains("piano lead__piano1 at 1 for 2 {", expanded.Text, StringComparison.Ordinal);
        Assert.Contains("from melody.notes -> notes", expanded.Text, StringComparison.Ordinal);
        Assert.Contains("reverb lead__reverb1 {", expanded.Text, StringComparison.Ordinal);
        Assert.Contains("from lead__reverb1.audio -> audio", expanded.Text, StringComparison.Ordinal);   // the limiter reads the end of the chain
        AmpleDocument doc = Parser.Parse(expanded.Text, Corpus.Spec);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error && d.Code != "AMP2001");
        // a notes input that is not connected says so
        string loose = Header + Voice + "koda.fx.voice lead at 1 for 2 {\n}\n";
        Assert.Contains(ModuleExpander.Expand(loose, Corpus.Spec, _ => null).Diagnostics, d => d.Code == "AMP2218");
    }

    [Fact]
    public void Every_instrument_block_can_stand_between_notes_and_sound_also_delay_llama()
    {
        IReadOnlyDictionary<string, AudioProcessor> instruments = AudioProcessors.Instruments(Corpus.Spec);
        foreach (string name in (string[])["piano", "bass", "guitar", "synth", "gm", "sampler", "delay-llama"]) Assert.Contains(name, instruments.Keys);
        Assert.DoesNotContain("drums", instruments.Keys);   // drums read a trigger, not notes
        string llama = "component koda.fx.llama version 1.0.0 {\n  id \"demo.koda.fx.llama\"\n  input notes: Notes\n  output audio: Audio\n  process {\n    emit notes |> delay-llama() |> reverb(size=30%)\n  }\n}\n";
        Assert.Empty(Codes(llama));
        Assert.Equal(["delay-llama1", "reverb1"], Graph(llama).Nodes.Select(n => n.Id));
    }

    private const string Nl = "\n";
    private const string Q = "\"";

    [Fact]
    public void A_component_with_one_instrument_can_run_live_the_server_renders_the_instrument_and_the_effects_follow()
    {
        AudioGraph g = Graph(Voice.Replace("reverb(size=room)", "delay(time=250ms) |> reverb(size=room)", StringComparison.Ordinal));
        Assert.True(g.LivePreview);
        Assert.Equal("piano1", g.LiveSource);
        Assert.Empty(g.NoLive);
        AudioGraph two = Graph("component koda.fx.two version 1.0.0 {" + Nl + "  id " + Q + "demo.koda.fx.two" + Q + Nl + "  input notes: Notes" + Nl + "  output audio: Audio" + Nl + "  process {" + Nl + "    emit mix(notes |> piano(), notes |> synth())" + Nl + "  }" + Nl + "}" + Nl);
        Assert.False(two.LivePreview);
        Assert.Contains("more than one instrument", two.NoLive);
        Assert.Null(two.LiveSource);
        Assert.Null(Graph(Echo).LiveSource);   // an audio component has no instrument: it loops the sound that feeds it
    }

    [Fact]
    public void An_instance_without_a_placement_plays_where_the_clip_it_reads_plays()
    {
        string song = Header + Voice + "midi-clip melody at 3 for 2 {\n  notes 1:0:1:60:100\n}\n\nkoda.fx.voice lead {\n  from melody.notes -> notes\n}\n";
        ExpandedSong expanded = ModuleExpander.Expand(song, Corpus.Spec, _ => null);
        Assert.Contains("piano lead__piano1 at 3 for 2 {", expanded.Text, StringComparison.Ordinal);
        // its own placement wins
        string own = song.Replace("koda.fx.voice lead {", "koda.fx.voice lead at 5 for 1 {", StringComparison.Ordinal);
        Assert.Contains("piano lead__piano1 at 5 for 1 {", ModuleExpander.Expand(own, Corpus.Spec, _ => null).Text, StringComparison.Ordinal);
    }
}
