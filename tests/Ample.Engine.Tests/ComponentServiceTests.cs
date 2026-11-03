using Ample.Engine.Library;
using Ample.Engine.Modules;
using Ample.Language;

namespace Ample.Core.Tests;

/// <summary>Components in a project: the files an import finds, what a song gets from them, the pins of ample.lock (a song never changes silently), and the status of seals.</summary>
[Trait("Category", "Modules")]
public sealed class ComponentServiceTests : IDisposable
{
    private const string Header = "composition \"demo\" { ample 1  tempo 120  master-seed 7 }\n\n";

    private readonly string folder = Directory.CreateTempSubdirectory("ample-components-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, true);
        }
        catch (IOException)
        {
            // A locked file on Windows must not fail the run.
        }
    }

    private static string Comp(string process = "for i in 0..copies {\n      emit source |> shift(i * 1bar)\n    }", string id = "com.demo.twice", string seal = "") =>
        $"component demo.fx.twice version 1.0.0 {{\n  id \"{id}\"\n  input source: Notes\n  property copies: Int = 2\n  output notes: Notes\n  process {{\n    {process}\n  }}\n{seal}}}\n";

    private static string Song(string imports = "import \"fx\"\n") => Header + imports + "\nmidi-clip theme at 1 for 1 {\n  notes 1:0:1:60:100\n}\n\ndemo.fx.twice twice at 2 for 3 {\n  from theme.notes -> source\n}\n";

    private ModuleService Service(string component)
    {
        Directory.CreateDirectory(Path.Combine(folder, "components"));
        File.WriteAllText(Path.Combine(folder, "components", "fx.amplec"), component);
        return new ModuleService(folder, (Ample.Storage.Library.Library?)null);
    }

    [Fact]
    public void An_imported_component_is_found_in_the_project_and_the_song_builds_with_it()
    {
        using ModuleService service = Service(Comp());
        AmpleDocument doc = service.ParseExpanded(Song(), EngineRig.Spec);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == Severity.Error);
        string notes = doc.Blocks.First(b => b.Name == "twice").Parameters.First(p => p.Name == "notes").Value;
        Assert.Equal(2, notes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);   // one note, two copies
        Assert.Contains(doc.Diagnostics, d => d.Code == "AMP2211");   // it is not sealed: that is said, once
    }

    [Fact]
    public void The_editors_know_the_imported_components_as_block_types()
    {
        using ModuleService service = Service(Comp());
        (IReadOnlyList<ComponentDefinition> components, IReadOnlyList<Diagnostic> issues) = service.ComponentsForEditors(Song(), EngineRig.Spec);
        Assert.Empty(issues);
        Assert.Equal("demo.fx.twice", Assert.Single(components).Name);
        AmpleDocument editor = Parser.Parse(Song(), EngineRig.Spec, new ParseOptions { Components = components });
        Assert.DoesNotContain(editor.Diagnostics, d => d.Code == "AMP2001");
        Assert.True(editor.Spec!.Templates["demo.fx.twice"].Component);
        Diagnostic missing = Assert.Single(service.ComponentsForEditors(Song("import \"nowhere\"\n"), EngineRig.Spec).Issues);
        Assert.Equal("AMP2216", missing.Code);
        Assert.Equal(3, missing.Line);   // at the import line (the project line, a blank line, then the import)
    }

    [Fact]
    public void The_first_use_pins_a_component_and_a_changed_code_is_reported_until_the_change_is_accepted()
    {
        using ModuleService service = Service(Comp());
        ExpandedSong first = service.Expand(Song(), EngineRig.Spec);
        string pinned = Assert.IsType<string>(service.LockWithNewComponents(first));
        Assert.Contains("components", pinned, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(folder, LockFile.FileName), pinned);
        Assert.Null(service.LockWithNewComponents(service.Expand(Song(), EngineRig.Spec)));   // nothing new to pin
        Assert.DoesNotContain(service.ParseExpanded(Song(), EngineRig.Spec).Diagnostics, d => d.Code == "AMP2203");
        // somebody changes the code of the component behind the back of the song
        File.WriteAllText(Path.Combine(folder, "components", "fx.amplec"), Comp(process: "for i in 0..copies {\n      emit source |> shift(i * 2bars)\n    }"));
        Diagnostic changed = Assert.Single(service.ParseExpanded(Song(), EngineRig.Spec).Diagnostics, d => d.Code == "AMP2203");
        Assert.Equal(Severity.Warning, changed.Severity);
        Assert.Contains("demo.fx.twice", changed.Message, StringComparison.Ordinal);
        // a change of the description alone is not a change of the code
        File.WriteAllText(Path.Combine(folder, "components", "fx.amplec"), "/** Now with a description. */\n" + Comp());
        Assert.DoesNotContain(service.ParseExpanded(Song(), EngineRig.Spec).Diagnostics, d => d.Code == "AMP2203");
    }

    [Fact]
    public void A_sealed_component_is_trusted_until_it_changes_and_a_changed_one_says_so()
    {
        string text = Comp();
        ComponentEditResult sealedResult = ComponentEdits.Seal(text, "demo.fx.twice", null, EngineRig.Spec);
        Assert.True(sealedResult.Ok, sealedResult.Message);
        using ModuleService service = Service(sealedResult.Text);
        AmpleDocument ok = service.ParseExpanded(Song(), EngineRig.Spec);
        Assert.DoesNotContain(ok.Diagnostics, d => d.Code is "AMP2211" or "AMP2210");
        File.WriteAllText(Path.Combine(folder, "components", "fx.amplec"), sealedResult.Text.Replace("i * 1bar", "i * 3bars", StringComparison.Ordinal));
        AmpleDocument changed = service.ParseExpanded(Song(), EngineRig.Spec);
        Assert.Contains(changed.Diagnostics, d => d.Code == "AMP2210");
    }

    [Fact]
    public void The_catalog_lists_every_version_with_its_seal()
    {
        string both = Comp() + "\n" + Comp(id: "com.demo.twice").Replace("version 1.0.0", "version 2.0.0", StringComparison.Ordinal);
        using ModuleService service = Service(both);
        (IReadOnlyList<ComponentListing> components, _) = ComponentCatalog.List(service, Song(), EngineRig.Spec);
        ComponentListing twice = Assert.Single(components);
        Assert.Equal("2.0.0", twice.DefaultVersion);
        Assert.Equal(["2.0.0", "1.0.0"], twice.Versions.Select(v => v.Version));
        Assert.All(twice.Versions, v => Assert.Equal("unsealed", v.Seal));
        Assert.Equal(["source"], twice.Inputs.Select(i => i.Name));
        Assert.Equal(["copies"], twice.Properties.Select(p => p.Name));
    }

    [Fact]
    public void The_properties_tell_the_panel_which_control_fits_them()
    {
        string component = "component demo.fx.twice version 1.0.0 {\n  id \"com.demo.twice\"\n  input source: Notes\n"
            + "  #[MinValue(1), MaxValue(16)]\n  property copies: Int = 2\n"
            + "  #[MinValue(0%), MaxValue(90%), Step(5)]\n  property amount: Percent = 30%\n"
            + "  #[MinValue(-24dB), MaxValue(6dB)]\n  property level: Gain = -3dB\n"
            + "  property flip: Bool = false\n"
            + "  #[Options(\"soft\", \"hard\")]\n  property shape: String = \"soft\"\n"
            + "  property note: Notes\n"
            + "  property gap: Duration = 1bar\n"
            + "  output notes: Notes\n  process {\n    emit source\n  }\n}\n";
        using ModuleService service = Service(component);
        (IReadOnlyList<ComponentListing> components, _) = ComponentCatalog.List(service, Song(), EngineRig.Spec);
        Dictionary<string, ComponentMember> p = Assert.Single(components).Properties.ToDictionary(x => x.Name, StringComparer.Ordinal);
        Assert.Equal(("number", (double?)1, (double?)16, (string?)null), (p["copies"].Kind, p["copies"].Min, p["copies"].Max, p["copies"].Unit));
        Assert.Equal(("number", (double?)0, (double?)90, (double?)5, "%"), (p["amount"].Kind, p["amount"].Min, p["amount"].Max, p["amount"].Step, p["amount"].Unit));
        Assert.Equal(("number", (double?)(-24), (double?)6, "dB"), (p["level"].Kind, p["level"].Min, p["level"].Max, p["level"].Unit));
        Assert.Equal("toggle", p["flip"].Kind);
        Assert.Equal("choice", p["shape"].Kind);
        Assert.Equal(["soft", "hard"], p["shape"].Options);
        Assert.Equal("notes", p["note"].Kind);
        Assert.Equal("text", p["gap"].Kind);   // a time can be written in ms, beats or bars: a text field until the layout says otherwise
    }

    [Fact]
    public void The_catalog_carries_the_layout_of_the_default_version()
    {
        string component = "component demo.fx.twice version 1.0.0 {\n  id \"com.demo.twice\"\n  input source: Notes\n  #[MinValue(1), MaxValue(16)]\n  property copies: Int = 2\n  output notes: Notes\n  ui {\n    row { knob copies }\n    card { knob copies }\n    preview live\n  }\n  process {\n    emit source\n  }\n}\n";
        using ModuleService service = Service(component);
        (IReadOnlyList<ComponentListing> components, _) = ComponentCatalog.List(service, Song(), EngineRig.Spec);
        ComponentUi ui = Assert.Single(components).Ui;
        Assert.Equal("row", Assert.Single(ui.Panel).Kind);
        Assert.Equal("copies", Assert.Single(ui.Card).Property);
        Assert.Equal("live", ui.Preview);
        // without a block the layout is made from the properties
        using ModuleService plain = Service(Comp());
        ComponentUi made = Assert.Single(ComponentCatalog.List(plain, Song(), EngineRig.Spec).Components).Ui;
        Assert.Equal("field", Assert.Single(made.Panel).Kind);
    }

    [Fact]
    public void The_graph_of_an_instance_takes_values_that_are_not_in_the_song_yet()
    {
        string component = "component demo.fx.echo version 1.0.0 {\n  id \"com.demo.echo\"\n  input audio: Audio\n  #[MinValue(0%), MaxValue(90%)]\n  property feedback: Percent = 30%\n  output audio: Audio\n  process {\n    emit audio\n      |> delay(time=250ms, feedback=feedback)\n  }\n}\n";
        using ModuleService service = Service(component);
        string song = Header + "import \"fx\"\n\ndemo.fx.echo e {\n}\n";
        (_, AudioGraph asWritten) = ComponentCatalog.GraphOf(service, song, EngineRig.Spec, "e");
        Assert.Contains(("feedback", "30%"), asWritten.Nodes[0].Parameters);
        (_, AudioGraph moving) = ComponentCatalog.GraphOf(service, song, EngineRig.Spec, "e", new Dictionary<string, string>(StringComparer.Ordinal) { ["feedback"] = "55%", ["nothing"] = "1" });
        Assert.Contains(("feedback", "55%"), moving.Nodes[0].Parameters);
        Assert.True(moving.LivePreview);
    }

    [Fact]
    public void A_property_can_have_a_friendly_name_the_listing_carries()
    {
        string component = "component demo.fx.twice version 1.0.0 {\n  id \"com.demo.twice\"\n  input source: Notes\n  #[DisplayName(\"How many\"), MinValue(1), MaxValue(16)]\n  property copies: Int = 2\n  property gap: Duration = 1bar\n  output notes: Notes\n  process {\n    emit source\n  }\n}\n";
        using ModuleService service = Service(component);
        (IReadOnlyList<ComponentListing> components, _) = ComponentCatalog.List(service, Song(), EngineRig.Spec);
        Dictionary<string, ComponentMember> p = Assert.Single(components).Properties.ToDictionary(x => x.Name, StringComparer.Ordinal);
        Assert.Equal("How many", p["copies"].Title);
        Assert.Equal(string.Empty, p["gap"].Title);
    }
}
