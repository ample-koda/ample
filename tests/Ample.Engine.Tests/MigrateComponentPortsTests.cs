using Ample.Engine.Modules;
using Ample.Language;

namespace Ample.Core.Tests;

/// <summary>ample migrate renames the old port names of connections that come from a component instance (a block whose type is a component), which the song alone does not declare.</summary>
public sealed class MigrateComponentPortsTests
{
    private static string Repo => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void Old_port_names_after_a_component_instance_are_renamed_when_the_components_are_known()
    {
        string folder = Directory.CreateTempSubdirectory("ample-migrate-").FullName;
        try
        {
            string demo = Path.Combine(Repo, "web", "remix", "demos", "ambient");
            foreach (string file in Directory.GetFiles(demo, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(folder, Path.GetRelativePath(demo, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }

            string song = Path.Combine(folder, "seed-garden.amplet");
            // the stream names the song should end up with (the demo itself may still be in the old form)
            string current = File.ReadAllText(song).Replace("\r\n", "\n", StringComparison.Ordinal);
            foreach ((string was, string now) in new[] { ("-> in1", "-> audio1"), ("-> in2", "-> audio2"), ("-> in3", "-> audio3"), ("-> in4", "-> audio4"), ("final.mix ->", "final.audio ->"), ("finish.audio -> mix", "finish.audio -> audio") })
            {
                current = current.Replace(was, now, StringComparison.Ordinal);
            }

            string old = current.Replace("walkBells.audio -> audio1", "walkBells.audio -> in1", StringComparison.Ordinal).Replace("walkLead.audio -> audio2", "walkLead.audio -> in2", StringComparison.Ordinal);
            Assert.NotEqual(current, old);

            using ModuleService service = new(folder, (Ample.Storage.Library.Library?)null);
            (IReadOnlyList<ComponentDefinition> imported, _) = service.ComponentsForEditors(old, EngineRig.Spec);

            Assert.Equal(old, AmpleMigration.Plan(old, EngineRig.Spec).Text);   // without the components the song alone shows nothing to rename
            Assert.Equal(current, AmpleMigration.Plan(old, EngineRig.Spec, null, imported).Text);
        }
        finally
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
    }
}
