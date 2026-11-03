using System.Text.RegularExpressions;

namespace Ample.Storage.Projects;

/// <summary>One change a rename or move makes to a song or module file.</summary>
/// <param name="File">Project-relative path of the file that changes.</param>
/// <param name="Line">1-based line.</param>
/// <param name="Old">The line before.</param>
/// <param name="New">The line after.</param>
public sealed record PathEdit(string File, int Line, string Old, string New);

/// <summary>What a rename or move of a project file or folder changes in the songs and modules: every path that points at it, and the name of a renamed module.</summary>
/// <param name="Edits">The changed lines, for the preview.</param>
/// <param name="Texts">The new text of every file that changes, by project-relative path.</param>
public sealed record RefactorPlan(IReadOnlyList<PathEdit> Edits, IReadOnlyDictionary<string, string> Texts);

/// <summary>Works out how references follow a moved file or folder (plan project-panel PP02). Text based on purpose: it changes only the lines that name the path, nothing else in the file.</summary>
public static partial class ProjectRefactor
{
    private const string Before = "\"' :;,=";
    private const string After = "\"' ;,";

    /// <summary>Plans the edits for moving <paramref name="from"/> to <paramref name="to"/> (project-relative, forward slashes) in the given files (path and text).</summary>
    public static RefactorPlan Plan(IEnumerable<(string Path, string Text)> files, string from, string to, bool isDirectory)
    {
        List<PathEdit> edits = [];
        Dictionary<string, string> texts = new(StringComparer.Ordinal);
        string? fromModule = ModuleNameOf(from), toModule = ModuleNameOf(to);
        foreach ((string path, string text) in files)
        {
            bool crlf = text.Contains("\r\n", StringComparison.Ordinal);
            string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            bool changed = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.TrimStart().StartsWith('#')) continue;
                string next = Replace(line, from, to, isDirectory);
                if (fromModule is not null && toModule is not null && fromModule != toModule)
                {
                    next = UseLine().Replace(next, m => m.Groups["module"].Value == fromModule ? m.Groups["head"].Value + toModule + m.Groups["tail"].Value : m.Value);
                    // the moved module names itself in its first line
                    if (path == from) next = Header().Replace(next, m => m.Groups["name"].Value == fromModule ? m.Groups["head"].Value + toModule + m.Groups["tail"].Value : m.Value);
                }

                if (next == line) continue;
                lines[i] = next;
                changed = true;
                edits.Add(new PathEdit(path, i + 1, line, next));
            }

            if (changed) texts[path] = string.Join(crlf ? "\r\n" : "\n", lines);
        }

        return new RefactorPlan(edits, texts);
    }

    /// <summary>Where a file is after the move: the moved file itself, or a file under a moved folder.</summary>
    public static string PathAfterMove(string path, string from, string to, bool isDirectory) =>
        path == from ? to : isDirectory && path.StartsWith(from + "/", StringComparison.Ordinal) ? to + path[from.Length..] : path;

    /// <summary>The module name of a <c>modules/name.module</c> path, or null.</summary>
    public static string? ModuleNameOf(string path) => path.StartsWith("modules/", StringComparison.Ordinal) && path.EndsWith(".module", StringComparison.Ordinal) && path.IndexOf('/', "modules/".Length) < 0 ? path["modules/".Length..^".module".Length] : null;

    /// <summary>Replaces every whole occurrence of a path in a line (a file path exactly, a folder path also as the start of a longer path).</summary>
    public static string Replace(string line, string from, string to, bool isDirectory)
    {
        if (from.Length == 0) return line;
        int index = 0;
        System.Text.StringBuilder result = new();
        while (index < line.Length)
        {
            int at = line.IndexOf(from, index, StringComparison.Ordinal);
            if (at < 0) { result.Append(line, index, line.Length - index); break; }
            int end = at + from.Length;
            bool startOk = at == 0 || Before.Contains(line[at - 1], StringComparison.Ordinal);
            bool endOk = end == line.Length || After.Contains(line[end], StringComparison.Ordinal) || (isDirectory && line[end] == '/');
            result.Append(line, index, at - index);
            result.Append(startOk && endOk ? to : from);
            index = end;
        }

        return result.ToString();
    }

    [GeneratedRegex(@"^(?<head>\s*use\s+)(?<module>[A-Za-z0-9_-]+)(?<tail>(?:@\S+)?\s+as\b.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex UseLine();

    [GeneratedRegex(@"^(?<head>module\s+)(?<name>[\w-]+)(?<tail>\s+version\b.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Header();
}
