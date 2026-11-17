using System.Text;
using System.Text.RegularExpressions;

namespace Ample.Language;

/// <summary>Formats AMPLE source into the canonical form (spec page 09). Idempotent; never refuses; leaves lines it cannot parse unchanged.</summary>
public static partial class Formatter
{
    /// <summary>Formats a whole document.</summary>
    public static string Format(string text, SpecData spec)
    {
        _ = spec;
        string normalized = text.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        List<string> output = [];
        bool inBlock = false;
        bool midiClip = false;
        int notesDepth = 0, scratchDepth = 0;
        bool rewind = false;
        int viewDepth = 0;
        int codeDepth = 0, parenDepth = 0;   // inside the braces of a helper or a component
        bool pipeCarry = false;
        foreach (string raw in lines)
        {
            string line = raw.Replace('\t', ' ').Trim();
            if (line.Length == 0)
            {
                if (output.Count > 0 && output[^1].Length > 0)
                {
                    output.Add(string.Empty);
                }

                continue;
            }

            if (viewDepth > 0)
            {
                if (line == "}")
                {
                    viewDepth--;
                }

                output.Add(new string(' ', viewDepth * 2) + line);
                if (line.EndsWith('{'))
                {
                    viewDepth++;
                }

                continue;
            }

            if (codeDepth > 0)
            {
                FormatCodeLine(line, output, ref codeDepth, ref parenDepth, ref pipeCarry);
                continue;
            }

            if (!inBlock && line == "view {")
            {
                viewDepth = 1;
                output.Add(line);
                continue;
            }

            if (line[0] == '#')
            {
                output.Add((inBlock ? new string(' ', 2 + (notesDepth + scratchDepth) * 2) : string.Empty) + line);
                continue;
            }

            if (inBlock)
            {
                if (scratchDepth > 0)
                {
                    if (line.StartsWith('}')) scratchDepth--;
                    output.Add(new string(' ', 2 + scratchDepth * 2) + line);
                    if (line.EndsWith('{')) scratchDepth++;
                    continue;
                }
                if (rewind && line.Replace(" ", string.Empty) == "scratch{")
                {
                    scratchDepth = 1;
                    output.Add("  scratch {");
                    continue;
                }
                if (notesDepth > 0)
                {
                    if (line == "}") notesDepth--;
                    output.Add(new string(' ', 2 + notesDepth * 2) + line);
                    if (line == "note {") notesDepth++;
                    continue;
                }
                if (midiClip && line == "notes {")
                {
                    notesDepth = 1;
                    output.Add("  notes {");
                    continue;
                }
                if (line == "}")
                {
                    inBlock = false;
                    midiClip = false;
                    output.Add("}");
                }
                else
                {
                    output.Add("  " + FormatBodyLine(line));
                }

                continue;
            }

            if (line.EndsWith('{') && CodeStart().IsMatch(line))
            {
                output.Add(FormatHeader(line));
                codeDepth = 1;
                parenDepth = 0;
                pipeCarry = false;
                continue;
            }

            if (line.EndsWith('{') && !ProjectWord().IsMatch(line) && !BraceLine().IsMatch(line) && !LayoutLoose().IsMatch(line))
            {
                inBlock = true;
                midiClip = line.StartsWith("midi-clip ", StringComparison.Ordinal);
                rewind = line.StartsWith("rewind ", StringComparison.Ordinal);
                output.Add(FormatHeader(line));
                continue;
            }

            output.Add(FormatTopLine(line));
        }

        while (output.Count > 0 && output[^1].Length == 0)
        {
            output.RemoveAt(output.Count - 1);
        }

        while (output.Count > 0 && output[0].Length == 0)
        {
            output.RemoveAt(0);
        }

        return output.Count == 0 ? string.Empty : string.Join('\n', output) + "\n";
    }

    // ---- code regions: the body of a helper and of a component is code, laid out by its braces (the generic body lines of a block do not nest)

    [GeneratedRegex("^(?:helper|component)\\b", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex CodeStart();

    private static string Pad(int level) => new(' ', Math.Max(0, level) * 2);

    /// <summary>Splits a line of code from its comment: a # that does not stand in a string and does not follow a letter, a digit or an underscore (a
    /// hash of a bake is not a comment).</summary>
    private static void SplitComment(string line, out string code, out string comment)
    {
        bool inString = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inString) { if (c == '\\') i++; else if (c == '"') inString = false; continue; }
            if (c == '"') { inString = true; continue; }
            if (c == '#' && !(i > 0 && (char.IsLetterOrDigit(line[i - 1]) || line[i - 1] == '_')))
            {
                code = line[..i].TrimEnd();
                comment = line[i..].TrimEnd();
                return;
            }
        }

        code = line.TrimEnd();
        comment = string.Empty;
    }

    /// <summary>What a line of code does to the nesting: the closing braces it starts with, the braces it opens minus the ones it closes, and the
    /// parentheses and brackets that stay open after it.</summary>
    private static void Scan(string text, int parensBefore, out int leading, out int delta, out int parensAfter)
    {
        leading = 0;
        delta = 0;
        parensAfter = parensBefore;
        bool inString = false, atStart = true;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inString) { if (c == '\\') i++; else if (c == '"') inString = false; continue; }
            if (c == ' ') continue;
            if (c == '"') { inString = true; atStart = false; continue; }
            if (c == '}') { delta--; if (atStart) leading++; }
            else
            {
                atStart = false;
                if (c == '{') delta++;
                else if (c is '(' or '[') parensAfter++;
                else if (c is ')' or ']') parensAfter = Math.Max(0, parensAfter - 1);
            }
        }
    }

    /// <summary>
    /// One line inside a helper or a component: its if and else if heads are written with the parentheses around the condition (ConditionSyntax), and
    /// it is indented by its braces, two spaces a level. A line that
    /// goes on from the one before (inside parentheses, or after a |>) stands one level in. The comment of a line stays where it was. Idempotent,
    /// because the indentation follows from the structure and not from what was there.
    /// </summary>
    private static void FormatCodeLine(string line, List<string> output, ref int depth, ref int parens, ref bool pipeCarry)
    {
        SplitComment(line, out string code, out string comment);
        if (code.Length == 0)
        {
            output.Add(Pad(depth + (parens > 0 || pipeCarry ? 1 : 0)) + comment);
            return;
        }

        string text = ConditionSyntax.Parse(code) is { Problem: null } head ? ConditionSyntax.Format(head) : code;
        if (parens == 0 && !pipeCarry && NotePipeline.FormatObservation(text, Math.Max(0, depth) * 2) is { } observed)
        {
            for (int i = 0; i < observed.Count; i++)
            {
                bool middle = i > 0 && i < observed.Count - 1;
                output.Add(Pad(depth + (middle ? 1 : 0)) + observed[i] + (i == observed.Count - 1 && comment.Length > 0 ? "  " + comment : string.Empty));
            }

            return;
        }

        Scan(text, parens, out int leading, out int delta, out int parensAfter);
        bool continuation = parens > 0 || pipeCarry || text.StartsWith("|>", StringComparison.Ordinal);
        bool closer = parens > 0 && text[0] is ')' or ']';
        int level = depth - leading + (leading == 0 && continuation && !closer ? 1 : 0);
        output.Add(Pad(level) + text + (comment.Length > 0 ? "  " + comment : string.Empty));
        depth += delta;
        parens = parensAfter;
        pipeCarry = text.EndsWith("|>", StringComparison.Ordinal);
        if (depth <= 0)
        {
            depth = 0;
            parens = 0;
            pipeCarry = false;
        }
    }

    private static string FormatTopLine(string line)
    {
        Match p = ProjectLoose().Match(line);
        if (p.Success)
        {
            return $"{p.Groups[1].Value} {p.Groups[2].Value} {{ {(p.Groups["ample"].Success ? "ample " + p.Groups["ample"].Value + "  " : string.Empty)}tempo {p.Groups[3].Value}  {(p.Groups["mn"].Success ? "meter " + p.Groups["mn"].Value + "/" + p.Groups["md"].Value + "  " : string.Empty)}master-seed {p.Groups[4].Value} }}";
        }

        Match f = BraceLine().Match(line);
        if (f.Success)
        {
            IReadOnlyList<string> tk = Parser.Tokenize(f.Groups[2].Value);
            StringBuilder sb = new();
            for (int i = 0; i < tk.Count; i += 2)
            {
                sb.Append(i == 0 ? string.Empty : "  ").Append(tk[i]);
                if (i + 1 < tk.Count)
                {
                    sb.Append(' ').Append(tk[i + 1]);
                }
            }

            return sb.Length == 0 ? $"{f.Groups[1].Value} {{ }}" : $"{f.Groups[1].Value} {{ {sb} }}";
        }

        Match l = LayoutLoose().Match(line);
        if (l.Success)
        {
            return $"layout {l.Groups[1].Value} {l.Groups[2].Value} {l.Groups[3].Value}";
        }

        return line;
    }

    private static string FormatHeader(string line)
    {
        IReadOnlyList<string> tk = Parser.Tokenize(line[..^1].Trim());
        if (tk.Count == 0)
        {
            return line;
        }

        string joined = string.Join(' ', tk).Replace(" ,", ",", StringComparison.Ordinal);
        return joined + " {";
    }

    private static string FormatBodyLine(string line)
    {
        bool disabledConnection = line.StartsWith("from ", StringComparison.Ordinal) && line.EndsWith(" disabled", StringComparison.Ordinal);
        Match from = FromLoose().Match(disabledConnection ? line[..^9].TrimEnd() : line);
        if (from.Success)
        {
            return $"from {from.Groups[1].Value}.{from.Groups[2].Value} -> {from.Groups[3].Value}" + (disabledConnection ? " disabled" : string.Empty);
        }

        Match parameter = ParameterLoose().Match(line);
        return parameter.Success ? $"{parameter.Groups[1].Value} {parameter.Groups[2].Value.Trim()}" : line;
    }

    [GeneratedRegex("^(?:project|composition)\\b", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ProjectWord();

    [GeneratedRegex("^(features|lint|settings)\\s*\\{(.*)\\}$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex BraceLine();

    [GeneratedRegex("^(project|composition)\\s*(\"(?:[^\"\\\\]|\\\\.)*\")\\s*\\{\\s*(?:ample\\s+(?<ample>\\d+)\\s+)?tempo\\s+(\\d+(?:\\.\\d+)?)\\s+(?:meter\\s+(?<mn>\\d+)/(?<md>\\d+)\\s+)?master-seed\\s+(\\d+)\\s*\\}$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ProjectLoose();

    [GeneratedRegex("^layout\\s+(\"(?:[^\"\\\\]|\\\\.)*\"|\\S+)\\s+(-?\\d+)\\s+(-?\\d+)$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex LayoutLoose();

    [GeneratedRegex("^from\\s+(\"(?:[^\"\\\\]|\\\\.)*\"|[^\\s.\"]+)\\.(\"(?:[^\"\\\\]|\\\\.)*\"|[^\\s\"-]+)\\s*->\\s*(\"(?:[^\"\\\\]|\\\\.)*\"|\\S+)$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex FromLoose();

    [GeneratedRegex("^([\\w-]+)\\s+(.+)$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ParameterLoose();
}
