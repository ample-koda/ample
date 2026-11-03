namespace Ample.Language.Tests;

/// <summary>The spec files name a port by the stream it carries (what a v1 song writes); the older internal name is an optional extra.</summary>
public class SpecPortNamesTests
{
    [Fact]
    public void Every_port_of_the_spec_has_the_stream_name_a_song_writes()
    {
        List<string> wrong = [];
        foreach (TemplateSpec t in Corpus.Spec.Templates.Values)
        {
            foreach ((IReadOnlyList<PortSpec> ports, bool output) in new[] { (t.Inputs, false), (t.Outputs, true) })
            {
                foreach (PortSpec p in ports)
                {
                    string want = PortNames.Public(p, ports, output);
                    if (p.Stream != want) wrong.Add($"{t.Id}.{p.Name}: spec says {p.Stream}, a song writes {want}");
                }
            }
        }

        Assert.Empty(wrong);
    }

    [Fact]
    public void The_spec_never_names_a_port_by_an_older_internal_name_except_as_internalName()
    {
        string[] older = ["in", "out", "mix", "mixed", "render_in", "region_in"];
        List<string> bad = [];
        foreach (TemplateSpec t in Corpus.Spec.Templates.Values)
        {
            foreach (PortSpec p in t.Inputs.Concat(t.Outputs))
            {
                if (p.Type == "audio" && older.Contains(p.Stream) && !t.Inputs.Concat(t.Outputs).Any(o => o != p && o.Stream == "audio")) bad.Add($"{t.Id}: {p.Stream}");
            }
        }

        Assert.Empty(bad);
    }
}
