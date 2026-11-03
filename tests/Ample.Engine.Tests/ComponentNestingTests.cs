using Ample.Engine.Modules;
using Ample.Language;

namespace Ample.Core.Tests;

/// <summary>A Notes component that calls another Notes component, and what happens when components call each other in a circle.</summary>
[Trait("Category", "Modules")]
public sealed class ComponentNestingTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-nesting-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
            // a locked file on Windows must not fail the run
        }
    }

    private const string Up = """
        component koda.notes.up version 1.0.0 current {
          id "demo.koda.test.up"
          input source: Notes
          #[MinValue(-12), MaxValue(12)]
          property semitones: Int = 2
          output notes: Notes
          process {
            emit source |> transpose(semitones)
          }
        }
        """;

    private static string Twice(string call) => $$"""
        import "components/up.amplec"

        component koda.notes.twice version 1.0.0 current {
          id "demo.koda.test.twice"
          input source: Notes
          output notes: Notes
          process {
            emit source |> merge({{call}})
          }
        }
        """;

    private const string Song = """
        composition "nest" { ample 1  tempo 100  master-seed 1 }
        import "components/twice.amplec"

        midi-clip src at 1 for 1 {
          notes 1:0:1:60:100
        }

        koda.notes.twice both at 1 for 1 {
          from src.notes -> source
        }
        """;

    private AmpleDocument Expand(string call)
    {
        Directory.CreateDirectory(Path.Combine(folder, "components"));
        File.WriteAllText(Path.Combine(folder, "components", "up.amplec"), Up);
        File.WriteAllText(Path.Combine(folder, "components", "twice.amplec"), Twice(call));
        using ModuleService service = new(folder, (Ample.Storage.Library.Library?)null);
        return service.ParseExpanded(Song, EngineRig.Spec);
    }

    private static string[] Notes(AmpleDocument doc) => doc.Blocks.First(b => b.Name == "both").Parameters.First(p => p.Name == "notes").Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void A_notes_component_calls_another_one_with_named_arguments()
    {
        AmpleDocument doc = Expand("koda.notes.up(source, semitones=7)");
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        string[] notes = Notes(doc);
        Assert.Equal(2, notes.Length);
        Assert.Contains(notes, n => n.Contains(":60:", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.Contains(":67:", StringComparison.Ordinal));
    }

    [Fact]
    public void What_is_left_out_of_a_call_takes_the_default_and_the_input_can_be_named()
    {
        AmpleDocument doc = Expand("koda.notes.up(source=source)");
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Contains(Notes(doc), n => n.Contains(":62:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_call_with_a_name_the_component_does_not_have_is_AMP2248()
    {
        AmpleDocument doc = Expand("koda.notes.up(source, octaves=1)");
        Assert.Contains(doc.Diagnostics, d => d.Code == "AMP2248" && d.Message.Contains("octaves", StringComparison.Ordinal));
    }

    [Fact]
    public void Changing_the_called_component_changes_the_hash_of_the_caller()
    {
        Expand("koda.notes.up(source, semitones=7)");
        using ModuleService service = new(folder, (Ample.Storage.Library.Library?)null);
        string before = service.Expand(Song, EngineRig.Spec).Components!.Single(u => u.Name == "koda.notes.twice").Semantic;
        File.WriteAllText(Path.Combine(folder, "components", "up.amplec"), Up.Replace("transpose(semitones)", "transpose(semitones + 1)", StringComparison.Ordinal));
        using ModuleService again = new(folder, (Ample.Storage.Library.Library?)null);
        string after = again.Expand(Song, EngineRig.Spec).Components!.Single(u => u.Name == "koda.notes.twice").Semantic;
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void A_call_can_be_a_pipe_step_and_the_notes_before_the_pipe_are_the_first_input()
    {
        AmpleDocument doc = Expand("source |> koda.notes.up(semitones=7)");
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        string[] notes = Notes(doc);
        Assert.Equal(2, notes.Length);
        Assert.Contains(notes, n => n.Contains(":67:", StringComparison.Ordinal));
        AmpleDocument chained = Expand("source\n      |> koda.notes.up(semitones=7)\n      |> koda.notes.up(semitones=5)");
        Assert.DoesNotContain(chained.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Contains(Notes(chained), n => n.Contains(":72:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_that_imports_with_an_alias_calls_the_component_by_the_alias()
    {
        Directory.CreateDirectory(Path.Combine(folder, "components"));
        File.WriteAllText(Path.Combine(folder, "components", "up.amplec"), Up);
        File.WriteAllText(Path.Combine(folder, "components", "twice.amplec"), Twice("p.notes.up(source, semitones=7)").Replace("import \"components/up.amplec\"", "import \"components/up.amplec\" as p", StringComparison.Ordinal));
        using ModuleService service = new(folder, (Ample.Storage.Library.Library?)null);
        AmpleDocument doc = service.ParseExpanded(Song, EngineRig.Spec);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Contains(Notes(doc), n => n.Contains(":67:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_component_written_in_the_song_calls_an_imported_component_by_the_alias_of_the_song()
    {
        Directory.CreateDirectory(Path.Combine(folder, "components"));
        File.WriteAllText(Path.Combine(folder, "components", "up.amplec"), Up);
        using ModuleService service = new(folder, (Ample.Storage.Library.Library?)null);
        AmpleDocument doc = service.ParseExpanded("""
            composition "inline" { ample 1  tempo 100  master-seed 1 }
            import "components/up.amplec" as p

            component koda.notes.twice version 1.0.0 current {
              id "demo.koda.test.twice-inline"
              input source: Notes
              output notes: Notes
              process {
                emit source |> merge(source |> p.notes.up(semitones=7))
              }
            }

            midi-clip src at 1 for 1 {
              notes 1:0:1:60:100
            }

            koda.notes.twice both at 1 for 1 {
              from src.notes -> source
            }
            """, EngineRig.Spec);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        string[] notes = Notes(doc);
        Assert.Equal(2, notes.Length);
        Assert.Contains(notes, n => n.Contains(":67:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_component_file_that_calls_an_imported_component_checks_without_errors_in_the_editor()
    {
        Directory.CreateDirectory(Path.Combine(folder, "components"));
        File.WriteAllText(Path.Combine(folder, "components", "up.amplec"), Up);
        AmpleDocument doc = Parser.Parse(Twice("koda.notes.up(source, semitones=7)"), EngineRig.Spec, new ParseOptions { RequireProjectLine = false, ReadFile = target => { string full = Path.GetFullPath(Path.Combine(folder, target)); return File.Exists(full) ? File.ReadAllText(full) : null; } });
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        AmpleDocument wrong = Parser.Parse(Twice("koda.notes.up(source, octaves=1)"), EngineRig.Spec, new ParseOptions { RequireProjectLine = false, ReadFile = target => { string full = Path.GetFullPath(Path.Combine(folder, target)); return File.Exists(full) ? File.ReadAllText(full) : null; } });
        Assert.Contains(wrong.Diagnostics, d => d.Code == "AMP2248");
    }

    [Fact]
    public void A_bracket_that_never_closes_in_a_comment_does_not_hide_the_calls_after_it()
    {
        Directory.CreateDirectory(Path.Combine(folder, "components"));
        File.WriteAllText(Path.Combine(folder, "components", "up.amplec"), Up);
        File.WriteAllText(Path.Combine(folder, "components", "twice.amplec"), Twice("koda.notes.up(source, semitones=7)").Replace("    emit source", "    # koda.notes.up( is a call that is not closed here, and \"koda.notes.up(1)\" is a text\n    emit source", StringComparison.Ordinal));
        using ModuleService service = new(folder, (Ample.Storage.Library.Library?)null);
        AmpleDocument doc = service.ParseExpanded(Song, EngineRig.Spec);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Contains(Notes(doc), n => n.Contains(":67:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_call_of_a_component_that_is_not_in_sight_says_so()
    {
        AmpleDocument doc = Expand("koda.notes.nope(source)");
        Assert.Contains(doc.Diagnostics, d => d.Code == "AMP2248" && d.Message.Contains("koda.notes.nope", StringComparison.Ordinal));
    }

    private const string UpTwoVersions = """
        component koda.notes.up version 1.0.0 {
          id "demo.koda.test.up"
          input source: Notes
          #[MinValue(-12), MaxValue(12)]
          property semitones: Int = 2
          output notes: Notes
          process {
            emit source |> transpose(semitones)
          }
        }

        component koda.notes.up version 2.0.0 current {
          id "demo.koda.test.up"
          input source: Notes
          #[MinValue(-12), MaxValue(12)]
          property semitones: Int = 2
          output notes: Notes
          process {
            emit source |> transpose(semitones + 1)
          }
        }
        """;

    private AmpleDocument ExpandTwoVersions(string call)
    {
        Directory.CreateDirectory(Path.Combine(folder, "components"));
        File.WriteAllText(Path.Combine(folder, "components", "up.amplec"), UpTwoVersions);
        File.WriteAllText(Path.Combine(folder, "components", "twice.amplec"), Twice(call));
        using ModuleService service = new(folder, (Ample.Storage.Library.Library?)null);
        return service.ParseExpanded(Song, EngineRig.Spec);
    }

    [Fact]
    public void A_call_can_pin_a_version_and_an_unpinned_call_takes_the_default()
    {
        AmpleDocument old = ExpandTwoVersions("koda.notes.up@1.0.0(source, semitones=7)");
        Assert.DoesNotContain(old.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Contains(Notes(old), n => n.Contains(":67:", StringComparison.Ordinal));
        AmpleDocument current = ExpandTwoVersions("koda.notes.up@2.0.0(source, semitones=7)");
        Assert.True(Notes(current).Any(n => n.Contains(":68:", StringComparison.Ordinal)), "pin 2.0.0: " + string.Join(" ", Notes(current)));
        AmpleDocument unpinned = ExpandTwoVersions("koda.notes.up(source, semitones=7)");
        Assert.True(Notes(unpinned).Any(n => n.Contains(":68:", StringComparison.Ordinal)), "unpinned: " + string.Join(" ", Notes(unpinned)));
        AmpleDocument range = ExpandTwoVersions("koda.notes.up@^1.0(source, semitones=7)");
        Assert.True(Notes(range).Any(n => n.Contains(":67:", StringComparison.Ordinal)), "range ^1.0 stays in 1.x: " + string.Join(" ", Notes(range)));
    }

    [Fact]
    public void A_pin_that_no_version_matches_is_AMP2248_and_an_unpinned_call_of_a_family_with_more_versions_is_said_in_AMP2249()
    {
        AmpleDocument missing = ExpandTwoVersions("koda.notes.up@3.0.0(source)");
        Assert.Contains(missing.Diagnostics, d => d.Code == "AMP2248" && d.Message.Contains("No version", StringComparison.Ordinal));
        Directory.CreateDirectory(Path.Combine(folder, "components"));
        File.WriteAllText(Path.Combine(folder, "components", "up.amplec"), UpTwoVersions);
        AmpleDocument editor = Parser.Parse(Twice("koda.notes.up(source, semitones=7)"), EngineRig.Spec, new ParseOptions { RequireProjectLine = false, ReadFile = target => { string full = Path.GetFullPath(Path.Combine(folder, target)); return File.Exists(full) ? File.ReadAllText(full) : null; } });
        Assert.Contains(editor.Diagnostics, d => d.Code == "AMP2249" && d.Severity == Severity.Info);
        AmpleDocument pinned = Parser.Parse(Twice("koda.notes.up@2.0.0(source, semitones=7)"), EngineRig.Spec, new ParseOptions { RequireProjectLine = false, ReadFile = target => { string full = Path.GetFullPath(Path.Combine(folder, target)); return File.Exists(full) ? File.ReadAllText(full) : null; } });
        Assert.DoesNotContain(pinned.Diagnostics, d => d.Code == "AMP2249");
    }

    [Fact]
    public void A_notes_call_before_the_instrument_of_a_mixed_component_makes_a_clip_the_instrument_reads()
    {
        Directory.CreateDirectory(Path.Combine(folder, "components"));
        File.WriteAllText(Path.Combine(folder, "components", "up.amplec"), Up);
        File.WriteAllText(Path.Combine(folder, "components", "keys.amplec"), """
            import "components/up.amplec"

            component koda.fx.upKeys version 1.0.0 current {
              id "demo.koda.test.up-keys"
              input notes: Notes
              output audio: Audio
              process {
                emit notes |> koda.notes.up(semitones=12) |> synth(wave="saw")
              }
            }
            """);
        using ModuleService service = new(folder, (Ample.Storage.Library.Library?)null);
        AmpleDocument doc = service.ParseExpanded("""
            composition "mixed" { ample 1  tempo 100  master-seed 1 }
            import "components/keys.amplec"

            midi-clip src at 1 for 1 {
              notes 1:0:1:60:100
            }

            koda.fx.upKeys voice at 1 for 1 {
              from src.notes -> notes
            }

            master out {
              from voice.audio -> audio
            }
            """, EngineRig.Spec);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        BlockNode clip = doc.Blocks.Single(b => b.Type == "midi-clip" && b.Name.Contains("up1", StringComparison.Ordinal));
        Assert.Contains(":72:", clip.Parameters.First(x => x.Name == "notes").Value, StringComparison.Ordinal);
        BlockNode synth = doc.Blocks.Single(b => b.Type == "synth");
        Assert.Contains(synth.Froms, f => f.Block == clip.Name);
    }

    [Fact]
    public void Components_that_call_each_other_are_a_circle_AMP2247_and_the_message_names_it()
    {
        const string text = """
            composition "circle" { ample 1  tempo 100 }
            component koda.notes.ping version 1.0.0 current {
              id "demo.koda.test.ping"
              input source: Notes
              output notes: Notes
              process {
                emit koda.notes.pong(source)
              }
            }

            component koda.notes.pong version 1.0.0 current {
              id "demo.koda.test.pong"
              input source: Notes
              output notes: Notes
              process {
                emit koda.notes.ping(source)
              }
            }
            """;
        AmpleDocument doc = Parser.Parse(text, EngineRig.Spec, new ParseOptions { RequireProjectLine = false });
        List<ModuleDiagnostic> circle = [.. doc.Diagnostics.Where(d => d.Code == "AMP2247").Select(d => new ModuleDiagnostic(d.Code, d.Message, d.Line, string.Empty))];
        Assert.Equal(2, circle.Count);
        Assert.Contains(circle, d => d.Message.Contains("koda.notes.ping -> koda.notes.pong -> koda.notes.ping", StringComparison.Ordinal));
        Assert.Contains(circle, d => d.Message.Contains("calls koda.notes.pong and koda.notes.pong calls", StringComparison.Ordinal));
    }

    [Fact]
    public void A_component_that_calls_itself_is_AMP2247()
    {
        const string text = """
            composition "self" { ample 1  tempo 100 }
            component koda.notes.loop version 1.0.0 current {
              id "demo.koda.test.loop"
              input source: Notes
              output notes: Notes
              process {
                emit koda.notes.loop(source)
              }
            }
            """;
        AmpleDocument doc = Parser.Parse(text, EngineRig.Spec, new ParseOptions { RequireProjectLine = false });
        Assert.Contains(doc.Diagnostics, d => d.Code == "AMP2247" && d.Message.Contains("calls itself", StringComparison.Ordinal));
    }
}
