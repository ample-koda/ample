using System.Text.Json.Nodes;
using Ample.Language;

namespace Ample.Audio.Engine;

/// <summary>
/// AL01, a quick structural look at a song's arrangement for an assistant: which blocks play in which bars, how dense each bar is, and what is likely wrong (silent bars, harmony
/// without a bass, nothing that builds, the same layers for too long, every loop starting at bar 1 with the same length, too many layers at once). It reads only the placements
/// (<c>at</c> and <c>for</c>) of the blocks, never audio, so it is instant and deterministic. Findings are advice, not errors.
/// </summary>
public static class ArrangementLint
{
    private static readonly HashSet<string> SoundTypes = new(StringComparer.Ordinal) { "drums", "bass", "piano", "guitar", "sampler", "sequencer", "synth", "click", "choir", "ace-music", "ai-voice", "ambience", "gm", "import-audio", "midi-render", "scratch", "rewind" };
    private static readonly HashSet<string> HarmonyTypes = new(StringComparer.Ordinal) { "piano", "guitar", "synth", "choir" };

    /// <summary>Looks at one parsed song.</summary>
    public static JsonObject Lint(AmpleDocument doc)
    {
        List<BlockNode> sounds = [.. doc.Blocks.Where(b => !b.Skipped && SoundTypes.Contains(b.Type))];
        JsonArray findings = [];
        void Add(string severity, string code, string message, string? bars = null) => findings.Add(new JsonObject { ["severity"] = severity, ["code"] = code, ["message"] = message, ["bars"] = bars });

        if (!doc.Blocks.Any(b => b.Type == "master" && !b.Skipped)) Add("warning", "no-master", "There is no master block: nothing is collected into one mix.");
        if (sounds.Count == 0)
        {
            Add("warning", "no-sound", "No block makes sound (drums, bass, piano, guitar, sampler, sequencer, synth...).");
            return Result(0, [], findings, []);
        }

        // placements: a block without parts plays the whole song
        Dictionary<BlockNode, List<(int From, int To)>> spans = [];
        int total = 0;
        foreach (BlockNode block in sounds)
        {
            List<(int, int)> list = [.. block.Parts.Select(p => (p.At, p.At + Math.Max(1, p.Length) - 1))];
            spans[block] = list;
            if (list.Count > 0) total = Math.Max(total, list.Max(s => s.Item2));
        }

        if (total == 0) total = 8;
        foreach (BlockNode block in sounds.Where(b => spans[b].Count == 0)) spans[block] = [(1, total)];
        bool Active(BlockNode block, int bar) => spans[block].Any(s => bar >= s.From && bar <= s.To);
        List<string>[] names = [.. Enumerable.Range(1, total).Select(bar => sounds.Where(b => Active(b, bar)).Select(b => b.Name).ToList())];
        int[] density = [.. names.Select(n => n.Count)];

        // sections: stretches of bars with the same set of blocks
        JsonArray sections = [];
        for (int bar = 1; bar <= total;)
        {
            int end = bar;
            while (end < total && names[end].OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(names[bar - 1].OrderBy(x => x, StringComparer.Ordinal))) end++;
            if (sections.Count < 60) sections.Add(new JsonObject { ["bars"] = Range(bar, end), ["layers"] = names[bar - 1].Count, ["blocks"] = new JsonArray([.. names[bar - 1].Select(n => (JsonNode)JsonValue.Create(n)!)]) });
            bar = end + 1;
        }

        foreach ((int from, int to) in Runs(density, d => d == 0)) Add("warning", "silent-bars", $"Bars {Range(from, to)} have no sound: a gap (use it on purpose, or fill or close it).", Range(from, to));
        if (total >= 16)
        {
            int quarter = Math.Max(1, total / 4), peak = density.Max();
            if (density.Take(quarter).Max() >= peak && peak > 1) Add("info", "no-build", "The first quarter already has as many layers as the loudest part: nothing builds up. Start with fewer layers and add one every 4 to 8 bars.", Range(1, quarter));
            foreach ((int from, int to) in SameSetRuns(names).Where(r => r.To - r.From + 1 >= 12)) Add("info", "same-layers", $"The same layers play for {to - from + 1} bars ({Range(from, to)}): change something (add or drop a layer, a fill, a filter) every 4 to 8 bars.", Range(from, to));
        }

        List<BlockNode> bass = [.. sounds.Where(b => b.Type == "bass" || b.Name.Contains("bass", StringComparison.OrdinalIgnoreCase))];
        List<BlockNode> harmony = [.. sounds.Where(b => HarmonyTypes.Contains(b.Type) && !bass.Contains(b))];
        if (harmony.Count > 0)
        {
            List<int> withoutBass = [.. Enumerable.Range(1, total).Where(bar => harmony.Any(h => Active(h, bar)) && !bass.Any(b => Active(b, bar)))];
            foreach ((int from, int to) in Runs([.. Enumerable.Range(1, total).Select(bar => withoutBass.Contains(bar) ? 1 : 0)], d => d == 1).Where(r => r.To - r.From + 1 >= 2)) Add("info", "no-bass", $"Harmony plays in bars {Range(from, to)} without a bass: add a bass (or say it is on purpose).", Range(from, to));
        }

        foreach ((int from, int to) in Runs(density, d => d >= 8)) Add("info", "crowded", $"{density.Skip(from - 1).Take(to - from + 1).Max()} layers play at once in bars {Range(from, to)}: check that they do not fight for the same register; mute or pan some.", Range(from, to));
        List<BlockNode> startsAtOne = [.. sounds.Where(b => b.Parts.Count == 1 && b.Parts[0].At == 1 && b.Parts[0].Length >= 8)];
        if (startsAtOne.Count >= 3 && startsAtOne.Select(b => b.Parts[0].Length).Distinct().Count() == 1) Add("info", "same-start-same-length", $"{startsAtOne.Count} blocks all start at bar 1 and last {startsAtOne[0].Parts[0].Length} bars: stagger their entries so the song grows.", Range(1, startsAtOne[0].Parts[0].Length));
        foreach (IGrouping<string, BlockNode> group in sounds.GroupBy(b => b.Type).Where(g => g.Count() >= 2 && g.Key is "piano" or "guitar" or "synth" or "bass"))
        {
            List<BlockNode> list = [.. group];
            int overlap = Enumerable.Range(1, total).Count(bar => list.Count(b => Active(b, bar)) >= 2);
            if (overlap >= 4) Add("info", "same-type-overlap", $"{list.Count} {group.Key} blocks play together for {overlap} bars: give them different registers or pan them apart.", null);
        }

        if (total < 16) Add("info", "short", $"The song is {total} bars: fine for a loop or a sketch; a full song usually needs 32 bars or more.");
        return Result(total, density, findings, sections);
    }

    private static string Range(int from, int to) => from == to ? from.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{from}-{to}";

    // 1-based inclusive runs where the predicate holds
    private static List<(int From, int To)> Runs(int[] values, Func<int, bool> match)
    {
        List<(int, int)> runs = [];
        for (int i = 0; i < values.Length;)
        {
            if (!match(values[i])) { i++; continue; }
            int j = i;
            while (j + 1 < values.Length && match(values[j + 1])) j++;
            runs.Add((i + 1, j + 1));
            i = j + 1;
        }

        return runs;
    }

    private static List<(int From, int To)> SameSetRuns(List<string>[] names)
    {
        List<(int, int)> runs = [];
        for (int i = 0; i < names.Length;)
        {
            int j = i;
            string key = string.Join('|', names[i].OrderBy(x => x, StringComparer.Ordinal));
            while (j + 1 < names.Length && string.Join('|', names[j + 1].OrderBy(x => x, StringComparer.Ordinal)) == key) j++;
            if (names[i].Count > 0) runs.Add((i + 1, j + 1));
            i = j + 1;
        }

        return runs;
    }

    private static JsonObject Result(int total, int[] density, JsonArray findings, JsonArray sections)
    {
        bool clean = !findings.Any(f => f!["severity"]!.GetValue<string>() == "warning");
        return new JsonObject
        {
            ["bars"] = total,
            ["density"] = string.Concat(density.Select(d => d > 9 ? '+' : (char)('0' + d))),
            ["densityNote"] = "One digit per bar: how many sound blocks play (+ is ten or more).",
            ["sections"] = sections,
            ["findings"] = findings,
            ["ok"] = clean,
            ["advice"] = "These are structural hints from the placements only (at, for); listen with ample_listen before changing the song for them.",
        };
    }
}
