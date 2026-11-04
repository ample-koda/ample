using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Ample.Audio.Engine;
using Ample.Audio.IO;
using Ample.Engine.Control;
using Ample.Engine.Engine;
using Ample.Storage.Projects;
using Ample.Storage.Takes;
using Ample.Core.Abstractions;
using Ample.Language;
using Ample.Plugins.Analysis;

namespace Ample.Server;

/// <summary>
/// The read-only context an AI task can ask for while it works: what AMPLE is (the reference), what the project is, and the song and its blocks. It is offered through the MCP server of
/// the command line (<c>ample mcp</c>), which Codex starts for a task, and it only ever reads. A task has a token of its own for it; that token cannot do anything else.
/// </summary>
internal static class AiContext
{
    private static readonly string[] TextExtensions = [".amplet", ".ampleseq", ".seq", ".lyr", ".lrc", ".amplelrc", ".chordpro", ".chords", ".module", ".amplec", ".txt", ".md", ".csv", ".json"];

    /// <summary>A path as an agent writes it: the copy it works in calls the project "project", the project itself does not.</summary>
    public static string Norm(string path)
    {
        string p = path.Replace('\\', '/').TrimStart('/');
        while (p.StartsWith("./", StringComparison.Ordinal))
        {
            p = p[2..];
        }

        return p.StartsWith("project/", StringComparison.Ordinal) ? p["project/".Length..] : p;
    }

    /// <summary>The reference analysers (tempo, key, loudness, spectrum, pitch) on the built audio of a block: what the sound really is, not what the text says.</summary>
    public static async Task<JsonObject> AnalyzeAsync(ProjectSession s, string path, string block, string with, CancellationToken ct)
    {
        DocSession d = await s.DocAsync(Norm(path), ct).ConfigureAwait(false);
        AmpleDocument parsed = s.ParseExpanded(d);
        IReadOnlyList<IBlockRenderer> renderers = await s.RenderersAsync(ct).ConfigureAwait(false);
        ControlService control = s.NewControl(parsed, renderers);
        BuildReport report = await s.Engine.BuildAsync(parsed, new BuildOptions("bake", null, null, 0, renderers, null, control, null, null) { PlanOnly = true }, ct).ConfigureAwait(false);
        string take = report.Blocks.FirstOrDefault(b => b.Block == block)?.TakeId ?? throw new AmpleException("AMP5458", $"The block {block} has no built audio yet (the song must be built first; the user can do that with Build).");
        AnalysisRecord record = new AnalysisService(s.Bank, s.Root).Analyze(take, with, "{}");
        return new JsonObject { ["block"] = block, ["analyser"] = record.Analyzer, ["version"] = record.Version, ["result"] = JsonNode.Parse(record.Json) };
    }

    private static readonly Dictionary<string, string> HintCache = new(StringComparer.Ordinal);

    /// <summary>What the documentation says about a diagnostic code (the line where it is explained), so a failure comes with a hint and where to read more.</summary>
    public static string HintFor(string ampleRoot, string code)
    {
        lock (HintCache)
        {
            if (HintCache.TryGetValue(code, out string? known))
            {
                return known;
            }
        }

        string result = string.Empty;
        string docs = Path.Combine(ampleRoot, "docs");
        foreach (string f in new[] { Path.Combine(docs, "language", "08-diagnostics.md") })
        {
            if (!File.Exists(f))
            {
                continue;
            }

            string? line = File.ReadLines(f).FirstOrDefault(l => l.Contains(code, StringComparison.Ordinal));
            if (line is not null)
            {
                result = line.Trim().TrimStart('|', '-', '*', ' ').Trim() + $" (more: ample_docs name={Path.GetFileNameWithoutExtension(f)})";
                break;
            }
        }

        lock (HintCache)
        {
            HintCache[code] = result.Length > 400 ? result[..400] + "…" : result;
        }

        return HintCache[code];
    }

    /// <summary>For every block that did not build: why (the message and the code in it), a hint from the documentation, what feeds it and its text, so that Codex can fix it without guessing.</summary>
    public static JsonArray BuildDetails(AmpleDocument doc, BuildReport report, string ampleRoot)
    {
        JsonArray details = [];
        foreach (BlockResult r in report.Blocks.Where(b => b.Status is "failed" or "skipped" or "cancelled").Take(12))
        {
            BlockNode? b = doc.Blocks.FirstOrDefault(x => x.Name == r.Block);
            string code = System.Text.RegularExpressions.Regex.Match(r.Message ?? string.Empty, @"AMP\d{4}").Value;
            JsonObject o = new() { ["block"] = r.Block, ["type"] = b?.Type, ["status"] = r.Status, ["message"] = r.Message, ["code"] = code.Length > 0 ? code : null };
            if (code.Length > 0 && HintFor(ampleRoot, code) is { Length: > 0 } hint)
            {
                o["hint"] = hint;
            }
            else if (r.Status == "skipped")
            {
                o["hint"] = "A block is skipped when something it needs failed (see the blocks that feed it) or when it is disabled.";
            }
            else
            {
                o["hint"] = $"The message says what is missing or wrong. The ports and parameters of this block type: ample_block_types type={b?.Type ?? "<type>"}; the block as it is now: ample_block name={r.Block}.";
            }

            if (b is not null)
            {
                o["fedBy"] = new JsonArray([.. b.Froms.Select(f => (JsonNode)$"{f.Block}.{f.RawOutput.Trim('"')} -> {f.RawInput.Trim('"')}").Distinct()]);
                o["text"] = string.Join('\n', doc.Lines.Skip(b.Line).Take(Math.Min(30, (b.CloseLine < 0 ? b.Line : b.CloseLine) - b.Line + 1)));
            }

            details.Add(o);
        }

        return details;
    }

    private const int DefaultFilesLimit = 300, MaxFilesLimit = 2000;

    /// <summary>The project: its name, tempo, settings that matter for the sound, and a page of its text files.</summary>
    /// <param name="s">The project session.</param>
    /// <param name="filesQuery">Case-insensitive substring match against each file's path (a plain grep); null/empty lists every file.</param>
    /// <param name="filesKind">Keep only "text" or "binary" files; null/empty keeps both.</param>
    /// <param name="filesExt">Keep only files with this extension (with or without a leading dot, for example "wav" or ".wav"); null/empty keeps every extension.</param>
    /// <param name="filesOffset">How many matching files to skip (paging), clamped to 0 or more.</param>
    /// <param name="filesLimit">How many matching files to return at most (default 300, capped at 2000).</param>
    public static JsonObject Project(ProjectSession s, string? filesQuery = null, string? filesKind = null, string? filesExt = null, int filesOffset = 0, int? filesLimit = null)
    {
        ProjectManifest m = ProjectFile.Parse(File.ReadAllText(Path.Combine(s.Root, ProjectLayout.AmpleFolder, ProjectLayout.ProjectFile)));
        string? wantExt = string.IsNullOrWhiteSpace(filesExt) ? null : "." + filesExt.Trim().TrimStart('.');
        List<(string Rel, string Kind, long Size)> matched = [];
        foreach (string f in Directory.EnumerateFiles(s.Root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(s.Root, f).Replace('\\', '/');
            if (rel.StartsWith(".ample/", StringComparison.Ordinal) || rel.StartsWith("out/", StringComparison.Ordinal) || rel.StartsWith("exports/", StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(filesQuery) && !rel.Contains(filesQuery, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (wantExt is not null && !Path.GetExtension(rel).Equals(wantExt, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string kind = TextExtensions.Contains(Path.GetExtension(rel), StringComparer.OrdinalIgnoreCase) ? "text" : "binary";
            if (!string.IsNullOrWhiteSpace(filesKind) && !kind.Equals(filesKind.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matched.Add((rel, kind, new FileInfo(f).Length));
        }

        int offset = Math.Max(0, filesOffset);
        int limit = Math.Clamp(filesLimit ?? DefaultFilesLimit, 1, MaxFilesLimit);
        JsonArray files = [];
        foreach ((string rel, string kind, long size) in matched.Skip(offset).Take(limit))
        {
            files.Add(new JsonObject { ["path"] = rel, ["kind"] = kind, ["size"] = size });
        }

        JsonArray catalogs = [];
        foreach (Ample.Storage.Library.Mount mount in s.Library.Mounts(m.Id.ToString("D")))
        {
            catalogs.Add(new JsonObject { ["name"] = mount.Name, ["scope"] = mount.Scope, ["readOnly"] = mount.ReadOnly, ["state"] = mount.State });
        }

        // catalogs and its note are deliberately placed before the files array: that array lists every project file and can be large enough that an
        // outer size limit (the AI host, not AMPLE) truncates the response, which previously meant catalogs silently never arrived at all.
        return new JsonObject
        {
            ["name"] = m.Name,
            ["masterSeed"] = m.MasterSeed.ToString(CultureInfo.InvariantCulture),
            ["defaultEngine"] = m.Settings.DefaultEngine,
            ["catalogs"] = catalogs,
            ["catalogsNote"] = "The project's linked sample-pack folders (global catalogs reach every project, project links are this project only). An empty list means none are linked yet, not that none exist on this machine — ask the user before assuming a source is unavailable. Sample sets inside a catalog resolve by name the same way as project folders (see the sampleset/file parameter kind); you cannot browse a catalog's own files directly.",
            ["aceGenerationBaseline"] = AceBaseline(),
            ["note"] = "Songs are the .amplet files. Sequences are .ampleseq or .seq files. Everything marked binary (audio, samples) is not for you to read or change.",
            ["totalFiles"] = matched.Count,
            ["filesOffset"] = offset,
            ["filesLimit"] = limit,
            ["filesTruncated"] = offset + files.Count < matched.Count,
            ["filesNote"] = "files is a page: filesQuery (substring match on the path), filesKind (text or binary) and filesExt (for example wav) filter it; filesOffset/filesLimit page it (default 300, max 2000 at a time). When filesTruncated is true, call again with filesOffset at offset+returned count, or narrow with filesQuery/filesKind/filesExt instead of assuming a file is missing.",
            ["files"] = files,
        };
    }

    /// <summary>The explicit ACE starting recipe Codex must keep fixed in seed comparisons.</summary>
    internal static JsonObject AceBaseline() => new()
    {
        ["turbo"] = new JsonObject { ["steps"] = 8, ["guidance"] = 1, ["shift"] = 3, ["solver"] = "euler" },
        ["sft"] = new JsonObject { ["steps"] = 50, ["guidance"] = 7, ["shift"] = 1, ["solver"] = "euler" },
        ["base"] = new JsonObject { ["steps"] = 50, ["guidance"] = 7, ["shift"] = 3, ["solver"] = "euler" },
        ["note"] = "Applies to XL variants too. The exact filename determines the family. Write model, steps, guidance, shift, solver, seed and lm_seed explicitly; keep all except the intended seed fixed for comparisons. Unknown variants need their own documented settings. Starting values do not guarantee audio quality.",
    };

    /// <summary>The reference documents: a list, one document, or the places where a word is found.</summary>
    public static JsonObject Docs(ProjectSession s, string? name, string? query)
    {
        string docs = Path.Combine(s.AmpleRoot, "docs");
        string guide = Path.Combine(s.AmpleRoot, "guide", "pages");
        string language = Path.Combine(docs, "language");
        // a folder that is not on this server (a release ships what it needs) is no reason to fail the whole lookup
        List<string> files = [.. (Directory.Exists(docs) ? Directory.EnumerateFiles(docs, "*.md", SearchOption.TopDirectoryOnly) : []), .. (Directory.Exists(language) ? Directory.EnumerateFiles(language, "*.md") : []), .. (Directory.Exists(guide) ? Directory.EnumerateFiles(guide, "*.md") : [])];
        string Rel(string f) => f.StartsWith(guide, StringComparison.OrdinalIgnoreCase) ? "guide/" + Path.GetFileName(f) : Path.GetRelativePath(docs, f).Replace('\\', '/');
        if (!string.IsNullOrWhiteSpace(name))
        {
            // the name may come with the folder (docs/), the extension (.md) or other slashes: all of those find the same document
            string wanted = name.Trim().Replace('\\', '/').TrimStart('/');
            if (wanted.StartsWith("docs/", StringComparison.OrdinalIgnoreCase))
            {
                wanted = wanted[5..];
            }

            if (wanted.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                wanted = wanted[..^3];
            }

            string? file = files.FirstOrDefault(f => string.Equals(Rel(f)[..^3], wanted, StringComparison.OrdinalIgnoreCase)) ?? files.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), wanted, StringComparison.OrdinalIgnoreCase));
            if (file is null)
            {
                // not an error of the model's: it gets the documents that look like what it asked for, and can try again at once
                string key = Path.GetFileNameWithoutExtension(wanted);
                string[] near = [.. files.Select(f => Rel(f)[..^3]).Where(n => n.Contains(key, StringComparison.OrdinalIgnoreCase) || key.Contains(Path.GetFileName(n), StringComparison.OrdinalIgnoreCase) || key.Split('-', '_', ' ').Any(w => w.Length > 3 && n.Contains(w, StringComparison.OrdinalIgnoreCase))).Order(StringComparer.Ordinal).Take(10)];
                return new JsonObject { ["error"] = $"There is no document called '{name}'.", ["didYouMean"] = new JsonArray([.. near.Select(n => (JsonNode)n)]), ["hint"] = near.Length == 0 ? "Call ample_docs without arguments for the list, or with query to search the text." : "Use one of these names." };
            }

            string text = File.ReadAllText(file);
            return new JsonObject { ["name"] = Rel(file), ["text"] = text.Length > 60000 ? text[..60000] + "\n… (cut)" : text };
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            JsonArray hits = [];
            foreach (string f in files)
            {
                string[] lines = File.ReadAllLines(f);
                for (int i = 0; i < lines.Length && hits.Count < 25; i++)
                {
                    if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        hits.Add(new JsonObject { ["doc"] = Rel(f), ["line"] = i + 1, ["text"] = string.Join('\n', lines.Skip(Math.Max(0, i - 1)).Take(4)) });
                    }
                }
            }

            return new JsonObject { ["query"] = query, ["hits"] = hits };
        }

        return new JsonObject { ["documents"] = new JsonArray([.. files.Select(f => (JsonNode)new JsonObject { ["name"] = Rel(f), ["title"] = File.ReadLines(f).FirstOrDefault(l => l.StartsWith('#'))?.TrimStart('#', ' ') })]) };
    }

    /// <summary>The block types: all of them in short, or one in full.</summary>
    public static JsonObject BlockTypes(ProjectSession s, string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return new JsonObject { ["types"] = new JsonArray([.. s.Spec.Templates.Values.OrderBy(t => t.Id, StringComparer.Ordinal).Select(t => (JsonNode)new JsonObject { ["id"] = t.Id, ["title"] = t.Title, ["version"] = t.Version, ["in"] = string.Join(", ", t.Inputs.Select(i => $"{PortNames.Written(i)}:{i.Type}").Distinct()), ["out"] = string.Join(", ", t.Outputs.Select(o => $"{PortNames.Written(o)}:{o.Type}").Distinct()), ["does"] = t.Description })]), ["lookup"] = "Details of one type: ample_block_types type=<id>. Search by word: ample_lookup topic=search query=<word>." };
        }

        TemplateSpec t2 = s.Spec.Templates.GetValueOrDefault(type) ?? throw new AmpleException("AMP5458", $"There is no block type {type}.");
        return new JsonObject
        {
            ["id"] = t2.Id,
            ["title"] = t2.Title,
            ["version"] = t2.Version,
            ["description"] = t2.Description,
            ["inputs"] = new JsonArray([.. t2.Inputs.Select(i => (JsonNode)new JsonObject { ["name"] = PortNames.Written(i), ["type"] = i.Type, ["required"] = i.Required })]),
            ["controlInputs"] = new JsonArray([.. t2.ControlInputs.Select(i => (JsonNode)new JsonObject { ["name"] = PortNames.Written(i), ["type"] = i.Type })]),
            ["outputs"] = new JsonArray([.. t2.Outputs.Select(o => (JsonNode)new JsonObject { ["name"] = PortNames.Written(o), ["type"] = o.Type })]),
            ["parameters"] = new JsonArray([.. t2.ParameterSpecs.Values.Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["default"] = p.Default, ["kind"] = p.Kind, ["options"] = p.Options is null ? null : new JsonArray([.. p.Options.Select(o => (JsonNode)o)]), ["label"] = p.Label, ["valueType"] = p.Type, ["unit"] = p.Unit, ["min"] = p.Min, ["max"] = p.Max, ["step"] = p.Step, ["accept"] = p.Accept is null ? null : new JsonArray([.. p.Accept.Select(a => (JsonNode)a)]), ["doc"] = p.Documentation, ["write"] = WriteForm(p) })]),
            ["lookup"] = "More: ample_lookup topic=parameter name=<type>.<parameter> (what it does and what it touches), topic=units, topic=impact name=<block>, topic=search query=<word>.",
        };
    }

    /// <summary>How a value of a parameter is written in the song, with an example inside its range.</summary>
    private static string WriteForm(TemplateParameterSpec p)
    {
        string unit = string.IsNullOrEmpty(p.Unit) ? string.Empty : (p.Unit is "%" ? "%" : " " + p.Unit);
        if (p.Kind == "choice" && p.Options is { Count: > 0 })
        {
            return "one of: " + string.Join(", ", p.Options.Take(12));
        }

        if (p.Kind == "number")
        {
            string range = p.Min is not null || p.Max is not null ? $" (from {p.Min?.ToString(CultureInfo.InvariantCulture) ?? "any"} to {p.Max?.ToString(CultureInfo.InvariantCulture) ?? "any"})" : string.Empty;
            return $"a number{(string.IsNullOrEmpty(p.Unit) ? string.Empty : " in " + p.Unit)}, written like {p.Default}{range}";
        }

        return p.Kind switch { "file" => "a project path of a file", "sampleset" => "a folder of samples", "seed" => "a whole number (or leave it: it is derived)", "pattern" => "a pattern text", _ => $"text, default {p.Default}" } + unit;
    }

    private static readonly (string Unit, string Meaning)[] UnitMeanings =
    [
        ("dB", "decibel: a change of level. 0 dB = no change, negative = quieter, positive = louder. -6 dB is about half the amplitude, +6 dB about double. 'x% quieter' is 20*log10(1-x): 10% = -0.9 dB, 20% = -1.9 dB, 30% = -3.1 dB, 50% = -6.0 dB, 75% = -12 dB. Written like -3 dB (the number can also be written without the unit where the parameter says so)."),
        ("%", "percent of the range of the parameter (a mix of 25% = a quarter wet). Written with the percent sign, like 25%."),
        ("ms", "milliseconds (1000 ms = 1 s). Attack and release times, delays, periods."),
        ("s", "seconds, for positions in audio (start, end, first, last)."),
        ("Hz", "hertz (cycles per second): frequencies; 440 Hz is the A above middle C. Written like 4000 Hz."),
        ("bars", "bars of the song (a bar has 4 beats unless said otherwise). Positions in the song are 1-based bars: at 1 for 8 is bars 1 to 8; a block lasts the 'for' number of bars."),
        ("LUFS", "loudness units relative to full scale: the perceived loudness of a whole piece; lower (more negative) is quieter. 1 LU ~ 1 dB."),
        ("bank", "a General MIDI sound bank number."),
        ("GM program", "a General MIDI instrument number (0 to 127, or its name)."),
        ("MIDI channel", "a MIDI channel number 1 to 16 (10 is drums in General MIDI)."),
    ];

    private static JsonObject UnitsInfo(ProjectSession s)
    {
        JsonArray units = [];
        foreach ((string unit, string meaning) in UnitMeanings)
        {
            string[] users = [.. s.Spec.Templates.Values.SelectMany(t => t.ParameterSpecs.Values.Where(p => p.Unit == unit).Select(p => $"{t.Id}.{p.Name}")).Take(8)];
            units.Add(new JsonObject { ["unit"] = unit, ["meaning"] = meaning, ["usedBy"] = new JsonArray([.. users.Select(u => (JsonNode)u)]) });
        }

        return new JsonObject
        {
            ["units"] = units,
            ["time"] = "tempo is in beats per minute (BPM); a bar of 4 beats lasts 240/tempo seconds (tempo 100 = 2.4 s). 'at <bar> for <bars>' places a block on the timeline.",
            ["seeds"] = "master-seed and the seed of a block make the 'random' choices repeatable: the same text gives the same sound. Changing a seed changes the sound of that block (and everything downstream).",
            ["pan"] = "a position between left and right; see the parameter in ample_block_types for its range and unit.",
        };
    }

    /// <summary>What reading a parameter, a block or a word more closely gives: units, the full description of a parameter, what a change reaches, and a search over the types and the documentation.</summary>
    public static JsonObject Lookup(ProjectSession s, string topic, string name, string query, string? songText)
    {
        switch ((topic ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "units":
            case "conventions":
                return UnitsInfo(s);
            case "parameter":
                {
                    string[] parts = (name ?? string.Empty).Split('.', 2);
                    IEnumerable<(TemplateSpec T, TemplateParameterSpec P)> hits = s.Spec.Templates.Values.SelectMany(t => t.ParameterSpecs.Values.Select(p => (T: t, P: p))).Where(x => parts.Length == 2 ? x.T.Id == parts[0] && x.P.Name == parts[1] : x.P.Name == parts[0]);
                    JsonArray found = [];
                    foreach ((TemplateSpec t, TemplateParameterSpec p) in hits.Take(12))
                    {
                        found.Add(new JsonObject
                        {
                            ["type"] = t.Id,
                            ["parameter"] = p.Name,
                            ["label"] = p.Label,
                            ["doc"] = p.Documentation,
                            ["unit"] = p.Unit,
                            ["min"] = p.Min,
                            ["max"] = p.Max,
                            ["default"] = p.Default,
                            ["write"] = WriteForm(p),
                            ["reaches"] = $"A change re-renders every block of type {t.Id} that has it, and everything downstream of those blocks (the blocks fed by their outputs, up to the master).",
                            ["unitMeaning"] = UnitMeanings.FirstOrDefault(u => u.Unit == p.Unit).Meaning,
                        });
                    }

                    return found.Count == 0 ? new JsonObject { ["found"] = false, ["note"] = $"No parameter '{name}'. Use type.parameter (for example reverb.mix) or ample_lookup topic=search query=<word>." } : new JsonObject { ["found"] = true, ["parameters"] = found };
                }

            case "impact":
                {
                    if (songText is null)
                    {
                        return new JsonObject { ["found"] = false, ["note"] = "There is no song to look at." };
                    }

                    AmpleDocument doc = new Ample.Engine.Modules.ModuleService(s.Root, () => s.Library, s.Manifest.Id.ToString("D")).ParseExpanded(songText, s.Spec);
                    Dictionary<string, BlockNode> by = doc.Blocks.Where(b => !b.Skipped).GroupBy(b => b.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                    if (string.IsNullOrWhiteSpace(name) || !by.TryGetValue(name, out BlockNode? start))
                    {
                        return new JsonObject { ["found"] = false, ["note"] = $"There is no block '{name}'. Blocks: {string.Join(", ", by.Keys.Take(40))}." };
                    }

                    HashSet<string> down = Walk(by, name, forward: true), up = Walk(by, name, forward: false);
                    string? master = doc.Blocks.LastOrDefault(b => !b.Skipped && b.Type == "master")?.Name;
                    string[] files = [.. start.Parameters.Where(p => s.Spec.Templates.TryGetValue(start.Type, out TemplateSpec? ts) && ts.ParameterSpecs.TryGetValue(p.Name, out TemplateParameterSpec? ps) && ps.Kind is "file" or "sampleset").Select(p => $"{p.Name}={p.Value}")];
                    return new JsonObject
                    {
                        ["found"] = true,
                        ["block"] = name,
                        ["type"] = start.Type,
                        ["feedsDirectly"] = new JsonArray([.. by.Values.Where(b => b.Froms.Any(f => f.Block == name)).Select(b => (JsonNode)b.Name)]),
                        ["downstream"] = new JsonArray([.. down.Order(StringComparer.Ordinal).Select(x => (JsonNode)x)]),
                        ["upstream"] = new JsonArray([.. up.Order(StringComparer.Ordinal).Select(x => (JsonNode)x)]),
                        ["reachesMaster"] = master is not null && (name == master || down.Contains(master)),
                        ["files"] = new JsonArray([.. files.Select(f => (JsonNode)f)]),
                        ["whenYouChangeIt"] = $"The block itself and its {down.Count} downstream block(s) are rendered again; the {up.Count} upstream block(s) keep their sound. A change of tempo or master-seed (project line) changes every block.",
                    };
                }

            case "howto":
                return HowTo(query ?? name ?? string.Empty);
            case "diagnostic":
                return Diagnostics(s, name ?? query ?? string.Empty);
            case "search":
                return Search(s, query ?? name ?? string.Empty);
            default:
                return new JsonObject
                {
                    ["topics"] = new JsonArray(
                        new JsonObject { ["topic"] = "units", ["gives"] = "what dB, %, ms, s, Hz, bars, LUFS mean, how time and seeds work, and how 'x% quieter' converts to dB" },
                        new JsonObject { ["topic"] = "parameter", ["name"] = "type.parameter (or just the parameter)", ["gives"] = "its unit, range, default, how to write it and what a change reaches" },
                        new JsonObject { ["topic"] = "impact", ["name"] = "a block of the song", ["gives"] = "what feeds it, what it feeds (downstream, what is rendered again), the files it reads, whether it reaches the master" },
                        new JsonObject { ["topic"] = "howto", ["query"] = "automation, fades, speed, sections, sidechain, tempo, chorus...", ["gives"] = "how to do something that changes over time or is not a plain parameter: the syntax, what accepts it, and what is NOT possible with the closest workable alternative" },
                        new JsonObject { ["topic"] = "diagnostic", ["name"] = "AMP code or a word", ["gives"] = "what an error or warning means, with a wrong and a right example" },
                        new JsonObject { ["topic"] = "search", ["query"] = "a word", ["gives"] = "the block types, parameters and documents that mention it" }),
                };
        }
    }

    /// <summary>The WAV bytes of a take (its first audio data), or null when the take has no audio.</summary>
    public static byte[]? WavBytes(Takebank bank, string takeId, string role = "")
    {
        TakeRead take = bank.ReadTake(bank.ResolveTakeId(takeId));
        foreach (TakeDataRef d in role.Length > 0 && take.Take.Data.Any(x => x.Role == role) ? take.Take.Data.Where(x => x.Role == role) : take.Take.Data)
        {
            using Stream st = bank.OpenData(d.DataId);
            using MemoryStream ms = new();
            st.CopyTo(ms);
            byte[] bytes = ms.ToArray();
            if (bytes.Length >= 44 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F')
            {
                return bytes;
            }
        }

        return null;
    }

    private static (StereoBuffer Buffer, int Rate) DecodeAudio(byte[] bytes)
    {
        if (BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(22)) == 2)
        {
            return StereoWav.Decode(bytes);
        }

        (short[] samples, int rate) = Wav.Decode(bytes);
        return (StereoBuffer.FromMono(samples), rate);
    }

    /// <summary>What the audio of a take really is: how long (seconds), how loud at the peak, how much silence at the start and at the end.</summary>
    public static JsonObject? AudioFacts(Takebank bank, string takeId)
    {
        TakeRead take = bank.ReadTake(bank.ResolveTakeId(takeId));
        foreach (TakeDataRef d in take.Take.Data)
        {
            using Stream st = bank.OpenData(d.DataId);
            using MemoryStream ms = new();
            st.CopyTo(ms);
            byte[] bytes = ms.ToArray();
            if (bytes.Length < 44 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F')
            {
                continue;
            }

            (Ample.Audio.Engine.StereoBuffer buffer, int rate) = DecodeAudio(bytes);
            int n = buffer.Length;
            int peak = 0, first = -1, last = -1;
            int floor = (int)(32768 * Math.Pow(10, -60.0 / 20));   // -60 dBFS: below this it counts as silence
            for (int i = 0; i < n; i++)
            {
                int v = Math.Max(Math.Abs((int)buffer.Left[i]), Math.Abs((int)buffer.Right[i]));
                peak = Math.Max(peak, v);
                if (v > floor)
                {
                    if (first < 0)
                    {
                        first = i;
                    }

                    last = i;
                }
            }

            return new JsonObject
            {
                ["seconds"] = Math.Round(n / (double)rate, 3),
                ["sampleRate"] = rate,
                ["peakDbfs"] = peak == 0 ? null : Math.Round(20 * Math.Log10(peak / 32768.0), 2),
                ["silenceAtStartSeconds"] = first < 0 ? null : Math.Round(first / (double)rate, 3),
                ["silenceAtEndSeconds"] = last < 0 ? null : Math.Round((n - 1 - last) / (double)rate, 3),
                ["silent"] = first < 0,
            };
        }

        return null;
    }

    /// <summary>The arrangement of a song as text: tempo, where every block that reaches the master sits (in bars and in seconds), the slice of the master, and the length that comes out.</summary>
    public static JsonObject Arrangement(ProjectSession s, string songText)
    {
        AmpleDocument doc = new Ample.Engine.Modules.ModuleService(s.Root, () => s.Library, s.Manifest.Id.ToString("D")).ParseExpanded(songText, s.Spec);
        double tempo = doc.Project?.Tempo is > 0 ? doc.Project.Tempo : 120;
        double bar = 240.0 / tempo;
        Dictionary<string, BlockNode> by = doc.Blocks.Where(b => !b.Skipped).GroupBy(b => b.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        BlockNode? master = doc.Blocks.LastOrDefault(b => !b.Skipped && b.Type == "master");
        HashSet<string> active = master is null ? [] : [master.Name, .. Walk(by, master.Name, forward: false)];
        JsonArray placements = [];
        int lastBar = 0;
        foreach (BlockNode b in doc.Blocks.Where(b => !b.Skipped && active.Contains(b.Name) && b.Parts.Count > 0))
        {
            foreach (Part p in b.Parts)
            {
                lastBar = Math.Max(lastBar, p.At + p.Length - 1);
                placements.Add(new JsonObject { ["block"] = b.Name, ["type"] = b.Type, ["at"] = p.At, ["for"] = p.Length, ["startSeconds"] = Math.Round((p.At - 1) * bar, 2), ["endSeconds"] = Math.Round((p.At - 1 + p.Length) * bar, 2) });
            }
        }

        string Param(string name) => master?.Parameters.FirstOrDefault(p => p.Name == name)?.Value ?? string.Empty;
        int? sliceFrom = int.TryParse(Param("slice_from"), out int sf) ? sf : null, sliceTo = int.TryParse(Param("slice_to"), out int st) ? st : null;
        int from = sliceFrom ?? 1, to = sliceTo ?? lastBar, effectiveBars = Math.Max(0, to - from + 1);
        return new JsonObject
        {
            ["tempoBpm"] = tempo,
            ["barSeconds"] = Math.Round(bar, 3),
            ["lastBar"] = lastBar,
            ["lengthOfTheArrangementSeconds"] = Math.Round(lastBar * bar, 2),
            ["master"] = master?.Name,
            ["slice"] = sliceFrom is null && sliceTo is null ? null : new JsonObject { ["fromBar"] = from, ["toBar"] = to },
            ["effectiveBars"] = effectiveBars,
            ["effectiveSecondsFromTheText"] = Math.Round(effectiveBars * bar, 2),
            ["placements"] = placements,
            ["notPlaced"] = new JsonArray([.. doc.Blocks.Where(b => !b.Skipped && active.Contains(b.Name) && b.Parts.Count == 0 && b.Type != "master").Select(b => (JsonNode)b.Name).Take(40)]),
            ["notReachingTheMaster"] = new JsonArray([.. doc.Blocks.Where(b => !b.Skipped && !active.Contains(b.Name)).Select(b => (JsonNode)b.Name).Take(40)]),
            ["note"] = "Seconds from the text assume bars of 4 beats; 'measured' (ample_arrangement measure=true) is the real length of the built master.",
        };
    }

    /// <summary>What changed in the arrangement between two versions of a song: blocks added or removed, moved or resized, tempo, slice and the length.</summary>
    public static JsonArray ArrangementChanges(JsonObject before, JsonObject after)
    {
        JsonArray changes = [];
        string[] Keys(JsonObject o) => [.. (o["placements"] as JsonArray ?? []).Select(p => $"{p!["block"]}@{p["at"]}+{p["for"]}")];
        string[] b = Keys(before), a = Keys(after);
        string[] blocksBefore = [.. b.Select(x => x.Split('@')[0]).Distinct()], blocksAfter = [.. a.Select(x => x.Split('@')[0]).Distinct()];
        foreach (string added in blocksAfter.Except(blocksBefore))
        {
            changes.Add($"added to the arrangement: {added} ({string.Join(", ", a.Where(x => x.StartsWith(added + "@", StringComparison.Ordinal)).Select(x => x[(added.Length + 1)..]))})");
        }

        foreach (string removed in blocksBefore.Except(blocksAfter))
        {
            changes.Add($"left the arrangement: {removed}");
        }

        foreach (string name in blocksAfter.Intersect(blocksBefore))
        {
            string[] was = [.. b.Where(x => x.StartsWith(name + "@", StringComparison.Ordinal))], now = [.. a.Where(x => x.StartsWith(name + "@", StringComparison.Ordinal))];
            if (!was.SequenceEqual(now))
            {
                changes.Add($"moved or resized: {name}: {string.Join(", ", was.Select(x => x[(name.Length + 1)..]))} -> {string.Join(", ", now.Select(x => x[(name.Length + 1)..]))} (at+bars)");
            }
        }

        foreach (string key in new[] { "tempoBpm", "effectiveBars", "effectiveSecondsFromTheText" })
        {
            if (before[key]?.ToString() != after[key]?.ToString())
            {
                changes.Add($"{key}: {before[key]} -> {after[key]}");
            }
        }

        if (before["slice"]?.ToJsonString() != after["slice"]?.ToJsonString())
        {
            changes.Add($"slice of the master: {before["slice"]?.ToJsonString() ?? "none"} -> {after["slice"]?.ToJsonString() ?? "none"}");
        }

        return changes;
    }

    private static readonly (string[] Words, string Title, string Text)[] HowTos =
    [
        (["automation", "automate", "change over time", "over the song", "ramp", "increase", "gradually", "lfo", "envelope", "env", "curve"], "Values that change over the song (automation)",
            "A parameter can follow a curve: auto(bar:value, bar:value, ...) with whole bar numbers from 1 and whole values; between two points it moves in a straight line, before the first point it is the first value and after the last the last value. Example: gain auto(1:-60, 5:0, 33:0, 37:-60). lfo(period, low, high) is a triangle between low and high with a period of an even number of bars. env(start, rise, hold, fall, low, high) rises, holds and falls in bars. fadein(start, length, shape) and fadeout(start, length, shape) (start and length in bars like 5 or 5.5, or seconds like 12s; shape lin, equal or exp) work on a volume gain and an effect mix only. WHAT ACCEPTS IT: volume.gain (dB), the mix (%) of delay, reverb, chorus, flanger, phaser and bitcrusher, compressor.threshold (dB), filter.cutoff (Hz), pan.pan and stretch.speed (percent, 25 to 400). Other parameters take a plain value. Bars to seconds: a bar is 240/tempo seconds (tempo 120: 2 s), so 'every 4 s' at tempo 120 is every 2 bars. Values are whole numbers."),
        (["fade", "crossfade", "fade in", "fade out"], "Fades",
            "A fade is written in the gain or mix: gain fadeout(7, 6s, exp) starts at bar 7, lasts 6 seconds, curve exp (slow start). A fade block gives it a place of its own in a chain; a crossfade block turns one sound into another. Read the guide page: ample_docs name=guide/08-fades."),
        (["speed", "slow", "slower", "faster", "stretch", "tempo", "ritardando", "accelerando", "slow down", "speed up", "time stretch", "pitch"], "Speed and tempo",
            "The project tempo is one number for the whole song: it cannot change over time, and tempo is not automatable. A sound can be played slower or faster with the stretch block: speed in percent (25 to 400; 50 is half speed and keeps pitch). For a gradual speed ramp, use speed auto(bar:percent, ...), for example speed auto(1:100, 9:50); the curve moves linearly between bars while the rendered block keeps its length. This is a stretch-speed ramp, not a project-wide tempo change. A sequencer or a loop has its own speed in its text."),
        (["section", "cut", "slice", "part", "repeat", "loop", "arrangement", "place", "at", "for", "bars", "length", "shorter", "longer"], "Placing and cutting parts",
            "A block is placed with at <bar> for <bars> (bars count from 1): several placements are allowed (at 1 for 4 at 9 for 4). A cut block keeps a stretch of its audio input (start, end in seconds, or the first or last seconds, optionally repeated). The master can cut one part of the arrangement out with slice_from and slice_to (bars). ample_arrangement tells where everything sits and how long the result is."),
        (["pause", "gap", "silence", "silent", "later", "start later", "wait", "between", "timing", "offset", "seconds", "beats", "spoken", "tik", "walking", "join", "sequence of clips"], "A pause, a gap, or starting a sound a little later",
            "A placement is in WHOLE bars only (at 1 for 4; at 1.8 or at 2.42 is not valid). To put something a few seconds or beats later, or to leave a pause: (1) a shift block after the sound: seconds and/or beats later (a minus moves it earlier), the sound keeps its length plus the shift; (2) a join block: in1, in2, in3 ... are played one after the other with a gap (seconds plus beats) between them, for spoken phrases with a pause; (3) in the text of a voice-clone block write [pause 800 ms] or [pause 1.2 s] between the words: the voice is made per piece and AMPLE puts the silence between. A delay block is not needed for this."),
        (["sidechain", "duck", "pump", "key"], "Sidechain and ducking",
            "The compressor has a key input: connect the block whose level should push the sound down (from kick.audio -> key). A duck block does it with a simple amount. See the compressor and duck block types."),
        (["sample set", "instrument from", "own instrument", "soundfont", "sampler", "make an instrument", "layers", "velocity layer", "round robin", "envelope", "attack", "release", "sustain loop", "loop point", "preview", "audiohero"], "Building an instrument from samples",
            "A sample set is a FOLDER of WAV files that a piano, bass or guitar block (parameter set=samples/<name>), or a notes channel of a sequencer (set=samples/<name>), plays on the piano roll; a drums block plays a kit folder (kit=samples/<name>). Name each file by the note it holds: c3.wav, eb3.wav, gb3.wav, c#4.wav (c4 is middle C); a note that is not recorded is made from the nearest recording, so a few samples an octave or a third apart are enough. A softer or harder recording of the same note is a layer: c3_v2_r1.wav (v = layer, r = round robin). " +
            "Make the files with ample_sample_set_add: source is a project audio file, or workspace/<path> for a fetched preview (mp3 is decoded to WAV for you); destination is the WAV inside the set folder (samples/campfire/c4.wav); it never overwrites. Add loop:true for a note that should sustain (strings, pads, organs): AMPLE finds loop points for that sample and writes them into the folder's set.json; the answer says where and how good the match is (quality near 1 is a clean loop; found:false means the sample is too short or has no matching stretch and plays as a one-shot, which is right for plucked or percussive sounds). " +
            "envelope sets one ADSR for the whole set (attack, hold, decay in ms, sustain 0-100, release in ms): a pad is attack 200, release 800; a pluck is attack 2, decay 300, sustain 0. A drums kit uses the envelope too but never loops. Then write the notes in a midi-clip or the sequencer channel and run ample_check and ample_build. AudioHero previews are non-commercial exploratory material: say so, and do not present them as licensed assets. No .sf2 file can be written yet."),
        (["delay llama", "delay lama", "llama", "monk", "chant", "chanting", "formant", "vowel", "ooh", "aah", "vocal synth", "singing synth", "choir synth", "hum"], "Delay Llama (a singing formant voice)",
            "delay-llama is an instrument that SINGS notes with a formant voice (FOF synthesis, in the spirit of the classic Delay Lama): connect notes to it (from melody.notes -> notes). vowel is 0 to 100 (a 0, e 25, i 50, o 75, u 100) or a letter, voice is the size of the singer (low = big and deep), mouth how open (closed is a hum); all three may be curves, vowel auto(1:0, 4:100) moves the mouth over the song. " +
            "It is monophonic: the last note wins and notes that touch glide (glide in ms). vibrato and vibrato_rate, breath, unison (1 to 7 voices) with detune and spread, an ADSR (attack, decay, sustain, release), echo 0-100% (the classic stereo echo), gain and leveler (0-100%, default 50: turns down what gets too loud, never up). The pitch bend of the notes (bend on the midi-clip, ;bend= on a note) is sung natively. " +
            "In a sequencer a channel of type llama sings its notes rows the same way (channel choir llama vowel=30 voice=60 glide=120 unison=3 echo=20). The window of the block (Advanced) has a vowel pad, the knobs and the singing llama (character off hides it)."),
        (["note pipeline", "pipeline", "|>", "bake", "notes expression", "formula notes", "derived notes", "for auto", "concat", "merge notes", "transpose the notes", "bars(", "editable"],
            "A midi-clip's notes can be a PIPELINE instead of a literal: notes take.notes |> bars(1..=4) |> transpose(+12) (a source, then steps joined by |>). Sources: another midi-clip's notes (name.notes), a literal list of entries, or editable { notes 1:0:1:60:100 ... } for a piece that is edited by hand. Operators: bars, beats and time (a..b) keep a range and move it to the start (half-open: bars(1..5) is four bars, bars(1..=4) includes bar 4; a fractional bar only gives a warning; time takes s or ms and uses the song tempo), mask(range), remove(range), cut(range), replace(range, other.notes), transpose(+12), shift(+250ms) (a length needs a unit: bars, beats, s, ms or ticks), concat(other.notes), merge(other.notes), reverse(), stretch(2), legato(), quantize(0.25beat), humanize(timing=20ms, velocity=4, seed=7) (a seed is required), repeat(4), arp(0.5beat), where(pitch=48..72), set(pitch=\"38 50 45\", velocity=\"105 82\") (any setting of a note, as a value or a list that cycles over the notes), and euclid(5, 8, pitch=36, step=0.5beat) as a source; arguments may be named. A midi block is a source too (song.notes). Randomness is always seeded and reproducible: seed=stable(\"verse\") follows the song master-seed, seed=stable(\"verse\").fork(\"drums\") is a branch, seed=123456 is pinned; random(0..100, seed=S, round=0), choose(a, b, c, seed=S, round=1) and chance(0.25, seed=S, round=2) are values, per-note operators (humanize, randomize(velocity=70..=95), thin(0.25), scramble) draw per note, walk(C, major, steps=16, seed=S) makes a melody; random-stream NAME SEED is a clip line with a cursor read as NAME.next() (never inline); seed=randomSeed() must be picked first (the editor writes the number). Generators: chords(\"Am F C G\"), scale(C, major); helpers: clamp(48..=72), fold(48..=71), snap(C, major), keep(C, major), avoid(C, major). The language also has helpers: a top-level helper canonVoice(Notes source, Interval by) -> Notes { ... } in lowerCamelCase (any order in the file), let constants, if / else if / else, for i in 0..n, repeat N, while cond max N [stop], return, emit (Notes only; a bare return ends early), bake EXPR as VALUE [@hash] [override], and numeric block properties may be expressions (gain quiet(theme.notes), program pickProgram(5)). A helper that reads currentTime(), currentDate(), osRandom() or environment(name) must be declared dynamic helper (an unmarked one is an error); a value made as an Octave is not a Program. Pitfalls: a division gives a Float, and a Float is not an Int (a fork label, a loop count, a program number): write Floor(a / b), Round(a / b) or Int(a / b) (there is no .floor() method), or use %; two statements on one line need a \";\" between them; a seed derived from the clock is a dynamic helper returning a RandomSource, for example dynamic helper sectionSeed(RandomSource s) -> RandomSource { return s.fork(Floor(currentTime().minute / 4)) }, and a stable(\"name\") inside a helper is shared by every call, so pass the seed in as a parameter. Types: Int, Float, Notes, Interval (+7st, -1oct), Duration (0.5beat, 250ms), PitchClass (C, F#), ScaleMode (minor), Octave, Velocity, Pitch (C4), Probability (25%), RandomSource. A hyphen inside an expression is part of a name: write a - b or (a-b) to subtract and [a-b] for the symbol. Capabilities (clock, os-random, environment, network, filesystem, external-process, external-plugin) are permissions set in settings lines and by the server: NEVER write capabilities.<name> on, NEVER remove or weaken a capabilities.<name> off line and NEVER raise a limit (limits.fuel and the other limits); the server refuses such a draft for good. Explain a denied capability to the user instead of working around it. A line may continue on the next line starting with |>, and # starts a comment (/* ... */ is a block comment, over several lines too). Write a doc comment above every helper, let constant or block you add: /** ... */ (or lines that start with ##) directly above it, with a summary and the tags @param name text (or @param {Interval|Int} name text with a type or a union), @return {Notes} text, @example code and @deprecated text; the editor shows it when the name is hovered. A part may say at 1 for auto (the length of the notes).",
            "|> bake name { ... } stores the notes made so far, signed: input-hash and notes-hash are written by AMPLE (the Bake item of the roll menu, the bake button of the error tooltip, or ample bake-notes), never by hand. Do not edit the notes of a bake by hand: change the pipeline and bake again, or turn the pipeline into plain notes. Notes inside editable { } may be edited freely. The roll shows a pipeline read-only."),
        (["pitch bend", "bend", "pitch wheel", "pitch curve", "pitch range", "glide", "riser", "tape stop", "pitch up", "pitch down", "dive", "whammy", "pitch automation"], "Pitch bend and pitch curves",
            "Three ways, pick by what should bend. (1) The notes of a midi-clip, like a pitch wheel: write bend auto(1:0, 2:100, 3:0) on the midi-clip (values are percent of bend_range; 100 is all the way up, -100 all the way down) and bend_range 2 (the usual wheel; 7, 12 for an octave, 24); the block that plays the notes (synth, sampler, piano, gm... every instrument) bends its sound with it. A whole chord under it bends together. " +
            "(2) One note: add ;bend=<cents> to that note in the notes text (1:0:2:60:100;bend=200 glides two semitones up over the note, ;bend=-1200 dives an octave); it also works in the notes rows of a sequencer channel. On a single line this is exactly that note. " +
            "(3) Any audio (a sample, a recording, a loop, a whole stem): a pitch block after it with semitones auto(1:0, 4:12) (a riser over three bars; points may be in seconds, 2.5s) or with range 12 and values in percent (auto(1:0, 4:100)); method voice keeps the colour of a voice, the others move it like a tape; the length stays. A steady move is semitones 3. " +
            "In Jam the clip shows a pitch bend lane (click to draw the curve, right-click for the range) and B bends the selected notes; the pitch block draws its curve in the automation editor."),
        (["vocoder", "vocodex", "fruity vocoder", "talking synth", "talk box", "talkbox", "robot voice", "daft punk", "make a chord speak", "make the synth talk"], "Vocoder (a voice makes another sound speak)",
            "The vocoder block makes ONE sound speak with another: connect a voice (a recording, a sample, an ai-voice) to its modulator input and a rich sound (a chord from a gm sawtooth lead or strings, a pad, a Delay Llama) to its carrier input: from voice.audio -> modulator, from chord.audio -> carrier. Its output is the carrier, speaking. bands (4-40, more is clearer), release (short is crisp, long is smooth), sibilance (noise that keeps s and t clear), shift (carrier bands in semitones: up is a smaller robot), mix and gain; the level is matched to the carrier. The carrier needs many harmonics (a sine hardly speaks). For the Daft Punk sound use a sawtooth-like lead with chords under a clear voice. A robot voice with a fixed pitch is autotune with speed 0 ms instead."),
        (["chop", "chop the voice", "slice", "slicer", "stutter", "chopped vocal", "rhythmic vocal", "work it harder", "make it faster", "words on the beat", "cut the words", "sample chop"], "Chop (a recording cut into slices on the beat)",
            "The chop block cuts ONE recording (for example a spoken line) into slices and puts them on the beat in the order of a pattern: chop words { slices auto  pattern \"1 2 3 4 1 2 3*2 4*2\"  step 1  repeat 4  from voice.audio -> audio }. slices auto finds the words from the level of the sound (sensitivity, gap in ms: a silence shorter than that does not split a word), or give start times in seconds (slices 0.3 1.1 1.9 2.8). The pattern is steps separated by spaces: a number is a slice (1 is the first), - is a rest, 3*2 plays slice 3 twice inside its step (a stutter). step is the length of a step in beats, gate how much of it sounds, fit how much a slice is sped up or slowed down to fill its step (the pitch changes), repeat how many times the whole pattern plays; the block is as long as steps x step x repeat. " +
            "For a rhythmic robot voice put a vocoder after it: chop -> vocoder.modulator, a gm sawtooth lead playing a short melody -> vocoder.carrier. The recording should have a clear pause between the phrases so that auto finds them; the window of the block (Advanced) shows the cuts, a click adds one and a right-click takes one away. Record the phrases with the user's own voice or use a CC0 sample; never use a sample of a commercial song."),
        (["autotune", "auto-tune", "auto tune", "pitch correction", "pitch-correct", "correct the pitch", "out of tune", "off key", "off-key", "in tune", "tune the voice", "tune the vocal", "t-pain", "robot voice", "melodyne"], "Autotune (the autotune block)",
            "The autotune block pulls ONE voice (or one monophonic instrument) to the right notes; it does not work on a choir, chords or a whole mix. Every autotune block states its method (required, AMP2016): method spectral (keeps the colour of the voice, the default and usually the best) or method psola (tighter, drier). Put it after the voice: from voice.audio -> audio (an ai-voice, ace-music vocal stem, import-audio of a recording). key is auto (found from what is sung), chromatic, or a root and a scale (A minor, F# major, D dorian, C pentatonic). speed is how fast the voice is pulled to a new note: 0 ms is the hard robot sound, 20-50 ms a clear correction, 100-200 ms a soft natural one; amount 50% moves it half way; humanize keeps slides and note starts; vibrato keep or flatten. " +
            "To make the voice follow a melody exactly, connect notes to its Melody input: from melody.notes -> target (a midi-clip, or a hum turned into MIDI); while a note plays the voice goes to it in the octave closest to what is sung, between notes the key decides. Single notes are fixed by hand with fixes: 4.2s-4.7s E4; 7.9s-8.4s G4 vibrato 0%; 12s-13s off (times in the time of the block). The window of the block (Advanced) shows the sung pitch and the notes and has a live preview."),
        (["rewind", "pull up", "pull-up", "pullup", "scratch back", "record back", "turntable stop", "tape stop", "record stop", "spin back", "back in time", "go back"], "A DJ rewind (the rewind block)",
            "A rewind is the music stopping, a scratch being heard alone, and the music going on from an earlier (or later) place of the same sound. Use the rewind block, not scratch (scratch only moves the record per step of a pattern). Put it after the sound (from sound.audio -> audio), then write its events parameter in the time of the block: at 6s scratch 2s back 5s; at 14s scratch 1s back 2s. A time is seconds (6s), milliseconds (500ms) or decimal bars from the start of the block (4.5); back is how far the music jumps back after the scratch (a negative back jumps forward). Read the length of the sample with ample_analyze_audio first and keep every event inside it; a jump back never goes before the start, and past the end the end parameter decides (stop or loop). For the best sound add a real scratch: ample_find_samples text=dj scratch, ample_fetch_sample, ample_copy_file source=workspace/assets/web/<file> to samples/<file> (the file is not in the project until you do; without it the build fails with AMP5350), an import-audio block with file samples/<file> and a connection from it to the fx input of rewind (sound auto then uses it; without one the scratch is made from the music itself). smear (ms) is the record kicking into gear after the scratch, brake (ms) the slowing down before it; hardness and blend (ms) soften the cuts; the region output carries only the scratches. To put an effect on the scratches ALONE (the output titled Scratches, port name region, a region and not audio): connect rewind.region -> effect.region_in (the input titled Region in; the effect then works on that region and leaves its audio input empty), the effect.region output (titled Loop seam) -> a region-merge block input region, and rewind.audio -> that region-merge input body; the region-merge out is the song again, with the effect only on the scratches. Never connect rewind.audio to the effect for this, and never leave rewind.region unconnected."),
        (["scratch", "dj scratch", "record scratch", "turntable", "vinyl", "foley", "sound effect", "real sample", "real recording", "cc0", "royalty", "licence", "license", "where can i find", "free sample", "download a sample", "riser", "impact"], "Real samples from a legal source",
            "When a sound cannot be made well with AMPLE's own blocks (a real DJ scratch, a vinyl crackle, a foley sound), do not fake it and do not invent a source: call ample_sample_sources (text=dj scratch) and give the owner the place, the search words and the licence filter. Only CC0 or public domain is used by default; CC BY only with the credit written down; never an unclear, non-commercial or 'royalty free' file without a licence text. Fetch it yourself: first ample_find_samples text=dj scratch (it searches Freesound CC0 and the Internet Archive by itself, without a browser or a sign-in, and checks every licence), then ample_fetch_sample page=<page> file=<file> title=<name> with one candidate (freesound.org, archive.org or opengameart.org; AMPLE reads the licence itself and only lets CC0 or public domain through, so do not ask the owner for the file). AudioHero only when the owner names it (ample_fetch_audio_previews; only the approved one is bought, never without asking). Bring it in with ample_sample_set_add after ample_analyze_audio, and write the sourceNote text of the answer as SOURCE.md next to it. Try without a sign-in first. If the page or the file needs a sign-in, ample_browser_open the sign-in page (AMPLE shows its browser window by itself), ask the owner to sign in there and say when it is done, then ample_fetch_sample with browser=true. Do not browse for the link by hand and do not use a pack page: ample_find_samples gives the page of one single sound and its preview file. If you built an effect-based stand-in (reverse, pitch-stretch, gate, distortion), say plainly that it is a stand-in and that a real recording is the proper answer."),
        (["beat", "beats", "groove", "rhythm", "drum pattern", "drums", "background", "bed", "genre", "style", "rap", "country", "singer", "songwriter", "lullaby", "ballad", "tempo", "bpm", "fill", "arrangement", "which instruments"], "Finding a beat (the beats catalogue)",
            "Do not invent a drum pattern from nothing: ample_beats is a catalogue of 160+ original, free-to-use grooves. It is big, so explore in small steps. Start with overview=true (counts per family, genre, mood, form, novelty), then search: genre=rap, family=country, mood=tender, form=loop|phrase|arrangement (a loop is one bar, a phrase 2-4 bars with a fill, an arrangement a LONG 8-32 bar form with sections), bpm=88 (it also finds half- and double-time fits), maxNovelty=2 for a safe background bed (1 = common, 5 = rare, use sparingly), energy=1-5, text=words. Answers are short summaries: use limit and offset to page, similar=<id> for neighbours, and queries=[{...},{...}] to run up to 8 searches in ONE call. " +
            "Then read only what you need of one entry: id=<id> with parts=say,drums,tips,howToBringIn,pairsWith,sections. say is the human form of the rhythm (BOOM . clap .), tips and howToBringIn say how to play it and when to bring it in, fast and slow say what changes at other tempos, pairsWith names the KINDS of instruments that suit it (not just guitar: which kind, GM program, role and register) and avoid says what to leave out. For a sung song pick novelty 1-2 and energy 1-2 and keep the drums under the voice (tag under-vocals). " +
            "drums.grid and drums.patterns/song can be pasted into a drums block with a kit (16 steps per bar; kit=samples/<folder>; patterns and song for named sections); a voice the kit does not have needs map= or fallback= (see the sample drums docs). The grids are starting points: set the bpm of the song to the entry's typical tempo, then listen with ample_listen and adjust."),
        (["analyse", "analyze", "analysis", "key of", "what key", "which key", "pitch", "root note", "tempo of", "how many bars", "loop ready", "fits the song", "check the arrangement", "arrangement", "structure", "build up", "lint", "mixing", "levels", "volume balance", "panning", "equalizer", "generate", "arpeggio", "bassline", "bass line", "euclid", "euclidean", "drum fill", "starting levels"], "Analysing a sample, checking the arrangement, mixing and generating patterns (no AI tools)",
            "Four small deterministic tools do work you should not guess. ample_analyze_audio source=<file or workspace/<preview>> tells you the length, level, brightness, the pitch of a single note (and the file name it should get in a sample set), the likely key, the tempo, the number of bars and whether it is a loop of whole bars, each with a confidence; use it on an AudioHero preview before you place it (set the song tempo or stretch it, name or transpose it, cut the part you want). The values are estimates: trust pitch above 0.85, tempo above 0.5, key above 0.3, otherwise listen. " +
            "ample_arrangement_lint path=<song> reads only the placements and gives the sections (which blocks play in which bars), a density digit per bar and findings: silent bars, harmony without a bass, nothing that builds, the same layers for 12+ bars, every loop starting at bar 1 with the same length, too many layers at once. Run it after you have built the structure, then listen with ample_listen before changing the song for a hint. " +
            "ample_mix_hints instruments=kick,bass,piano,pad,vocal-lead genre=techno gives first volume, pan, highpass, EQ, compression and sends per instrument as the parameters of AMPLE's own blocks, the master (limiter ceiling -1 dB, about -6 dB peak before it, a loudness target per genre), a duck setting for genres that pump and the order to mix in: starting points to set, then adjust by ear with ample_listen. " +
            "ample_generate makes patterns from a seed: kind=euclid (steps, hits, rotate), groove (style, variation, swing: a drums grid to paste), arpeggio and bassline (a progression such as Am F C G gives melody text for a midi-clip, one chord per bar; a melody text may carry dynamics: pp p mp mf f ff, and < or > for a crescendo or decrescendo to the next marker), fill (a grid for the last bar of a section; pass voices=<what the chosen kit has>, a voice without a sample in the kit fails the build with AMP5350). The same query and seed give the same result."),
        (["formula", "formulas", "compress", "shorter", "mathematical", "pattern finder", "repeat", "cycle", "set(", "replace notes", "helpers"], "Writing notes as a formula and proving that nothing changed",
            "A midi-clip or a sequencer row can be a FORMULA instead of a list of notes: a pipeline that is worked out. The most useful shape is a rhythm that repeats, written once as note entries, plus values that cycle over it: 1:0:0.5:60:100 1:0.75:0.25:60:100 1:1.5:0.5:60:100 |> bars(1..=1) |> repeat(32) |> set(pitch=\"38 43 45 38\", velocity=\"105 82 91\"). set(...) puts one value, or a quoted list that cycles over the notes in order (start, then pitch), on pitch (a number or a name like C4), velocity, pan, fine, rel, cut, slide, port, bend, channel, bank and program: every setting of a note can be written with it. A row of a sequencer pattern is a formula when it starts with = (its bar 1 is the first bar of the pattern); in a midi-clip it is the value of notes. bars(1..=p) after a rhythm of p bars fixes its length, so repeat(m) puts the copies exactly p bars apart. " +
            "To write notes shorter: ample_find_patterns block=<midi-clip or sequencer> [channel=<name>] finds the rhythm that repeats and the values that cycle and gives the formula; every candidate was already worked out and compared with the original (verified=true: every note, length and setting is the same). ample_resolve_notes block=... compare_block=... (or expression= / compare_expression=) says whether two things are EXACTLY the same: use it as the check before any replacement by a formula and replace only when compare.same is true; keep the original as a temporary clip while you check a helper-based formula (the expression of ample_resolve_notes can also call the helpers declared in the song). ample_import_score ... compress=true writes the rows of an imported score as verified formulas when that is much shorter, and always plays a repeated pattern again from the first one (a similar pattern is a variation that lists only what differs). If nothing shorter is found, say so: the music is not mathematical, and the notes stay as they are."),
        (["component", "components", "amplec", "reusable block", "custom block", "import", "reexport", "seal", "sealed", "unsealed", "migrate", "migration", "deprecated", "semver", "canonical id", "namespace"], "Components: reusable typed blocks made of Notes code",
            "A COMPONENT is a reusable, typed block declared in the song or in a .amplec file: component koda.notes.tripleNotes version 1.0.0 { input source: Notes { optional true  fallback localNotes }  property localNotes: Notes { editor jam }  #[MinValue(1), MaxValue(16)] property copies: Int = 3  output notes: Notes  process { for i in 0..copies { emit source |> shift(i * 1bar) } } }. The process is the body of a helper (emit the notes); connected(source) says whether an optional input has a connection; a condition is read by truthiness and cond ? a : b exists. " +
            "An INSTANCE is a block of that type: koda.notes.tripleNotes intro at 2 for 3 { copies 3  from theme.notes -> source }. After the name an instance may carry a version request (pinned@1.8, pinned@^1.2); without it the instance follows the current version, else the highest release, never a pre-release by surprise. Read the output as from intro.notes -> x. Properties are typed (docs/language/15) and checked against the contract (AMP2218). Import files with import \"fx.amplec\" [as alias]; reexport a.b as c.d. " +
            "NEVER change a sealed component (it has a seal block): a sealed version is immutable, so make a new version instead (copy the whole declaration with a higher version number and without its seal block) and change that copy. NEVER write a seal block, an id of a component that is not yours, or a version of an existing component by hand; the engine writes seals. The semantic hash of every component a song uses is pinned in ample.lock: if the code of a component changes, the song gets AMP2203 until the person accepts the change, so tell the person when you change the code of a component, and prefer a new version. Descriptions, DisplayName and KodaFact never change what a component makes. Attributes are only the built-in ones (MinValue, MaxValue, Step, Options, Editor, DisplayName, Description, Category, Experimental, KodaFact) and are written as #[Name(args)] above the declaration. ample_components lists what the song can see with every version, its seal and signature."),
        (["component library", "find a component", "search components", "import a component", "alias", "tags", "which components", "not imported"], "Finding, importing and naming components",
            "The LIBRARY is every component of every .amplec file of the project, also the ones the song does not import. ample_component_library searches it (query words, tag, imported=yes|no) and says for each match whether the song imports it, which import line to write and which block type to use. Use it BEFORE you write a new component (one may exist), when asked what components there are, which are imported and which are not, and to find a component by what it does (#tags, description)." +
            " To use a component that is not imported yet, add its importLine to the song (after the project line, with the other imports) and write the instance with its instanceType. When nameTaken is true another component with that name is already visible and the importLine carries an alias that was checked to work (import \"file.amplec\" as alias: the first segment of the name becomes the alias). Propose an alias on your own when two files export the same name, and say why. A name that is taken without an alias is AMP2213: never let the last import win. When you make a component, give it a layout: ranges on the number properties (#[MinValue(0%), MaxValue(90%)]) and a ui block with the 3 to 6 controls people touch (ui { row { knob feedback tint red  knob room }  field delayTime  card { knob feedback size s }  preview live }); widgets are knob, slider, toggle, choice, xy, readout, meter, jam, curve, field; give a property a friendly name with #[DisplayName(\"Echo\")] and tint a widget or a group with tint <colour> (accent, green, yellow, orange, red, pink, purple, blue, teal, gray); a knob or slider needs a number property, a toggle a Bool, a choice Options. preview live is only for audio components made of eq, filter, compressor, distortion, delay, reverb, chorus and gain processors (a sidechain key or a limiter cannot run live). The layout never changes what the component makes." +
            " To make a component, write a .amplec file in a folder like components/ (one file can hold several), then import it. Give it an id, a #[DisplayName(\"Friendly name\")], a one-sentence description (a doc comment above the attributes, or #[Description(\"...\")]) and #[Tags(\"echo\", \"space\")] with a few lower-case words people would search for. Tags, names and descriptions never change what the component makes. Prefer one sound-describing tag over many. When asked to find a component, show its friendly name, kind (notes or audio), whether it is imported, and its tags." +
            " A component can call other components: a Notes process calls a Notes component like a function (emit koda.notes.echoNotes(koda.notes.octaveLayers(source, layers=2), plays=3): the first unnamed value is the first input, the rest is given by name), an audio process calls an audio component as a processor (audio |> koda.fx.dreamEcho(feedback=20%)). Put import lines for the called files at the top of the component file. Components never call themselves, directly or through each other (A calls B and B calls A is AMP2247), and are nested at most four deep. A call takes the default version of the called component; write name@1.2(...) to pin it."),
        (["audio component", "audio process", "audio graph", "processor", "wet dry", "sidechain", "dreamEcho", "emit audio"], "Audio components: reusable effect graphs",
            "A component can take NOTES IN and give SOUND OUT (an instrument with its effects, like a Delay Llama chain): input notes: Notes, output audio: Audio, process { emit notes |> piano() |> reverb(size=room) } with an instrument (piano, bass, guitar, synth, gm, sampler, delay-llama) between the notes and the effects; never say a component cannot have a Notes input and an Audio output. A Notes OUTPUT next to Audio ports is the one thing refused. The instance may have at/for like a clip, else it plays where the clip it reads plays; it is wired from melody.notes -> notes. An AUDIO COMPONENT has Audio ports (input audio: Audio, output audio: Audio) and a process that is a graph of audio processors, not Notes code: process { let wet = audio |> delay(time=delayTime, feedback=feedback) |> reverb(room=room)  emit mix(audio |> gain(-3dB), wet |> gain(-8dB)) }. The processors are the effect blocks (delay, reverb, chorus, compressor, eq, filter, limiter, pan, ...; gain is the volume block; mix is a bus): ONE definition, so audio |> delay(...) and a delay block are the same thing. Arguments are named and typed by the unit of the block parameter: a time (250ms, 0.25beat), a Percent (35%), a gain (-12dB), a frequency (80Hz); arithmetic over the properties is allowed (space * 0.5). A second Audio input is wired by name: compressor(threshold=-12dB, key=sidechain). Names are read top to bottom, so a graph cannot loop. At most 64 processors. An instance is wired like any block: from vocals.audio -> audio, and the next block reads from vocalFx.audio. The build makes ordinary blocks named <instance>__<processor><n> (delay1, reverb1). Use ample_components to see the Kind (Audio, Notes), the processors, the latency and whether the browser can preview it. Never edit a sealed Audio component: make a new version."),
        (["effect on", "add effect", "chain", "send", "reverb", "chorus", "delay", "insert", "connect"], "Adding an effect to a sound",
            "An effect is a block that takes audio in and gives audio out: put it between the sound and what it fed (from sound.audio -> audio on the effect; the next block reads from the effect's out). ample_edit connect and disconnect change the lines; ample_lookup topic=impact name=<block> shows what a block feeds. " +
            "An input takes exactly ONE source, so an effect cannot be hung next to a sound that already feeds something: the output it would read is taken, and a second line into the same input is refused. Insert it IN the chain instead: (1) ample_lookup topic=impact name=<sound> to see which block reads the sound and from which output; (2) add the effect, reading from the sound (from sound.<out> -> <in> inside the effect); (3) change the block that read the sound so it reads from the effect (disconnect the old line, connect from effect.<out>). Do this for one sound at a time and run ample_check after each. " +
            "A wet/dry or parallel effect (the dry sound and the effect together) needs a mixer with a free input: feed one input from the sound and another from the effect. If a placement is not possible, say so plainly and leave it out; do not remove other blocks to make room. " +
            "In 'text' of ample_edit (add_block, insert, create) write real line breaks (in JSON a single \\n), never the two characters backslash and n twice."),
    ];

    private static JsonObject HowTo(string query)
    {
        string q = (query ?? string.Empty).Trim().ToLowerInvariant();
        JsonArray found = [.. HowTos.Where(h => q.Length == 0 || h.Words.Any(w => q.Contains(w, StringComparison.Ordinal) || w.Contains(q, StringComparison.Ordinal))).Select(h => (JsonNode)new JsonObject { ["about"] = h.Title, ["howto"] = h.Text })];
        return new JsonObject
        {
            ["found"] = found.Count > 0,
            ["howtos"] = found,
            ["note"] = found.Count == 0 ? "Nothing matches that word. Try: automation, fades, speed, sections, sidechain, effect. Or ample_lookup topic=search." : "When something is not possible directly, do the closest workable thing and say plainly what it is and what is different, instead of giving up.",
        };
    }

    private static JsonObject Diagnostics(ProjectSession s, string what)
    {
        string file = Path.Combine(s.AmpleRoot, "spec", "diagnostics.json");
        if (!File.Exists(file) || string.IsNullOrWhiteSpace(what))
        {
            return new JsonObject { ["found"] = false, ["note"] = "Give an AMP code (AMP5371) or a word." };
        }

        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
        JsonArray hits = [];
        foreach (System.Text.Json.JsonElement d in doc.RootElement.GetProperty("diagnostics").EnumerateArray())
        {
            string code = d.TryGetProperty("code", out System.Text.Json.JsonElement c) ? c.GetString() ?? string.Empty : string.Empty;
            string text = (d.TryGetProperty("title", out System.Text.Json.JsonElement t) ? t.GetString() : string.Empty) + " " + (d.TryGetProperty("explanation", out System.Text.Json.JsonElement e) ? e.GetString() : string.Empty);
            if (code.Equals(what, StringComparison.OrdinalIgnoreCase) || (!what.StartsWith("AMP", StringComparison.OrdinalIgnoreCase) && text.Contains(what, StringComparison.OrdinalIgnoreCase)))
            {
                hits.Add(new JsonObject { ["code"] = code, ["title"] = d.TryGetProperty("title", out System.Text.Json.JsonElement t2) ? t2.GetString() : null, ["explanation"] = d.TryGetProperty("explanation", out System.Text.Json.JsonElement e2) ? e2.GetString() : null, ["wrong"] = d.TryGetProperty("wrongExample", out System.Text.Json.JsonElement w) ? w.GetString() : null, ["right"] = d.TryGetProperty("rightExample", out System.Text.Json.JsonElement r) ? r.GetString() : null });
            }

            if (hits.Count >= 8)
            {
                break;
            }
        }

        return new JsonObject { ["query"] = what, ["found"] = hits.Count > 0, ["diagnostics"] = hits };
    }

    private static HashSet<string> Walk(Dictionary<string, BlockNode> by, string start, bool forward)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        Stack<string> todo = new([start]);
        while (todo.Count > 0)
        {
            string current = todo.Pop();
            IEnumerable<string> next = forward ? by.Values.Where(b => b.Froms.Any(f => f.Block == current)).Select(b => b.Name) : (by.TryGetValue(current, out BlockNode? b) ? b.Froms.Select(f => f.Block) : []);
            foreach (string n in next)
            {
                if (n != start && seen.Add(n))
                {
                    todo.Push(n);
                }
            }
        }

        return seen;
    }

    private static JsonObject Search(ProjectSession s, string query)
    {
        string q = query.Trim();
        if (q.Length < 2)
        {
            return new JsonObject { ["found"] = false, ["note"] = "Give a word of at least two letters." };
        }

        JsonArray hits = [];
        foreach (TemplateSpec t in s.Spec.Templates.Values.OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            if (t.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || t.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || (t.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                hits.Add(new JsonObject { ["kind"] = "block type", ["id"] = t.Id, ["what"] = t.Description });
            }

            foreach (TemplateParameterSpec p in t.ParameterSpecs.Values.Where(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || (p.Documentation?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)))
            {
                hits.Add(new JsonObject { ["kind"] = "parameter", ["id"] = $"{t.Id}.{p.Name}", ["what"] = p.Documentation, ["unit"] = p.Unit });
            }
        }

        foreach (var h in HowTos.Where(h => h.Words.Any(w => w.Contains(q, StringComparison.OrdinalIgnoreCase) || q.Contains(w, StringComparison.OrdinalIgnoreCase)) || h.Text.Contains(q, StringComparison.OrdinalIgnoreCase)))
        {
            hits.Add(new JsonObject { ["kind"] = "how-to", ["id"] = "ample_lookup topic=howto query=" + h.Words[0], ["what"] = h.Title });
        }

        string docs = Path.Combine(s.AmpleRoot, "docs");
        string languageDocs = Path.Combine(docs, "language");
        foreach (string f in Directory.Exists(docs) ? Directory.EnumerateFiles(docs, "*.md", SearchOption.TopDirectoryOnly).Concat(Directory.Exists(languageDocs) ? Directory.EnumerateFiles(languageDocs, "*.md") : []) : [])
        {
            string? line = File.ReadLines(f).FirstOrDefault(l => l.Contains(q, StringComparison.OrdinalIgnoreCase));
            if (line is not null)
            {
                hits.Add(new JsonObject { ["kind"] = "document", ["id"] = Path.GetRelativePath(docs, f).Replace('\\', '/')[..^3], ["what"] = line.Trim().Length > 200 ? line.Trim()[..200] + "…" : line.Trim() });
            }
        }

        return new JsonObject { ["query"] = q, ["found"] = hits.Count > 0, ["total"] = hits.Count, ["hits"] = new JsonArray([.. hits.Take(20).Select(h => h!.DeepClone())]), ["note"] = hits.Count > 20 ? "Only the first 20 are shown: use a more specific word." : "Read a document with ample_docs name=<id>, a block type with ample_block_types type=<id>." };
    }

    private static JsonObject BlockOf(AmpleDocument doc, BlockNode b)
    {
        List<string> notes = [];
        for (int i = b.Line - 1; i >= 0 && doc.Lines[i].TrimStart().StartsWith('#'); i--)
        {
            notes.Insert(0, doc.Lines[i].TrimStart().TrimStart('#').Trim());
        }

        return new JsonObject
        {
            ["name"] = b.Name,
            ["type"] = b.Type,
            ["version"] = b.Version,
            ["placed"] = new JsonArray([.. b.Parts.Select(p => (JsonNode)$"at {p.At} for {p.Length}")]),
            ["lines"] = $"{b.Line + 1}-{(b.CloseLine < 0 ? b.Line + 1 : b.CloseLine + 1)}",
            ["parameters"] = new JsonObject([.. b.Parameters.Select(p => KeyValuePair.Create(p.Name, (JsonNode?)(p.Value.Length > 300 ? p.Value[..300] + "…" : p.Value)))]),
            ["from"] = new JsonArray([.. b.Froms.Select(f => (JsonNode)$"{f.Block}.{f.RawOutput.Trim('"')} -> {f.RawInput.Trim('"')}{(f.Disabled ? " (disabled)" : string.Empty)}")]),
            ["comment"] = notes.Count == 0 ? null : string.Join('\n', notes),
            ["flags"] = new JsonArray([.. b.Flags.Select(f => (JsonNode)f)]),
            ["hint"] = b.Type == "delay-llama" && !b.Froms.Any(f => f.Input == "notes") ? "It has no notes: connect a melody (from melody.notes -> notes); it sings nothing without them." : b.Type == "autotune" && !b.Froms.Any(f => f.Input == "in") ? "It has no voice on its in input: connect one voice (from voice.audio -> audio); a melody goes to target, not to in." : b.Type == "rewind" && !doc.Blocks.Any(x => x.Froms.Any(f => f.Block == b.Name && f.Output == "region")) ? "Its Scratches output (port region, a region and not audio) is not connected to anything. An effect on the scratches alone is wired rewind.region -> effect.region_in, effect.region -> region-merge.region and rewind.audio -> region-merge.body; the effect gets no audio input." : null,
        };
    }

    /// <summary>A song: the project line, every block in short with how they are connected, and the problems the parser finds.</summary>
    public static async Task<JsonObject> SongAsync(ProjectSession s, string path, CancellationToken ct, string? proposal = null)
    {
        path = Norm(path);
        DocSession? d = proposal is null ? await s.DocAsync(path, ct).ConfigureAwait(false) : null;
        string shown = proposal ?? d!.Text;
        AmpleDocument doc = proposal is null ? s.ParseExpanded(d!) : new Ample.Engine.Modules.ModuleService(s.Root, () => s.Library, s.Manifest.Id.ToString("D")).ParseExpanded(proposal, s.Spec);
        return new JsonObject
        {
            ["path"] = path,
            ["project"] = doc.Project is null ? null : new JsonObject { ["name"] = doc.Project.Name, ["tempo"] = doc.Project.Tempo, ["masterSeed"] = doc.Project.MasterSeed.ToString(CultureInfo.InvariantCulture) },
            ["unsavedChanges"] = d is not null && d.Text != d.Saved,
            ["source"] = proposal is null ? "the current working copy of the project" : "your proposal (the files of your working folder)",
            ["blocks"] = new JsonArray([.. doc.Blocks.Where(b => !b.Skipped).Select(b => (JsonNode)BlockOf(doc, b))]),
            ["problems"] = new JsonArray([.. doc.Diagnostics.Where(x => x.Severity is Severity.Error or Severity.Warning).Take(40).Select(x => (JsonNode)$"line {x.Line}: {x.Severity.ToString().ToLowerInvariant()} {x.Code} {x.Message}")]),
            ["text"] = shown.Length > 80000 ? shown[..80000] + "\n… (cut)" : shown,
        };
    }

    /// <summary>One block with what feeds it and what it feeds.</summary>
    public static async Task<JsonObject> BlockAsync(ProjectSession s, string path, string name, CancellationToken ct, string? proposal = null)
    {
        path = Norm(path);
        AmpleDocument doc = proposal is null ? s.ParseExpanded(await s.DocAsync(path, ct).ConfigureAwait(false)) : new Ample.Engine.Modules.ModuleService(s.Root, () => s.Library, s.Manifest.Id.ToString("D")).ParseExpanded(proposal, s.Spec);
        BlockNode b = doc.Blocks.FirstOrDefault(x => !x.Skipped && x.Name == name) ?? throw new AmpleException("AMP5458", $"There is no block called {name} in {path}.");
        JsonObject o = BlockOf(doc, b);
        o["text"] = string.Join('\n', doc.Lines.Skip(b.Line).Take((b.CloseLine < 0 ? b.Line : b.CloseLine) - b.Line + 1));
        o["feeds"] = new JsonArray([.. doc.Blocks.Where(x => !x.Skipped && x.Froms.Any(f => f.Block == name)).Select(x => (JsonNode)$"{x.Name} ({x.Type})")]);
        o["fedBy"] = new JsonArray([.. b.Froms.Select(f => (JsonNode)f.Block).Distinct()]);
        return o;
    }

    private const int FileWindow = 80000;

    /// <summary>The text of a project or task-work text file, in a bounded window; never silently drops the rest, it says so and how to read on.</summary>
    public static JsonObject File_(ProjectSession s, string path) => File_(s, path, null, 0, "project");

    public static JsonObject File_(ProjectSession s, string path, int offset) => File_(s, path, null, offset, "project");

    public static JsonObject File_(ProjectSession s, string path, string? proposal, int offset, string source)
    {
        path = Norm(path);
        if (proposal is not null)
        {
            return Window(path, source, proposal, offset);
        }

        string full = ProjectPath.Resolve(s.Root, path);
        if (!TextExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) || path.StartsWith(".ample", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            throw new AmpleException("AMP5458", $"{path} is not a text file of the project that can be read.");
        }

        return Window(path, null, File.ReadAllText(full, Encoding.UTF8), offset);
    }

    private static JsonObject Window(string path, string? source, string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        int take = Math.Min(FileWindow, text.Length - offset);
        bool cutBefore = offset > 0, cutAfter = offset + take < text.Length;
        JsonObject o = new() { ["path"] = path, ["text"] = text.Substring(offset, take) };
        if (source is not null) o["source"] = source;
        if (cutBefore || cutAfter)
        {
            o["totalLength"] = text.Length;
            o["offset"] = offset;
            o["truncated"] = true;
            o["note"] = cutAfter
                ? $"Showing characters {offset}-{offset + take} of {text.Length}. Call ample_file again with offset={offset + take} to read on."
                : $"Showing characters {offset}-{offset + take} of {text.Length} (the end of the file).";
        }

        return o;
    }
}
