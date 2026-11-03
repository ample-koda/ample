namespace Ample.Language.Tests;

/// <summary>The mini layout of a component (plan/components/ui): the ui block, the widget attributes, the checks and what the layout does not change (the semantic hash).</summary>
public sealed class ComponentUiTests
{
    private const string Header = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n";

    private static string Comp(string ui = "", string attributes = "", string preview = "") =>
        $"{preview}component koda.fx.dreamEcho version 1.0.0 {{\n  id \"demo.koda.fx.dream-echo\"\n  input audio: Audio\n  #[MinValue(0%), MaxValue(90%)]\n  {attributes}property feedback: Percent = 30%\n  property tone: Percent = 50%\n  property depth: Percent = 10%\n  property bypass: Bool = false\n  #[Options(\"soft\", \"hard\")]\n  property shape: String = \"soft\"\n  property name: String = \"echo\"\n  property note: Notes\n  output audio: Audio\n{ui}  process {{\n    emit audio\n      |> delay(time=250ms, feedback=feedback)\n  }}\n}}\n";

    private static ComponentDefinition Only(string text) => Assert.Single(Parser.Parse(Header + text, Corpus.Spec).Components);

    private static string[] Codes(string text) => [.. Parser.Parse(Header + text, Corpus.Spec).Diagnostics.Where(d => d.Code.StartsWith("AMP224", StringComparison.Ordinal)).Select(d => d.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    [Fact]
    public void A_ui_block_becomes_a_tree_with_rows_groups_the_card_and_the_preview()
    {
        string ui = "  ui {\n    row {\n      knob feedback size l label \"Echo\"\n      toggle bypass\n    }\n    group \"Voice\" {\n      choice shape\n      xy x=tone y=depth\n    }\n    card { knob feedback }\n    preview live\n  }\n";
        ComponentUi tree = Assert.IsType<ComponentUi>(Only(Comp(ui)).UiTree);
        Assert.Equal("live", tree.Preview);
        Assert.Equal(["row", "group"], tree.Panel.Select(n => n.Kind));
        ComponentUiNode row = tree.Panel[0];
        Assert.Equal(["knob", "toggle"], row.Children.Select(n => n.Kind));
        Assert.Equal(("feedback", "l", "Echo"), (row.Children[0].Property, row.Children[0].Size, row.Children[0].Label));
        Assert.Equal("Voice", tree.Panel[1].Label);
        Assert.Equal(("tone", "depth"), (tree.Panel[1].Children[1].Property, tree.Panel[1].Children[1].Second));
        Assert.Equal("feedback", Assert.Single(tree.Card).Property);
        Assert.Empty(Codes(Comp(ui)));
    }

    [Fact]
    public void Every_mistake_in_the_layout_has_its_own_code()
    {
        Assert.Contains("AMP2240", Codes(Comp("  ui {\n    dial feedback\n  }\n")));   // not a widget
        Assert.Contains("AMP2240", Codes(Comp("  ui {\n    row { knob feedback\n  }\n")));   // never closed
        Assert.Contains("AMP2241", Codes(Comp("  ui {\n    knob feedbak\n  }\n")));   // not a property
        Assert.Contains("AMP2241", Codes(Comp("  ui {\n    meter nothing\n  }\n")));   // not an output
        Assert.Contains("AMP2242", Codes(Comp("  ui {\n    knob name\n  }\n")));   // a knob on text
        Assert.Contains("AMP2242", Codes(Comp("  ui {\n    toggle feedback\n  }\n")));   // a toggle on a number
        Assert.Contains("AMP2243", Codes(Comp("  ui {\n    row { row { row { row { row { knob feedback } } } } }\n  }\n")));   // too deep
        Assert.Contains("AMP2244", Codes(Comp("  ui {\n    knob feedback\n    slider feedback\n  }\n")));   // twice
        Assert.DoesNotContain("AMP2244", Codes(Comp("  ui {\n    knob feedback\n    readout feedback\n  }\n")));
        Assert.Contains("AMP2245", Codes(Comp().Replace("output audio: Audio", "output audio: Audio", StringComparison.Ordinal).Replace("component koda.fx.dreamEcho", "#[LivePreview]\ncomponent koda.fx.dreamEcho", StringComparison.Ordinal).Replace("input audio: Audio", "input source: Notes", StringComparison.Ordinal).Replace("output audio: Audio", "output notes: Notes", StringComparison.Ordinal).Replace("emit audio\n      |> delay(time=250ms, feedback=feedback)", "emit source", StringComparison.Ordinal)));   // a Notes component cannot preview live
    }

    [Fact]
    public void The_widget_attribute_is_checked_like_a_widget_in_the_block()
    {
        Assert.Empty(Codes(Comp(attributes: "#[Widget(knob)]\n  ")));
        Assert.Contains("AMP2240", Codes(Comp(attributes: "#[Widget(dial)]\n  ")));
        Assert.Contains("AMP2242", Codes(Comp(attributes: "#[Widget(toggle)]\n  ")));   // a toggle on a Percent
    }

    [Fact]
    public void Without_a_block_the_layout_is_made_from_the_attributes_and_a_plain_field_otherwise()
    {
        string attributes = "#[Widget(knob), Group(\"Echo\")]\n  ";
        ComponentUi made = ComponentUiReader.Effective(Only(Comp(attributes: attributes)));
        ComponentUiNode group = Assert.Single(made.Panel, n => n.Kind == "group");
        Assert.Equal(("Echo", "knob"), (group.Label, Assert.Single(group.Children).Kind));
        Assert.Equal(["field", "field", "field", "field", "field", "field"], made.Panel.Where(n => n.Kind != "group").Select(n => n.Kind));   // bypass, shape, name, note: the plain fields (the note property is the fourth, see below)
    }

    [Fact]
    public void The_live_preview_attribute_asks_for_it_unless_the_block_says_off()
    {
        Assert.Equal("live", ComponentUiReader.Effective(Only(Comp(preview: "#[LivePreview]\n"))).Preview);
        Assert.Equal("off", ComponentUiReader.Effective(Only(Comp("  ui {\n    knob feedback\n    preview off\n  }\n", preview: "#[LivePreview]\n"))).Preview);
        Assert.Equal(string.Empty, ComponentUiReader.Effective(Only(Comp())).Preview);
    }

    [Fact]
    public void The_layout_is_presentation_it_changes_the_payload_hash_and_never_the_semantic_one()
    {
        ComponentDefinition plain = Only(Comp());
        ComponentDefinition laidOut = Only(Comp("  ui {\n    knob feedback\n  }\n"));
        ComponentDefinition widget = Only(Comp(attributes: "#[Widget(knob)]\n  "));
        Assert.Equal(ComponentHashing.Semantic(plain, []), ComponentHashing.Semantic(laidOut, []));
        Assert.Equal(ComponentHashing.Semantic(plain, []), ComponentHashing.Semantic(widget, []));
        Assert.NotEqual(ComponentHashing.Payload(plain, []), ComponentHashing.Payload(laidOut, []));
        Assert.NotEqual(ComponentHashing.Payload(plain, []), ComponentHashing.Payload(widget, []));
        // the range is not presentation: it constrains the value
        ComponentDefinition wider = Only(Comp().Replace("MaxValue(90%)", "MaxValue(95%)", StringComparison.Ordinal));
        Assert.NotEqual(ComponentHashing.Semantic(plain, []), ComponentHashing.Semantic(wider, []));
    }

    [Fact]
    public void The_editor_offers_the_widgets_the_properties_and_the_sizes_inside_the_block()
    {
        string text = Header + Comp("  ui {\n    knob \n  }\n");
        AmpleDocument doc = Parser.Parse(text, Corpus.Spec);
        string[] lines = text.Split('\n');
        int line = Array.FindIndex(lines, l => l.Trim() == "knob") + 1;
        IReadOnlyList<CompletionItem> properties = Queries.CompletionAt(doc, Corpus.Spec, line, lines[line - 1].Length + 1);
        Assert.Contains(properties, i => i.Label == "feedback");
        string start = Header + Comp("  ui {\n    \n  }\n");
        AmpleDocument doc2 = Parser.Parse(start, Corpus.Spec);
        string[] lines2 = start.Split('\n');
        int blank = Array.FindIndex(lines2, l => l.Trim().Length == 0 && l.Length == 4) + 1;
        IReadOnlyList<CompletionItem> words = Queries.CompletionAt(doc2, Corpus.Spec, blank, 5);
        Assert.Contains(words, i => i.Label == "knob" && i.Kind == "widget");
        Assert.Contains(words, i => i.Label == "row" && i.Kind == "container");
    }

    [Fact]
    public void The_control_of_a_property_comes_from_its_type_and_attributes()
    {
        ComponentDefinition d = Only(Comp());
        Dictionary<string, ComponentControl> controls = d.Properties.ToDictionary(p => p.Name, ComponentControl.Of, StringComparer.Ordinal);
        Assert.Equal(("number", "%", (double?)0, (double?)90), (controls["feedback"].Kind, controls["feedback"].Unit, controls["feedback"].Min, controls["feedback"].Max));
        Assert.Equal("toggle", controls["bypass"].Kind);
        Assert.Equal(("choice", 2), (controls["shape"].Kind, controls["shape"].Options.Count));
        Assert.Equal("text", controls["name"].Kind);
        Assert.Equal("notes", controls["note"].Kind);
    }

    [Fact]
    public void An_instance_can_set_a_switch_and_a_text_property_without_a_false_number_error()
    {
        string instance = "koda.fx.dreamEcho echo {\n  bypass true\n  shape \"hard\"\n  name \"room\"\n}\n";
        Assert.DoesNotContain(Parser.Parse(Header + Comp() + "\n" + instance, Corpus.Spec).Diagnostics, d => d.Code == "AMP2218");
        string wrong = "koda.fx.dreamEcho echo {\n  bypass maybe\n}\n";
        Assert.Contains(Parser.Parse(Header + Comp() + "\n" + wrong, Corpus.Spec).Diagnostics, d => d.Code == "AMP2218");
    }

    [Fact]
    public void The_shared_stable_note_names_the_component_not_the_helper_it_became()
    {
        string text = Header + "component koda.notes.octaveLift version 1.0.0 {\n  id \"demo.koda.notes.octave-lift\"\n  input source: Notes\n  output notes: Notes\n  process {\n    emit source |> shift(stable(\"lift\"))\n  }\n}\n";
        string[] messages = [.. Parser.Parse(text, Corpus.Spec).Diagnostics.Where(d => d.Code == "AMP2055").Select(d => d.Message)];
        Assert.DoesNotContain(messages, m => m.Contains("__c_", StringComparison.Ordinal));
    }

    [Fact]
    public void Connecting_to_an_instance_of_a_component_finds_its_template_in_the_spec_of_the_song()
    {
        string song = Header + "component koda.notes.twice version 1.0.0 {\n  id \"demo.koda.notes.twice\"\n  input source: Notes\n  output notes: Notes\n  process {\n    emit source\n  }\n}\n\nmidi-clip melody at 1 for 1 {\n  notes 1:0:1:60:100\n}\n\nkoda.notes.twice again at 2 for 1 {\n}\n";
        AmpleDocument doc = Parser.Parse(song, Corpus.Spec);
        EditResult result = Edits.Connect(doc, Corpus.Spec, "melody", "notes", "again", "source");
        Assert.True(result.Ok, result.Error);
        Assert.Contains(result.Edits, e => e.NewText.Contains("from melody.notes -> source", StringComparison.Ordinal));
    }

    [Fact]
    public void An_effect_can_be_put_after_an_audio_component_instance()
    {
        string echo = "component koda.fx.dreamEcho version 1.0.0 {\n  id \"demo.koda.fx.dream-echo\"\n  input audio: Audio\n  output audio: Audio\n  process {\n    emit audio |> delay(time=250ms)\n  }\n}\n\n";
        string song = Header + echo + "midi-clip melody at 1 for 1 {\n  notes 1:0:1:60:100\n}\n\npiano lead at 1 for 1 {\n  from melody.notes -> notes\n}\n\nkoda.fx.dreamEcho space {\n  from lead.audio -> audio\n}\n";
        string? text = Edits.InsertEffectAfter(song, Corpus.Spec, "space", "reverb", "room", [], out string? error);
        Assert.True(text is not null, error);
        AmpleDocument after = Parser.Parse(text, Corpus.Spec);
        BlockNode room = Assert.Single(after.Blocks, b => b.Name == "room");
        Assert.Contains(room.Froms, f => f.Block == "space");   // the new effect reads the instance
    }

    [Fact]
    public void A_widget_a_group_and_a_property_can_be_tinted_with_a_name_or_a_hex_text_and_anything_else_is_refused()
    {
        string ui = "  ui {\n    group \"Echo\" tint blue {\n      knob feedback tint red\n      slider tone tint \"#ff8800\"\n    }\n  }\n";
        ComponentUi tree = Assert.IsType<ComponentUi>(Only(Comp(ui)).UiTree);
        ComponentUiNode group = Assert.Single(tree.Panel);
        Assert.Equal(("Echo", "blue"), (group.Label, group.Tint));
        Assert.Equal(["red", "#ff8800"], group.Children.Select(n => n.Tint));
        Assert.Empty(Codes(Comp(ui)));
        Assert.Contains("AMP2246", Codes(Comp("  ui {\n    knob feedback tint reddish\n  }\n")));
        Assert.Contains("AMP2246", Codes(Comp("  ui {\n    group \"x\" tint nope {\n      knob feedback\n    }\n  }\n")));
        // the attribute is the shortcut when there is no block, and checked the same
        ComponentUiNode made = Assert.Single(ComponentUiReader.Effective(Only(Comp(attributes: "#[Tint(green)]\n  "))).Panel, n => n.Property == "feedback");
        Assert.Equal("green", made.Tint);
        Assert.Contains("AMP2246", Codes(Comp(attributes: "#[Tint(dull)]\n  ")));
        // a tint is presentation: the semantic hash does not change
        Assert.Equal(ComponentHashing.Semantic(Only(Comp()), []), ComponentHashing.Semantic(Only(Comp("  ui {\n    knob feedback tint red\n  }\n")), []));
    }

    [Fact]
    public void A_curve_needs_a_text_property()
    {
        Assert.Empty(Codes(Comp("  ui {\n    curve name\n  }\n")));
        Assert.Contains("AMP2242", Codes(Comp("  ui {\n    curve feedback\n  }\n")));   // a number is a knob, not a curve
    }

    [Fact]
    public void A_length_range_in_bars_is_checked_in_beats_and_a_loop_does_not_inherit_the_length_of_its_riff()
    {
        string arrange = "component koda.techno.arrange version 1.0.0 {\n  id \"demo.koda.techno.arrange\"\n  input p1: Notes { optional true }\n  #[MinValue(0bar), MaxValue(128bar)]\n  property s1: Duration = 0bar\n  output notes: Notes\n  process {\n    emit (connected(p1) ? p1 : emptyNotes()) |> shift(s1)\n  }\n}\n";
        string song = Header + arrange + "\nmidi-clip riff at 1 for 4 {\n  notes 1:0:1:60:100\n}\n\nkoda.techno.arrange plan at 1 for 16 {\n  s1 40bar\n  from riff.notes -> p1\n}\n";
        string[] bad = [.. Parser.Parse(song, Corpus.Spec).Diagnostics.Where(d => d.Code is "AMP2218" or "AMP2049").Select(d => d.Code + " " + d.Message)];
        Assert.Empty(bad);   // 40 bars is 160 beats, inside 0..512 beats; and the component makes its own length
        string tooFar = song.Replace("s1 40bar", "s1 200bar", StringComparison.Ordinal);
        Assert.Contains(Parser.Parse(tooFar, Corpus.Spec).Diagnostics, d => d.Code == "AMP2218");
    }
}
