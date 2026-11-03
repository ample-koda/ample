using Ample.Engine.Modules;
using Ample.Language;

namespace Ample.Core.Tests;

/// <summary>Text and flag properties of a Notes component: a default, a value given on the instance, and what they make.</summary>
[Trait("Category", "Modules")]
public sealed class ComponentPropertyTypeTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("ample-proptypes-").FullName;

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

    private const string Components = """
        component koda.p.progression version 1.0.0 current {
          id "demo.koda.test.progression"
          property symbols: String = "Am F"
          output notes: Notes
          process {
            emit chords(symbols, length=1bar)
          }
        }

        component koda.p.lift version 1.0.0 current {
          id "demo.koda.test.lift"
          input source: Notes
          property up: Bool = true
          output notes: Notes
          process {
            emit up ? (source |> transpose(12)) : source
          }
        }
        """;

    private AmpleDocument Expand(string instances)
    {
        Directory.CreateDirectory(Path.Combine(folder, "components"));
        File.WriteAllText(Path.Combine(folder, "components", "p.amplec"), Components);
        using ModuleService service = new(folder, (Ample.Storage.Library.Library?)null);
        return service.ParseExpanded($"composition \"types\" {{ ample 1  tempo 100  master-seed 1 }}\nimport \"components/p.amplec\"\n\nmidi-clip src at 1 for 1 {{\n  notes 1:0:1:60:100\n}}\n\n{instances}\n", EngineRig.Spec);
    }

    private static string[] Notes(AmpleDocument doc, string block) => doc.Blocks.First(b => b.Name == block).Parameters.First(p => p.Name == "notes").Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void A_text_property_has_its_default_and_takes_a_value_on_the_instance()
    {
        AmpleDocument doc = Expand("koda.p.progression plain at 1 for 2 {\n}\n\nkoda.p.progression given at 1 for 2 {\n  symbols \"Dm7 G7\"\n}\n");
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Equal(6, Notes(doc, "plain").Length);
        string[] given = Notes(doc, "given");
        Assert.Equal(8, given.Length);
        Assert.Contains(given, n => n.Contains(":67:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_flag_property_has_its_default_and_takes_a_value_on_the_instance()
    {
        AmpleDocument doc = Expand("koda.p.lift high at 1 for 1 {\n  from src.notes -> source\n}\n\nkoda.p.lift same at 1 for 1 {\n  up false\n  from src.notes -> source\n}\n");
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        Assert.Contains(":72:", Notes(doc, "high").Single(), StringComparison.Ordinal);
        Assert.Contains(":60:", Notes(doc, "same").Single(), StringComparison.Ordinal);
    }
}
