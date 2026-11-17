using Ample.Engine.Engine;
using Ample.Language;
using Ample.Storage.Takes;

namespace Ample.Core.Tests;

/// <summary>An observation option on a component instance is not a parameter of its block, so it can never change the fingerprint that a take plan is made from (plan F22).</summary>
public sealed class DirectiveFingerprintTests
{
    [Fact]
    public async Task Observation_levels_and_statements_do_not_change_a_rendered_take()
    {
        using EngineRig rig = await EngineRig.CreateAsync();
        const string song = """
            composition "demo" { ample 1 tempo 120 master-seed 7 }
            settings { logging.level LEVEL }
            helper melody(Notes n) -> Notes {
              OBSERVATION
              return n |> transpose(+2)
            }
            midi-clip theme at 1 for 1 {
              notes 1:0:1:60:100
            }
            midi-clip shifted at 1 for 1 {
              notes melody(theme.notes)
            }
            piano piano at 1 for 1 {
              from shifted.notes -> notes
            }
            master master {
              from piano.audio -> audio
            }
            """;
        AmpleDocument Parse(string level, string observation) => Parser.Parse(song
            .Replace("LEVEL", level, StringComparison.Ordinal)
            .Replace("OBSERVATION", observation, StringComparison.Ordinal),
            EngineRig.Spec, new ParseOptions { ApplyNoteFlow = true });
        AmpleDocument plain, observed;
        using (SyntaxGate.Use(SyntaxFeatures.Observe | SyntaxFeatures.Record))
        {
            plain = Parse("off", string.Empty);
            observed = Parse("debug", "debug currentTime().hour");
        }
        Assert.DoesNotContain(plain.Diagnostics, diagnostic => diagnostic.Severity == Severity.Error);
        Assert.DoesNotContain(observed.Diagnostics, diagnostic => diagnostic.Severity == Severity.Error);
        Assert.Empty(plain.Observations);
        Assert.Single(observed.Observations);
        Assert.Equal(plain.Helpers.Select(helper => helper.Name), observed.Helpers.Select(helper => helper.Name));
        Assert.Equal(plain.HelperTable.ClosureHashes("melody(theme.notes)").Where(hash => !hash.StartsWith("source:", StringComparison.Ordinal)),
            observed.HelperTable.ClosureHashes("melody(theme.notes)").Where(hash => !hash.StartsWith("source:", StringComparison.Ordinal)));
        Assert.Equal(BuildEngine.Fingerprint(plain.Blocks.Single(b => b.Name == "shifted")),
            BuildEngine.Fingerprint(observed.Blocks.Single(b => b.Name == "shifted")));
        BuildReport first = await rig.Engine.BuildAsync(plain, new BuildOptions());
        string takeId = Assert.IsType<string>(first.Blocks.Single(b => b.Block == "piano").TakeId);
        StoredPlan plan = rig.Bank.GetPlan(rig.Bank.GetTake(takeId).PlanId);
        BuildReport second = await rig.Engine.BuildAsync(observed, new BuildOptions());
        Assert.Equal(takeId, second.Blocks.Single(b => b.Block == "piano").TakeId);
        Assert.Equal(plan.CanonicalJson, rig.Bank.GetPlan(rig.Bank.GetTake(takeId).PlanId).CanonicalJson);
    }

    [Fact]
    public void A_logging_option_on_an_instance_does_not_change_the_fingerprint_of_the_block()
    {
        const string Header = "composition \"x\" { ample 1  tempo 100  master-seed 1 }\n\n";
        const string Component = "component koda.notes.shifter version 1.0.0 {\n  input source: Notes\n  property steps: Int = 2\n  output notes: Notes\n  process {\n    emit source |> transpose(steps)\n  }\n}\n\n";
        const string Intro = "midi-clip intro at 1 for 1 {\n  notes 1:0:1:60:100\n}\n\n";
        static string Song(string extra) => Header + Component + Intro + "koda.notes.shifter up at 2 for 1 {\n  steps 3\n" + extra + "  from intro.notes -> source\n}\n";
        SpecData spec = EngineRig.Spec;
        using IDisposable scope = SyntaxGate.Use(SyntaxFeatures.Observe | SyntaxFeatures.Assert);
        BlockNode plain = Parser.Parse(Song(string.Empty), spec).Blocks.First(b => b.Name == "up");
        BlockNode noisy = Parser.Parse(Song("  logging.level trace\n  diagnostics.assertions on\n"), spec).Blocks.First(b => b.Name == "up");
        Assert.Equal(2, noisy.Directives.Count);
        Assert.Equal(BuildEngine.Fingerprint(plain), BuildEngine.Fingerprint(noisy));

        AmpleDocument Build(string level)
        {
            string text = Song("  logging.level " + level + "\n").Replace(
                "    emit source", "    debug \"checked\"\n    emit source", StringComparison.Ordinal);
            ExpandedSong expanded = ModuleExpander.Expand(text, spec, _ => null);
            return Parser.Parse(expanded.Text, spec, new ParseOptions
            {
                ApplyNoteFlow = true,
                InstanceDirectives = expanded.Directives,
            });
        }

        AmpleDocument quiet = Build("off"), observed = Build("debug");
        Assert.Empty(quiet.Observations);
        Assert.Single(observed.Observations);
        Assert.Equal(BuildEngine.Fingerprint(quiet.Blocks.Single(b => b.Name == "up")),
            BuildEngine.Fingerprint(observed.Blocks.Single(b => b.Name == "up")));
    }
}
