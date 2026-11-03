namespace Ample.Language;

/// <summary>
/// The names of ports as a song writes them. A port is on the left of a block when it takes a stream and on the right when it gives one, so its name says
/// <em>what stream</em> it carries, which is also what it can be connected to: <c>audio</c>, <c>notes</c> (MIDI), <c>render</c> (a render recipe),
/// <c>text</c>, <c>input</c> and <c>output</c> for a port that carries no stream of its own (a control value, anything), and <c>audio1</c>, <c>audio2</c>, ... for the lanes of a mixer. The templates keep their older internal names (<c>in</c>, <c>out</c>,
/// <c>mix</c>, <c>in3</c>, <c>render_in</c>); a song may write either, and the document model carries the internal one.
/// </summary>
public static class PortNames
{
    /// <summary>Gets the name a song should write for a port (the stream it carries when that is unambiguous, else the name it has).</summary>
    /// <param name="port">The port.</param>
    /// <param name="siblings">The ports of the same side of the block.</param>
    /// <param name="output">Whether it is on the output side.</param>
    public static string Public(PortSpec port, IReadOnlyList<PortSpec> siblings, bool output)
    {
        bool taken(string name) => siblings.Any(p => p.Name == name && !ReferenceEquals(p, port));
        if (!output && port.Type == "render" && port.Name == "render_in")
        {
            return taken("render") ? port.Name : "render";
        }

        if (!output && port.Type == "region" && port.Name == "region_in")
        {
            return "region";
        }

        if (port.Type != "audio")
        {
            // a port that carries no stream of its own (a control value, anything) is named by its side
            string side = output ? "out" : "in", word = output ? "output" : "input";
            return port.Name == side && !taken(word) ? word : port.Name;
        }

        if (output)
        {
            return port.Name is "out" or "mix" or "mixed" or "master" && !taken("audio") ? "audio" : port.Name;
        }

        if (port.Name is "in" or "mix")
        {
            return taken("audio") ? port.Name : "audio";
        }

        if (port.Name.Length > 2 && port.Name.StartsWith("in", StringComparison.Ordinal) && int.TryParse(port.Name[2..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int lane))
        {
            return "audio" + lane.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return port.Name;
    }

    /// <summary>The name a v1 song writes for a port of a template: the stream name the spec gives it, or its own name when it has none (a generated port).</summary>
    public static string Written(PortSpec port) => port.Stream.Length > 0 ? port.Stream : port.Name;

    /// <summary>Finds a port by its internal name, by the name a song writes for it, or (an output) by the older alias <c>out</c> for the first audio output.</summary>
    public static PortSpec? Find(IReadOnlyList<PortSpec> ports, string name, bool output)
    {
        PortSpec? exact = ports.FirstOrDefault(p => p.Name == name);
        if (exact is not null)
        {
            return exact;
        }

        PortSpec? written = ports.FirstOrDefault(p => Public(p, ports, output) == name);
        if (written is not null)
        {
            return written;
        }

        return output && name == "out" ? ports.FirstOrDefault(p => p.Type == "audio") : null;
    }

    /// <summary>The internal name of the port a song means, or the text itself when there is no such port.</summary>
    public static string Resolve(IReadOnlyList<PortSpec> ports, string name, bool output) => Find(ports, name, output)?.Name ?? name;
}
