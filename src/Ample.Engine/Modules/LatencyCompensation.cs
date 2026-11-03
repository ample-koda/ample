using System.Globalization;
using System.Text.RegularExpressions;
using Ample.Language;

namespace Ample.Engine.Modules;

/// <summary>
/// Optional delay compensation between blocks (plan: optional plugin delay compensation). A song that says <c>settings { audio.latencyCompensation on }</c> gets, in its expanded text, a <c>shift</c> block in
/// front of every input of a mixer, bus, send or vocoder whose signal is earlier than the latest input, and a <c>cut</c> block in front of the master that takes the remaining delay off the start,
/// so the song lies on the grid again. A block adds latency only when its template declares one (<c>latencyMs</c>; no built-in does: the limiter looks ahead over a whole file without delaying); the alignment shifts that a component already puts inside its own graph count as latency too, because they
/// delay a branch. A song without the setting (or with <c>off</c>) comes back unchanged: its plan ids do not move.
/// </summary>
internal static partial class LatencyCompensation
{
    /// <summary>The song setting that switches the compensation on.</summary>
    internal const string Setting = "audio.latencyCompensation";

    private static readonly HashSet<string> Summing = new(StringComparer.Ordinal) { "mixer", "bus", "send", "vocoder" };

    [GeneratedRegex(@"^\s*""?\s*([0-9]*\.?[0-9]+)\s*(ms|s)?\s*""?\s*$", RegexOptions.None, matchTimeoutMilliseconds: 500)]
    private static partial Regex Amount();

    [GeneratedRegex(@"^(\s*)from\s+\S+\s*->\s*(\S+)(.*)$", RegexOptions.None, matchTimeoutMilliseconds: 500)]
    private static partial Regex FromText();

    [GeneratedRegex(@"__align[0-9]+$", RegexOptions.None, matchTimeoutMilliseconds: 500)]
    private static partial Regex AlignName();

    /// <summary>Adds the alignment blocks to an expanded song.</summary>
    /// <param name="song">The expanded song.</param>
    /// <param name="spec">The language data.</param>
    /// <returns>The song with the blocks (and a note about them), or the same song.</returns>
    internal static ExpandedSong Apply(ExpandedSong song, SpecData spec)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(spec);
        if (!song.Text.Contains(Setting, StringComparison.Ordinal)) return song;
        AmpleDocument doc = Parser.Parse(song.Text, spec, new ParseOptions { RequireProjectLine = false });
        if (!doc.Settings.TryGetValue(Setting, out string? value) || value != "on") return song;
        if (!spec.Templates.TryGetValue("shift", out TemplateSpec? shift) || !spec.Templates.TryGetValue("cut", out TemplateSpec? cut)) return song;
        Dictionary<string, BlockNode> blocks = [];
        foreach (BlockNode b in doc.Blocks.Where(b => !b.Skipped)) blocks[b.Name] = b;
        Dictionary<string, double> latency = new(StringComparer.Ordinal);
        double Of(string name, int depth = 0)
        {
            if (latency.TryGetValue(name, out double known)) return known;
            if (depth > 64 || !blocks.TryGetValue(name, out BlockNode? b)) return 0;
            latency[name] = 0;   // a loop counts as no latency
            double inputs = b.Froms.Where(f => !f.Disabled).Select(f => Of(f.Block, depth + 1)).DefaultIfEmpty(0).Max();
            double result = Own(b, spec) + inputs;
            latency[name] = result;
            return result;
        }

        Dictionary<int, string> replace = [];
        Dictionary<int, List<string>> before = [];
        int aligned = 0;
        double trimmed = 0;
        foreach (BlockNode b in doc.Blocks.Where(b => !b.Skipped && b.CloseLine >= 0))
        {
            List<FromLine> froms = [.. b.Froms.Where(f => !f.Disabled)];
            if (Summing.Contains(b.Type) && froms.Count >= 2)
            {
                double longest = froms.Max(f => Of(f.Block));
                foreach (FromLine f in froms)
                {
                    double gap = longest - Of(f.Block);
                    if (gap < 0.001) continue;
                    string name = $"{b.Name}__lat_{f.Input}";
                    Insert(before, b.Line, Block(shift, "shift", name, "seconds", gap / 1000, f, "audio"));
                    replace[f.Line] = name;
                    aligned++;
                }
            }
            else if (b.Type == "master" && froms.Count == 1)
            {
                double total = Of(froms[0].Block);
                if (total < 0.001) continue;
                string name = $"{b.Name}__lat_trim";
                Insert(before, b.Line, Block(cut, "cut", name, "start", total / 1000, froms[0], "audio"));
                replace[froms[0].Line] = name;
                trimmed = total;
            }
        }

        if (before.Count == 0) return song;
        string[] lines = song.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List<string> output = [];
        List<int> origin = [];
        for (int i = 0; i < lines.Length; i++)
        {
            int from = i < song.LineOrigin.Count ? song.LineOrigin[i] : i;
            if (before.TryGetValue(i, out List<string>? added))
            {
                foreach (string line in added)
                {
                    output.Add(line);
                    origin.Add(from);
                }
            }

            string text = lines[i];
            if (replace.TryGetValue(i, out string? name))
            {
                Match m = FromText().Match(text);
                if (m.Success) text = $"{m.Groups[1].Value}from {Edits.Quote(name)}.audio -> {m.Groups[2].Value}{m.Groups[3].Value}";
            }

            output.Add(text);
            origin.Add(from);
        }

        string note = $"Latency compensation is on: {aligned} input{(aligned == 1 ? string.Empty : "s")} delayed to line up" + (trimmed >= 0.001 ? $", and the master takes {trimmed.ToString("0.##", CultureInfo.InvariantCulture)} ms off the start" : string.Empty) + ".";
        return song with { Text = string.Join('\n', output), LineOrigin = origin, Diagnostics = [.. song.Diagnostics, new ModuleDiagnostic("AMP2250", note, Math.Max(0, doc.SettingsLine), null, Severity.Info)] };
    }

    private static void Insert(Dictionary<int, List<string>> before, int line, IEnumerable<string> text)
    {
        if (!before.TryGetValue(line, out List<string>? list)) before[line] = list = [];
        list.AddRange(text);
    }

    private static string[] Block(TemplateSpec template, string type, string name, string parameter, double seconds, FromLine source, string port) =>
    [
        $"{type} {Edits.Quote(name)} {{",
        $"  {parameter} {seconds.ToString("0.######", CultureInfo.InvariantCulture)}",
        $"  from {source.RawBlock}.{source.RawOutput} -> {port}",
        "}",
    ];

    /// <summary>The delay a block adds by itself, in milliseconds: what its template declares (<c>latencyMs</c>), or the delay of an alignment shift that a component put in its own graph.</summary>
    private static double Own(BlockNode b, SpecData spec)
    {
        string? Read(string name) => b.Parameters.LastOrDefault(p => p.Name == name)?.Value;
        if (Read("enabled")?.Trim().Trim('"') == "off") return 0;
        if (b.Type == "shift" && AlignName().IsMatch(b.Name))
        {
            string? text = Read("seconds");
            return text is not null && Amount().Match(text) is { Success: true } m ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 1000 : 0;
        }

        return spec.Templates.TryGetValue(b.Type, out TemplateSpec? t) ? t.LatencyMs : 0;
    }
}
