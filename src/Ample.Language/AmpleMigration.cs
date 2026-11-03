using System.Globalization;
using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>What a migration would do to a song, worked out without writing anything (the impact the author sees before agreeing).</summary>
/// <param name="From">The version the song is written for (the current one when it says nothing).</param>
/// <param name="To">The version it is moved to.</param>
/// <param name="Steps">The upgrade steps that would run, in order.</param>
/// <param name="Text">The migrated text.</param>
/// <param name="Changes">The lines that differ, as before and after.</param>
public sealed record SongMigrationPlan(int From, int To, IReadOnlyList<UpgradeStep> Steps, string Text, IReadOnlyList<LineChange> Changes)
{
    /// <summary>Gets a value indicating whether the migration changes the text at all.</summary>
    public bool Changed => Changes.Count > 0;
}

/// <summary>One line of a migration: 1-based line number of the old text, the old line and the new one (empty when the line is added or removed).</summary>
/// <param name="Line">1-based line in the old text.</param>
/// <param name="Before">The old line, or empty for an added line.</param>
/// <param name="After">The new line, or empty for a removed line.</param>
public sealed record LineChange(int Line, string Before, string After);

/// <summary>
/// Moves a song to the current AMPLE version, on the author's initiative: the same steps as the in-memory upgrade, but the result is meant to be written.
/// <see cref="Plan"/> only works it out; the caller shows <see cref="SongMigrationPlan.Changes"/> and writes <see cref="SongMigrationPlan.Text"/> when the author agrees.
/// </summary>
public static partial class AmpleMigration
{
    /// <summary>Works out the migration of <paramref name="text"/> to the current version.</summary>
    /// <param name="text">The song.</param>
    /// <param name="spec">The language data.</param>
    /// <param name="steps">The steps to use (the registered ones when null).</param>
    /// <returns>The plan; nothing is written.</returns>
    /// <param name="components">The components the song imports, so the ports of component instances are renamed too (null: only what the song itself declares is seen).</param>
    public static SongMigrationPlan Plan(string text, SpecData spec, IReadOnlyList<UpgradeStep>? steps = null, IReadOnlyList<ComponentDefinition>? components = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(spec);
        string normal = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        AmpleDocument doc = Parser.Parse(normal, spec, new ParseOptions { RequireProjectLine = false });
        int from = doc.Project?.Ample ?? AmpleVersions.Current;
        (string upgraded, IReadOnlyList<UpgradeStep> applied) = AmpleVersions.Upgrade(ConvertPreRelease(normal, spec, components), from, steps);
        string stamped = Stamp(upgraded);
        return new SongMigrationPlan(from, AmpleVersions.Current, applied, stamped, Diff(normal, stamped));
    }

    /// <summary>Writes the current version into the composition line (adds it, or replaces an older number); text without such a line is returned as it is.</summary>
    public static string Stamp(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string current = AmpleVersions.Current.ToString(CultureInfo.InvariantCulture);
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            Match m = SongLine().Match(lines[i]);
            if (!m.Success) continue;
            lines[i] = m.Groups["ample"].Success
                ? string.Concat(lines[i].AsSpan(0, m.Groups["ample"].Index), current, lines[i].AsSpan(m.Groups["ample"].Index + m.Groups["ample"].Length))
                : lines[i].Insert(m.Groups["brace"].Index + m.Groups["brace"].Length, $" ample {current} ");
            break;
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Converts the pre-release form into the v1 form, keeping the sound: the song line says <c>composition</c> instead of <c>project</c>, and a block header drops its class and number
    /// (<c>stable@1.1</c>, <c>live@0.4</c>, a bare <c>stable</c>). <c>pinned@...</c> stays. A component that asked for a version (<c>stable@^1.2</c>) keeps that request as <c>pinned@^1.2</c>.
    /// Text that is already in the v1 form comes back unchanged.
    /// </summary>
    /// <param name="text">The song, with LF line ends.</param>
    /// <param name="spec">The language data (which block types are built in).</param>
    /// <param name="components">The components the song imports (the host resolves them); with them the connections of component instances are seen and renamed too.</param>
    public static string ConvertPreRelease(string text, SpecData spec, IReadOnlyList<ComponentDefinition>? components = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(spec);
        AmpleDocument doc = Parser.Parse(text, spec, new ParseOptions { RequireProjectLine = false });
        string[] lines = text.Split('\n');
        foreach (BlockNode b in doc.Blocks)
        {
            if (b.Line < 0 || b.Line >= lines.Length) continue;
            Match m = OldHeader().Match(lines[b.Line]);
            if (!m.Success || m.Groups["class"].Value == "pinned") continue;
            bool builtIn = spec.Templates.TryGetValue(b.Type, out TemplateSpec? t) && !t.Component;
            string version = m.Groups["version"].Value;
            string keep = builtIn || version.Length == 0 ? string.Empty : " pinned@" + version;
            lines[b.Line] = string.Concat(m.Groups["head"].Value, keep, lines[b.Line].AsSpan(m.Length));
        }

        return RenamePorts(OldSongWord().Replace(string.Join('\n', lines), "composition", 1), spec, components);
    }

    /// <summary>
    /// Renames the old internal port names of the connections (in, out, mix, in3, render_in) to the names of the streams they carry, one name per round,
    /// with the quick fix of lint L010 (which is off by default, so it is switched on for the check by a lint line that is not written). Public because the expanded text of modules and components goes through it too: the expanders write the internal names.
    /// </summary>
    public static string RenamePorts(string text, SpecData spec, IReadOnlyList<ComponentDefinition>? components = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(spec);
        const string Probe = "lint { L010 warning }";
        for (int round = 0; round < 500; round++)
        {
            List<string> lines = [.. text.Split('\n')];
            int song = lines.FindIndex(l => SongLine().IsMatch(l) || OldSongLine().IsMatch(l));
            int probeAt = song + 1;
            lines.Insert(probeAt, Probe);
            AmpleDocument doc = Parser.Parse(string.Join('\n', lines), spec, components is null ? new ParseOptions { RequireProjectLine = false } : new ParseOptions { RequireProjectLine = false, Components = components });
            // one fix per line and round (a block reports its first old name only); the lines do not move, so all of them are applied together
            List<Fix> fixes = [.. doc.Diagnostics.Where(d => d.Code == "L010" && d.Fix is { Kind: "replace" } f && f.Line - 1 != probeAt).Select(d => d.Fix!).GroupBy(f => f.Line).Select(g => g.First())];
            bool changed = false;
            foreach (Fix fix in fixes.OrderByDescending(f => f.Line))
            {
                int at = fix.Line - 1;
                string line = lines[at];
                if (fix.Col < 1 || fix.EndCol < fix.Col || fix.EndCol - 1 > line.Length) continue;
                lines[at] = string.Concat(line.AsSpan(0, fix.Col - 1), fix.Text, line.AsSpan(fix.EndCol - 1));
                changed = true;
            }

            if (!changed) return text;
            lines.RemoveAt(probeAt);
            text = string.Join('\n', lines);
        }

        return text;
    }

    private static List<LineChange> Diff(string before, string after)
    {
        string[] a = before.Split('\n'), b = after.Split('\n');
        List<LineChange> changes = [];
        int common = Math.Min(a.Length, b.Length);
        for (int i = 0; i < common; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) changes.Add(new LineChange(i + 1, a[i], b[i]));
        }

        for (int i = common; i < a.Length; i++) changes.Add(new LineChange(i + 1, a[i], string.Empty));
        for (int i = common; i < b.Length; i++) changes.Add(new LineChange(i + 1, string.Empty, b[i]));
        return changes;
    }

    [GeneratedRegex("^(?:project|composition)\\s+\"(?:[^\"\\\\]|\\\\.)*\"\\s*(?<brace>\\{)\\s*(?:ample\\s+(?<ample>\\d+)\\s+)?tempo\\b", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SongLine();

    [GeneratedRegex("^(?<head>\\s*\\S+\\s+(?:\"(?:[^\"\\\\]|\\\\.)*\"|\\S+))\\s+(?<class>stable|pinned|live)(?:@(?<version>[^\\s{]+))?(?=\\s|\\{|$)", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex OldHeader();

    [GeneratedRegex("^project(?=\\s+\")", RegexOptions.Multiline, matchTimeoutMilliseconds: 2000)]
    private static partial Regex OldSongWord();

    [GeneratedRegex("^project\\s+\"", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex OldSongLine();
}
