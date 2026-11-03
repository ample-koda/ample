using System.Text.RegularExpressions;
using Ample.Core.Abstractions;
using Ample.Language;

namespace Ample.Audio.Engine;

/// <summary>
/// What a drums block asks of its kit, checked before anything is rendered: a voice in a pattern (kick, snare, tom ...) that the kit has no sample for. At render time that is AMP5350
/// and the block fails; here it is a diagnostic on the line of the pattern, so the editors, the checks of an assistant and the build check of an accepted proposal all see it first.
/// </summary>
public static partial class DrumPreflight
{
    /// <summary>The code of the diagnostic.</summary>
    public const string Code = "AMP2088";

    [GeneratedRegex(@"(?<![\w-])([A-Za-z][\w-]*):(?=[xXo.1-9@])", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Lane();

    [GeneratedRegex(@"(?<![\w-])([A-Za-z][\w-]*)\s*=", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MappedVoice();

    /// <summary>The names of the voices a sample folder offers (the base names of its WAV files: kick_v2 and kick_l2 are kick), or null when that cannot be told (no folder, an SFZ set).</summary>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="folder">The folder as the block names it.</param>
    /// <returns>The voices.</returns>
    public static IReadOnlySet<string>? VoicesOf(string projectRoot, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || SampleSet.IsSfz(folder))
        {
            return null;
        }

        try
        {
            string full = SampleSet.ResolveFolder(projectRoot, null, folder);
            if (!Directory.Exists(full) || Directory.EnumerateFiles(full, "*.sfz").Any())
            {
                return null;
            }

            HashSet<string> voices = new(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.EnumerateFiles(full, "*.wav"))
            {
                voices.Add(SampleName.Parse(Path.GetFileNameWithoutExtension(file)).Base);
            }

            return voices.Count == 0 ? null : voices;
        }
        catch (Exception ex) when (ex is AmpleException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Finds the voices of the patterns of the drums blocks that their kit does not have.</summary>
    /// <param name="projectRoot">The project folder (null: nothing can be checked).</param>
    /// <param name="blocks">The blocks of the song.</param>
    /// <param name="lines">The lines of the song text.</param>
    /// <returns>The diagnostics (errors: the block would fail when it is built).</returns>
    public static IReadOnlyList<Diagnostic> Check(string? projectRoot, IReadOnlyList<BlockNode> blocks, IReadOnlyList<string> lines)
    {
        List<Diagnostic> found = [];
        if (projectRoot is null)
        {
            return found;
        }

        foreach (BlockNode block in blocks.Where(b => !b.Skipped && b.Type == "drums"))
        {
            string kit = Param(block, "kit")?.Value.Trim().Trim('"') ?? string.Empty;
            IReadOnlySet<string>? voices = VoicesOf(projectRoot, kit);
            if (voices is null)
            {
                continue;
            }

            string fallback = Param(block, "fallback")?.Value.Trim().Trim('"') ?? string.Empty;
            IReadOnlySet<string>? fallbackVoices = fallback.Length > 0 ? VoicesOf(projectRoot, fallback) : null;
            HashSet<string> mapped = new(StringComparer.OrdinalIgnoreCase);
            if (Param(block, "map") is { } map)
            {
                foreach (Match m in MappedVoice().Matches(map.Value))
                {
                    mapped.Add(m.Groups[1].Value);
                }
            }

            foreach (string name in new[] { "pattern", "patterns" })
            {
                if (Param(block, name) is not { } parameter)
                {
                    continue;
                }

                HashSet<string> reported = new(StringComparer.OrdinalIgnoreCase);
                foreach (Match lane in Lane().Matches(parameter.Value))
                {
                    string voice = lane.Groups[1].Value;
                    if (voices.Contains(voice) || mapped.Contains(voice) || (fallbackVoices?.Contains(voice) ?? false) || !reported.Add(voice))
                    {
                        continue;
                    }

                    (int line, int col) = Place(parameter, lane.Groups[1].Index, lines);
                    string has = string.Join(", ", voices.Order(StringComparer.OrdinalIgnoreCase));
                    found.Add(new Diagnostic(Code, Severity.Error, line + 1, col + 1, col + 1 + voice.Length,
                        $"The kit \"{kit}\" has no sample for \"{voice}\". It has: {has}.",
                        "Use one of those voices, map this voice to a sample of another kit (map " + voice + "=other-kit/" + voice + "), or give the block a fallback kit that has it. Otherwise the block fails when it is built.",
                        null, null));
                }
            }
        }

        return found;
    }

    private static Parameter? Param(BlockNode block, string name) => block.Parameters.FirstOrDefault(p => p.Name == name);

    /// <summary>The line and column (0-based) of an offset inside the value of a parameter (a value that spans several lines is counted by its own breaks).</summary>
    private static (int Line, int Col) Place(Parameter parameter, int offset, IReadOnlyList<string> lines)
    {
        string before = parameter.Value[..Math.Min(offset, parameter.Value.Length)];
        int breaks = before.Count(c => c == '\n');
        if (breaks > 0) return (parameter.Line + breaks, offset - before.LastIndexOf('\n') - 1);
        string raw = parameter.Line < lines.Count ? lines[parameter.Line] : string.Empty;
        int at = parameter.ValueColumn > 0 ? parameter.ValueColumn : raw.IndexOf(parameter.Value, Math.Max(0, raw.IndexOf(parameter.Name, StringComparison.Ordinal)) + parameter.Name.Length, StringComparison.Ordinal);
        return (parameter.Line, Math.Max(0, at) + offset);
    }
}
