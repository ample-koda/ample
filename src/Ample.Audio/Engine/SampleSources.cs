using System.Text.Json.Nodes;

namespace Ample.Audio.Engine;

/// <summary>
/// Where an assistant can point the owner to real, legal samples (a DJ scratch, a vinyl crackle, a foley sound), and how such a sample comes into a project correctly:
/// only CC0 or public domain by default, the source and licence written down next to the file, nothing downloaded or bought without the owner. No network, no AI: a small fixed catalogue.
/// </summary>
public static class SampleSources
{
    private static readonly string[] Rules =
    [
        "Default: only CC0 or public domain samples. They can be used anywhere, also commercially, without conditions.",
        "CC BY is allowed only when the credit line is written down (the author, the title, the link, the licence); say so to the owner before using it.",
        "Never use a sample whose licence is unclear, non-commercial (NC), no-derivatives (ND) or 'royalty free' without a licence text: it is an experiment and must not end up in a released song.",
        "Fetch it yourself: first ample_find_samples text=<words> (it searches Freesound CC0 and the Internet Archive by itself, needs no browser or sign-in and checks every licence), then ample_fetch_sample page=<page> file=<file> title=<name> with a candidate. Only for another place on those sites use ample_web_search and ample_web_read. AMPLE reads the licence itself and only lets CC0 or public domain through, so you do not ask the owner for the file. Another site is not fetched automatically: tell the owner and let them decide.",
        "Try without a sign-in first (a preview of a sound is often enough). Only when the page or the file needs a sign-in: ample_browser_open the sign-in page (AMPLE shows its browser window by itself), ask the owner to sign in there and say when it is done, then ample_fetch_sample with browser=true. The session stays in the browser profile of AMPLE, so next time it may not ask again.",
        "A pack page shows no licence per sound: use the page of one single sound (ample_find_samples gives those).",
        "Never claim a licence you have not seen. When the owner has a file, ask for the page it came from and write the source down; do not guess.",
        "Bring the file in with ample_sample_set_add (a sample set) or ample_analyze_audio first when it is a loop or a single note; keep it under samples/ and write a SOURCE.md next to it: where it came from (link), the author, the licence and the date.",
        "The sample goes into the proposal, not straight into the song: the owner listens to the draft and keeps or drops it.",
    ];

    private static readonly (string Id, string Name, string Where, string License, string Use, string[] Tags)[] Places =
    [
        ("freesound-cc0", "Freesound (CC0 filter)", "freesound.org", "Per file. Set the licence filter to 'Creative Commons 0': files with another licence (CC BY, NC, Sampling+) are NOT what we want by default.", "The largest source of single sounds: scratches, foley, vinyl, hits, ambience. Search words: 'dj scratch', 'record scratch', 'turntable', 'vinyl crackle', 'riser'. An account is needed to download.", ["scratch", "dj", "turntable", "vinyl", "foley", "sfx", "hit", "ambience", "riser", "impact", "vocal"]),
        ("opengameart-cc0", "OpenGameArt (CC0 filter)", "opengameart.org", "Per entry. Filter on 'CC0'.", "Clean, well described sound effects and short loops (impacts, risers, UI, game sounds). Smaller than Freesound; each entry names its licence.", ["sfx", "impact", "hit", "riser", "loop", "game"]),
        ("ample-audio-packs", "AMPLE's own audio packs", "audio-packs/free in the AMPLE folder", "CC0 or public domain, checked in the source's own licence file (tier free); tier licensed means credit the author.", "Drum kits, basses, guitars, pianos and waveforms with their licence and source written down (audio-packs/packs.json, SOURCE.md). The owner fetches them with tools/fetch-audio-packs.mjs.", ["drums", "kit", "bass", "guitar", "piano", "waveform", "synth"]),
        ("internet-archive", "Internet Archive", "archive.org", "Per item. Only items marked CC0 or public domain; read the licence on the page.", "Older sample libraries and field recordings. Quality and licence vary a lot: the page decides, not the title.", ["field", "recording", "vinyl", "library", "ambience"]),
        ("own-sound", "Make it with AMPLE", "inside the song", "Yours: nothing to credit.", "A rough effect from what AMPLE has: reverse and pitch-stretch a short vocal or drum hit, chop it with a gate or tremolo, add distortion. A scratch colour, not a real record being moved.", ["scratch", "riser", "effect", "foley", "vocal", "synth"]),
    ];

    /// <summary>The catalogue: the rules, and the places (all of them, or the ones that fit the words of <c>text</c>).</summary>
    /// <param name="query">Optional <c>text</c>: words such as "dj scratch" or "vinyl"; places that match are listed first.</param>
    /// <returns>The rules, the places and a short how-to.</returns>
    public static JsonObject Run(JsonObject query)
    {
        ArgumentNullException.ThrowIfNull(query);
        string[] words = [.. (query["text"]?.GetValue<string>() ?? string.Empty).ToLowerInvariant().Split([' ', ',', ';', '-'], StringSplitOptions.RemoveEmptyEntries)];
        int Score((string Id, string Name, string Where, string License, string Use, string[] Tags) p) => words.Count(w => p.Tags.Any(t => t.Contains(w, StringComparison.Ordinal) || w.Contains(t, StringComparison.Ordinal)) || p.Use.Contains(w, StringComparison.OrdinalIgnoreCase));
        JsonArray places = [.. Places.OrderByDescending(Score).ThenBy(p => Array.IndexOf(Places, p)).Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Id,
            ["name"] = p.Name,
            ["where"] = p.Where,
            ["licence"] = p.License,
            ["use"] = p.Use,
            ["matches"] = words.Length > 0 && Score(p) > 0,
        })];
        return new JsonObject
        {
            ["summary"] = "Legal sources for real samples. Only CC0 or public domain by default; write the source down; the owner brings the file in.",
            ["rules"] = new JsonArray([.. Rules.Select(r => (JsonNode)r)]),
            ["places"] = places,
        };
    }
}
