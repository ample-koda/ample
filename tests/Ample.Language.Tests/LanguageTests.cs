using System.Diagnostics;

namespace Ample.Language.Tests;

public sealed class EditsAndQueriesTests
{
    private const string Song = "composition \"demo\" { ample 1  tempo 92  master-seed 424242 }\nmidi-clip chords {\n  chords Am F C G\n}\npiano piano pinned@2.0 {\n  from chords.notes -> notes\n}\nlayout chords 10 20\n";

    private static AmpleDocument Parse(string text) => Parser.Parse(text, Corpus.Spec);

    private static string Apply(string text, EditResult r)
    {
        Assert.True(r.Ok, r.Error);
        return Edits.Apply(text, r.Edits);
    }

    [Fact]
    public void Set_layout_changes_an_existing_line_or_adds_one()
    {
        string moved = Apply(Song, Edits.SetLayout(Parse(Song), "chords", 40, -60));
        Assert.Contains("layout chords 40 -60", moved, StringComparison.Ordinal);
        Assert.DoesNotContain("layout chords 10 20", moved, StringComparison.Ordinal);
        string added = Apply(Song, Edits.SetLayout(Parse(Song), "piano", 300, 80));
        Assert.Contains("layout piano 300 80", added, StringComparison.Ordinal);
        Assert.Equal((300, 80), Parse(added).Layout["piano"]);
        Assert.False(Edits.SetLayout(Parse(Song), "nope", 1, 1).Ok);
        Assert.DoesNotContain(Parse(Apply(Song, Edits.SetLayout(Parse(Song), "chords", 10, 20))).Diagnostics, d => d.Severity == Severity.Error);
    }

    [Fact]
    public void Set_parts_updates_and_validates_arrangement_placements()
    {
        string placed = Apply(Song, Edits.SetParts(Parse(Song), "chords", [new Part(3, 4, "intro"), new Part(9, 2, "")]));
        Assert.Contains("at 3 for 4 as intro, 9 for 2 {", placed, StringComparison.Ordinal);
        Assert.Equal(new[] { new Part(3, 4, "intro"), new Part(9, 2, "") }, Parse(placed).Blocks.First(b => b.Name == "chords").Parts);
        Assert.DoesNotContain(Parse(placed).Diagnostics, d => d.Severity == Severity.Error);
        Assert.False(Edits.SetParts(Parse(Song), "missing", [new Part(1, 1, "")]).Ok);
        Assert.False(Edits.SetParts(Parse(Song), "chords", [new Part(0, 1, "")]).Ok);
        Assert.False(Edits.SetParts(Parse(Song), "chords", [new Part(256, 2, "")]).Ok);
    }

    [Fact]
    public void Set_freeze_writes_and_removes_the_freeze_words_and_keeps_flags()
    {
        string withBake = Apply(Song, Edits.AddFlag(Parse(Song), "piano", "bake"));
        string seeds = Apply(withBake, Edits.SetFreeze(Parse(withBake), "piano", "seeds"));
        Assert.Equal("seeds", Parse(seeds).Blocks.First(b => b.Name == "piano").Freeze);
        Assert.Contains("bake", Parse(seeds).Blocks.First(b => b.Name == "piano").Flags);
        string plan = Apply(seeds, Edits.SetFreeze(Parse(seeds), "piano", "plan"));
        Assert.Equal("plan", Parse(plan).Blocks.First(b => b.Name == "piano").Freeze);
        string free = Apply(plan, Edits.SetFreeze(Parse(plan), "piano", "free"));
        Assert.Null(Parse(free).Blocks.First(b => b.Name == "piano").Freeze);
        Assert.Empty(Edits.SetFreeze(Parse(free), "piano", "free").Edits);
        Assert.False(Edits.SetFreeze(Parse(Song), "piano", "frozen").Ok);
        Assert.False(Edits.SetFreeze(Parse(Song), "nope", "plan").Ok);
    }

    [Fact]
    public void Remove_flag_takes_the_word_out_of_the_header_only()
    {
        string withFlag = Apply(Song, Edits.AddFlag(Parse(Song), "piano", "lock"));
        Assert.Contains("lock", Parse(withFlag).Blocks.First(b => b.Name == "piano").Flags);
        string withDisabled = Apply(withFlag, Edits.AddFlag(Parse(withFlag), "piano", "disabled"));
        Assert.Contains("disabled", Parse(withDisabled).Blocks.First(b => b.Name == "piano").Flags);
        string without = Apply(withDisabled, Edits.RemoveFlag(Parse(withDisabled), "piano", "disabled"));
        without = Apply(without, Edits.RemoveFlag(Parse(without), "piano", "lock"));
        Assert.Equal(Song, without);
        Assert.Empty(Edits.RemoveFlag(Parse(Song), "piano", "bake").Edits);
        Assert.False(Edits.RemoveFlag(Parse(Song), "nope", "bake").Ok);
    }

    [Fact]
    public void Set_parameter_replaces_an_existing_value_and_adds_a_missing_one()
    {
        AmpleDocument doc = Parse(Song);
        string changed = Apply(Song, Edits.SetParameter(doc, "chords", "chords", "C G Am F"));
        Assert.Contains("  chords C G Am F\n", changed, StringComparison.Ordinal);
        Assert.DoesNotContain(Parse(changed).Diagnostics, d => d.Severity == Severity.Error);

        string added = Apply(Song, Edits.SetParameter(doc, "piano", "velocity", "natural"));
        Assert.Contains("  velocity natural\n", added, StringComparison.Ordinal);
        Assert.DoesNotContain(Parse(added).Diagnostics, d => d.Severity == Severity.Error);
    }

    [Fact]
    public void Setting_an_empty_value_removes_the_parameter_instead_of_breaking_the_block()
    {
        string cleared = Apply(Song, Edits.SetParameter(Parse(Song), "chords", "chords", ""));
        Assert.DoesNotContain("  chords", cleared, StringComparison.Ordinal);
        Assert.DoesNotContain(Parse(cleared).Diagnostics, d => d.Severity == Severity.Error);
    }

    [Fact]
    public void Remove_parameter_clears_a_field_without_leaving_an_empty_value()
    {
        string removed = Apply(Song, Edits.RemoveParameter(Parse(Song), "chords", "chords"));
        Assert.DoesNotContain("  chords ", removed, StringComparison.Ordinal);
        Assert.Empty(Edits.RemoveParameter(Parse(Song), "chords", "missing").Edits);
        Assert.False(Edits.RemoveParameter(Parse(Song), "missing", "file").Ok);
    }

    [Fact]
    public void Add_and_remove_blocks_keep_the_song_valid()
    {
        AmpleDocument doc = Parse(Song);
        string added = Apply(Song, Edits.AddBlock(doc, Corpus.Spec, "drums", "beats"));
        Assert.DoesNotContain(Parse(added).Diagnostics, d => d.Severity == Severity.Error);
        Assert.Contains(Parse(added).Blocks, b => b.Name == "beats");

        string removed = Apply(added, Edits.RemoveBlock(Parse(added), "beats"));
        Assert.Equal(Song, removed);
        Assert.False(Edits.RemoveBlock(doc, "chords").Ok);
        Assert.False(Edits.AddBlock(doc, Corpus.Spec, "drumz", "x").Ok);
        Assert.False(Edits.AddBlock(doc, Corpus.Spec, "drums", "piano").Ok);
    }

    [Fact]
    public void Voice_music_scene_templates_expose_their_ports_and_editor_metadata()
    {
        TemplateSpec voice = Corpus.Spec.Templates["ai-voice"];
        TemplateSpec music = Corpus.Spec.Templates["ace-music"];
        TemplateSpec scene = Corpus.Spec.Templates["mood-scene"];
        Assert.True(voice.Inputs.Single(p => p.Name == "lyrics").Required);
        Assert.False(music.Inputs.Single(p => p.Name == "lyrics").Required);
        Assert.Equal("audio", music.Outputs.Single(o => o.Name == "audio").Type);
        Assert.Equal("data", scene.Outputs.Single().Type);
        Assert.True(voice.ParameterSpecs["voice"].ModalEditor);
        Assert.True(voice.ParameterSpecs["voice"].Multiline);
        Assert.True(Corpus.Spec.Templates["drums"].ParameterSpecs["pattern"].ModalEditor);
        Assert.DoesNotContain("  text ", Apply(Song, Edits.AddBlock(Parse(Song), Corpus.Spec, "lyrics", "words")), StringComparison.Ordinal);
    }

    [Fact]
    public void Connect_checks_ports_types_inputs_and_cycles_and_disconnect_undoes_it()
    {
        string text = Song + "bass bass {\n}\n";
        AmpleDocument doc = Parse(text);
        string connected = Apply(text, Edits.Connect(doc, Corpus.Spec, "chords", "notes", "bass", "notes"));
        Assert.Contains("from chords.notes -> notes", connected, StringComparison.Ordinal);
        Assert.DoesNotContain(Parse(connected).Diagnostics, d => d.Severity == Severity.Error);
        Assert.Equal(text, Apply(connected, Edits.Disconnect(Parse(connected), "bass", "notes")));

        Assert.False(Edits.Connect(doc, Corpus.Spec, "chords", "note", "bass", "notes").Ok);
        Assert.False(Edits.Connect(doc, Corpus.Spec, "bass", "audio", "piano", "notes").Ok);
        Assert.False(Edits.Connect(doc, Corpus.Spec, "chords", "notes", "piano", "notes").Ok);
        Assert.False(Edits.Connect(doc, Corpus.Spec, "piano", "audio", "chords", "notes").Ok);
        string groups = Song + "group a {\n}\ngroup b {\n}\n";
        string ab = Apply(groups, Edits.Connect(Parse(groups), Corpus.Spec, "a", "out", "b", "in"));
        Assert.Contains("cycle", Edits.Connect(Parse(ab), Corpus.Spec, "b", "out", "a", "in").Error ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Rename_updates_the_header_every_reference_and_the_layout_line()
    {
        AmpleDocument doc = Parse(Song);
        string renamed = Apply(Song, Edits.RenameBlock(doc, "chords", "main chords"));
        Assert.Contains("midi-clip \"main chords\" {", renamed, StringComparison.Ordinal);
        Assert.Contains("from \"main chords\".notes -> notes", renamed, StringComparison.Ordinal);
        Assert.Contains("layout \"main chords\" 10 20", renamed, StringComparison.Ordinal);
        AmpleDocument after = Parse(renamed);
        Assert.DoesNotContain(after.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Equal((10, 20), after.Layout["main chords"]);
        Assert.False(Edits.RenameBlock(doc, "chords", "piano").Ok);
    }

    [Fact]
    public void Symbols_definition_and_references_find_the_blocks()
    {
        AmpleDocument doc = Parse(Song);
        Assert.Equal(["chords", "piano"], Queries.DocumentSymbols(doc).Select(s => s.Name));
        Assert.Equal(new Location(2, 11, 17), Queries.FindDefinition(doc, 6, 12));
        Assert.Equal(2, Queries.FindReferences(doc, 2, 12).Count);
        Assert.Null(Queries.FindDefinition(doc, 3, 4));
    }

    [Fact]
    public void Hover_and_completion_use_the_template_data()
    {
        AmpleDocument doc = Parse(Song + "drums d {\n  \n  from \n}\n");
        Assert.Contains("Textual MIDI", Queries.HoverAt(doc, Corpus.Spec, 2, 2) ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("parameter of midi-clip", Queries.HoverAt(doc, Corpus.Spec, 3, 4) ?? string.Empty, StringComparison.Ordinal);

        int blank = doc.Lines.ToList().FindIndex(l => l == "  ") + 1;
        Assert.Contains(Queries.CompletionAt(doc, Corpus.Spec, blank, 3), c => c.Label == "kit" && c.Kind == "parameter");
        Assert.Contains(Queries.CompletionAt(doc, Corpus.Spec, blank + 1, 8), c => c.Label == "chords" && c.Kind == "block");
        Assert.Contains(Queries.CompletionAt(doc, Corpus.Spec, 1, 1).Take(30), c => c.Label == "midi-clip" || c.Label == "drums");
    }

    [Fact]
    public void Completion_knows_the_header_tail_view_lines_and_parameter_values()
    {
        AmpleDocument doc = Parse(Song + "drums d \n# view \n# view pin \nmaster m {\n  target \n}\n");
        int At(string startsWith) => doc.Lines.ToList().FindIndex(l => l.StartsWith(startsWith, StringComparison.Ordinal)) + 1;
        Assert.Contains(Queries.CompletionAt(doc, Corpus.Spec, At("drums d"), 20), c => c.Label == "at" && c.Kind == "keyword");
        Assert.Contains(Queries.CompletionAt(doc, Corpus.Spec, At("# view "), 8), c => c.Label is "group" or "size" or "pin");
        Assert.Contains(Queries.CompletionAt(doc, Corpus.Spec, At("# view pin"), 12), c => c.Label == "chords" && c.Kind == "block");
        Assert.Contains(Queries.CompletionAt(doc, Corpus.Spec, At("  target"), 10), c => c.Label == "\"-14 LUFS\"" && c.Kind == "value");
    }
}

public sealed class RobustnessTests
{
    private static readonly string[] CorpusFolders = ["valid", "invalid", "recovery", "format"];

    private static IEnumerable<string> CorpusTexts() =>
        CorpusFolders.SelectMany(f => Directory.GetFiles(Path.Combine(Corpus.Root, "spec-tests", f), "*.amplet")).OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllText);

    private static string Mutate(string text, Random random)
    {
        const string noise = "{}\"#\n -> .@,\t\r0\\éx😀";
        string[] lines = text.Split('\n');
        switch (random.Next(6))
        {
            case 0:
                return text[..random.Next(text.Length + 1)];
            case 1 when text.Length > 0:
                int a = random.Next(text.Length);
                return text.Remove(a, Math.Min(random.Next(1, 8), text.Length - a));
            case 2:
                return text.Insert(random.Next(text.Length + 1), noise[random.Next(noise.Length)].ToString());
            case 3 when lines.Length > 1:
                return string.Join('\n', lines.Where((_, i) => i != random.Next(lines.Length)));
            case 4 when lines.Length > 1:
                int d = random.Next(lines.Length);
                return string.Join('\n', lines.Take(d + 1).Append(lines[d]).Concat(lines.Skip(d + 1)));
            default:
                return text.Replace(noise[random.Next(noise.Length)].ToString(), noise[random.Next(noise.Length)].ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_parser_never_throws_the_tree_text_equals_the_input_and_ranges_are_valid()
    {
        Random random = new(20261002);
        List<string> texts = [.. CorpusTexts()];
        for (int i = 0; i < 4000; i++)
        {
            string text = texts[random.Next(texts.Count)];
            for (int m = random.Next(1, 4); m > 0; m--)
            {
                text = Mutate(text, random);
            }

            AmpleDocument doc = Parser.Parse(text, Corpus.Spec);
            Assert.Equal(text, doc.ToText());
            foreach (Diagnostic d in doc.Diagnostics)
            {
                Assert.True(d.Line >= 1 && d.Col >= 1 && d.EndCol > d.Col, $"bad range {d.Code} {d.Line}:{d.Col}-{d.EndCol}");
            }

            Assert.Equal(doc.Diagnostics.OrderBy(d => d.Line).ThenBy(d => d.Col).Select(d => d.Code), doc.Diagnostics.Select(d => d.Code));
        }
    }

    [Fact]
    public void The_formatter_never_throws_and_is_idempotent_on_mutated_input()
    {
        Random random = new(7);
        List<string> texts = [.. CorpusTexts()];
        for (int i = 0; i < 4000; i++)
        {
            string text = texts[random.Next(texts.Count)];
            for (int m = random.Next(0, 4); m > 0; m--)
            {
                text = Mutate(text, random);
            }

            string once = Formatter.Format(text, Corpus.Spec);
            string twice = Formatter.Format(once, Corpus.Spec);
            Assert.True(once == twice, $"not idempotent for input:\n{text}\n--- once:\n{once}\n--- twice:\n{twice}");
        }
    }

    [Fact]
    public void Formatting_valid_programs_keeps_their_meaning()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(Corpus.Root, "spec-tests", "valid"), "*.amplet"))
        {
            AmpleDocument before = Parser.Parse(File.ReadAllText(file), Corpus.Spec);
            AmpleDocument after = Parser.Parse(Formatter.Format(File.ReadAllText(file), Corpus.Spec), Corpus.Spec);
            Assert.Equal(before.Blocks.Select(b => $"{b.Type} {b.Name} {b.Class}@{b.Version} {string.Join(',', b.Flags)} {string.Join(',', b.Parts)} {string.Join(',', b.Parameters.Select(p => p.Name + "=" + p.Value))} {string.Join(',', b.Froms.Select(f => f.Block + "." + f.Output + ">" + f.Input))}"), after.Blocks.Select(b => $"{b.Type} {b.Name} {b.Class}@{b.Version} {string.Join(',', b.Flags)} {string.Join(',', b.Parts)} {string.Join(',', b.Parameters.Select(p => p.Name + "=" + p.Value))} {string.Join(',', b.Froms.Select(f => f.Block + "." + f.Output + ">" + f.Input))}"));
            Assert.Equal(before.Diagnostics.Select(d => d.Code), after.Diagnostics.Select(d => d.Code));
        }
    }

    [Fact]
    public void A_five_thousand_line_song_parses_quickly()
    {
        string unit = File.ReadAllText(Path.Combine(Corpus.Root, "spec-tests", "valid", "15-full-song.amplet"));
        List<string> parts = [unit.Split('\n', 2)[0], "composition \"big\" { ample 1  tempo 100  master-seed 1 }"];
        for (int i = 0; i < 400; i++)
        {
            parts.Add($"drums d{i} at 1 for 16 {{\n  kit kit/tape\n  pattern beat-A\n  bars 16\n  from p{i}.audio -> trigger\n}}\npiano p{i} pinned@2.0 {{\n  set felt-piano\n  velocity natural\n}}\nmidi-clip c{i} {{\n  chords Am F C G\n}}\nbass b{i} {{\n  from c{i}.notes -> notes\n}}\n");
        }

        string text = string.Join('\n', parts.Skip(1));
        Assert.True(text.Count(c => c == '\n') > 5000);
        Parser.Parse(text, Corpus.Spec);
        Stopwatch sw = Stopwatch.StartNew();
        AmpleDocument doc = Parser.Parse(text, Corpus.Spec);
        sw.Stop();
        Assert.NotEmpty(doc.Blocks);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"parse took {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"parse+analyse of {doc.Lines.Count} lines / {doc.Blocks.Count} blocks: {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void A_number_outside_the_range_of_its_parameter_is_warned_about_and_out_names_the_sound_of_any_block()
    {
        const string Song = """
            composition "t" { ample 1  tempo 120  master-seed 1 }
            import-audio a {
              file x.wav
            }
            volume v {
              gain 54
              from a.out -> in
            }
            volume w {
              gain auto(1:-70, 5:0)
              from v.out -> in
            }
            master master {
              from w.out -> mix
            }
            """;
        AmpleDocument doc = Parser.Parse(Song, Corpus.Spec);
        Assert.Equal(2, doc.Diagnostics.Count(d => d.Code == "AMP2021"));
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == "AMP2005");   // import-audio has no port called out: its audio is read as out
        AmpleDocument fine = Parser.Parse("composition \"t\" { ample 1  tempo 120  master-seed 1 }\nvolume v {\n  gain 48\n}\n", Corpus.Spec);
        Assert.DoesNotContain(fine.Diagnostics, d => d.Code == "AMP2021");
    }

    [Fact]
    public void Ports_are_written_by_the_stream_they_carry_and_the_older_names_are_an_error()
    {
        const string Song = """
            composition "t" { ample 1  tempo 120  master-seed 1 }
            import-audio a {
              file x.wav
            }
            import-audio b {
              file y.wav
            }
            volume v {
              from a.audio -> audio
            }
            mixer m {
              from v.audio -> audio1
              from b.audio -> in2
            }
            master master {
              from m.audio -> mix
            }
            """;
        AmpleDocument doc = Parser.Parse(Song, Corpus.Spec);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error && d.Code != "L010");
        Assert.Equal(2, doc.Diagnostics.Count(d => d.Code == "L010" && d.Severity == Severity.Error));   // in2 and mix: v1 only reads the stream names (ample migrate renames them)
        BlockNode mixer = doc.Blocks.Single(x => x.Name == "m");
        Assert.Equal(["in1", "in2"], mixer.Froms.Select(f => f.Input).ToArray());   // the model carries the internal names
        Assert.Equal("mix", doc.Blocks.Single(x => x.Name == "master").Froms.Single().Output);
        Assert.Equal("audio", PortNames.Public(new PortSpec("in3", "audio", false), [new PortSpec("in3", "audio", false)], false) is "audio3" ? "audio" : "wrong");
    }

    [Fact]
    public void An_effect_can_be_put_after_a_block_and_everything_that_read_the_block_reads_the_effect()
    {
        const string Song = """
            composition "t" { ample 1  tempo 120  master-seed 1 }
            import-audio a {
              file x.wav
            }
            volume v {
              from a.audio -> audio
            }
            master master {
              from v.audio -> audio
            }
            """;
        string? result = Edits.InsertEffectAfter(Song, Corpus.Spec, "v", "normalize", "v-normalize", [("peak", "-3")], out string? error);
        Assert.Null(error);
        Assert.NotNull(result);
        AmpleDocument doc = Parser.Parse(result!, Corpus.Spec);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        BlockNode normalize = doc.Blocks.Single(b => b.Name == "v-normalize");
        Assert.Equal("v", normalize.Froms.Single().Block);
        Assert.Equal("v-normalize", doc.Blocks.Single(b => b.Type == "master").Froms.Single().Block);
        Assert.Contains(normalize.Parameters, p => p.Name == "peak" && p.Value == "-3");
        Assert.Null(Edits.InsertEffectAfter(Song, Corpus.Spec, "nope", "normalize", "n", [], out string? missing));
        Assert.NotNull(missing);
    }

    [Fact]
    public void Master_and_block_seeds_accept_full_unsigned_64_bit_values()
    {
        const string source = "composition \"wide-seeds\" { ample 1  tempo 100 master-seed 18446744073709551615 }\nace-music song {\n  lm_seed 460973420405530378\n  seed 5530072552843900951\n}\n";
        AmpleDocument doc = Parser.Parse(source, Corpus.Spec);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Equal(ulong.MaxValue, doc.Project!.MasterSeed);
        Assert.Contains(doc.Blocks.Single().Parameters, p => p.Name == "lm_seed" && p.Value == "460973420405530378");
        Assert.Contains(doc.Blocks.Single().Parameters, p => p.Name == "seed" && p.Value == "5530072552843900951");

        AmpleDocument badBlockSeed = Parser.Parse(source.Replace("seed 5530072552843900951", "seed 18446744073709551616", StringComparison.Ordinal), Corpus.Spec);
        Assert.Contains(badBlockSeed.Diagnostics, d => d.Code == "AMP2011");
        AmpleDocument badMasterSeed = Parser.Parse(source.Replace("master-seed 18446744073709551615", "master-seed 18446744073709551616", StringComparison.Ordinal), Corpus.Spec);
        Assert.Contains(badMasterSeed.Diagnostics, d => d.Code == "AMP1009");
    }
}
