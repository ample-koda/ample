using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Ample.Core.Abstractions;
using Ample.Storage.Projects;

namespace Ample.Server;

/// <summary>
/// The notes the AI assistant keeps for later tasks, at two levels: the whole project (taste, agreements, decisions) and one song (what it is for, what was tried). They live in the
/// <c>.ample</c> folder of the project as Markdown files, never in a song, never in the temporary copy that Codex edits, and they are not part of an export of a song. The assistant reads
/// and writes them with the tools <c>ample_memory_read</c> and <c>ample_memory_write</c>; the owner can edit the files too.
/// </summary>
internal static class AiMemory
{
    /// <summary>The longest text one write may bring.</summary>
    public const int MaxWrite = 6_000;

    /// <summary>The largest a memory may become: when it is full the assistant has to tidy it (mode replace).</summary>
    public const int MaxFile = 24_000;

    /// <summary>The Markdown file of a memory: <c>project.md</c>, or the file of a song under <c>songs/</c> (its project path with the slashes replaced), inside <c>.ample/ai/memory</c>.</summary>
    public static string FileOf(string projectRoot, string scope, string song)
    {
        string folder = Path.Combine(projectRoot, ProjectLayout.AmpleFolder, "ai", "memory");
        switch (scope)
        {
            case "project":
                return Path.Combine(folder, "project.md");
            case "song":
                {
                    string clean = song.Replace('\\', '/').Trim('/');
                    if (clean.Length == 0)
                    {
                        throw new AmpleException("AMP5458", "This task has no song, so there is no song memory. Use scope=project.");
                    }

                    StringBuilder name = new();
                    foreach (char ch in clean)
                    {
                        name.Append(ch == '/' ? "__" : Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch);
                    }

                    return Path.Combine(folder, "songs", name + ".md");
                }

            default:
                throw new AmpleException("AMP5458", "scope must be project or song.");
        }
    }

    /// <summary>Reads a memory (empty text when nothing was written yet).</summary>
    public static JsonObject Read(string projectRoot, string scope, string song)
    {
        string file = FileOf(projectRoot, scope, song);
        string text = File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : string.Empty;
        return new JsonObject
        {
            ["scope"] = scope,
            ["song"] = scope == "song" ? song : null,
            ["exists"] = text.Length > 0,
            ["chars"] = text.Length,
            ["limit"] = MaxFile,
            ["text"] = text,
            ["note"] = text.Length == 0 ? "Nothing is written yet." : "These notes are background kept outside the song; they are not part of the song or of your proposal.",
        };
    }

    /// <summary>
    /// Writes to a memory. Mode <c>append</c> (the default) adds one dated note; mode <c>replace</c> rewrites the whole memory (to tidy or shorten it, or to clear it with empty text).
    /// </summary>
    public static JsonObject Write(string projectRoot, string scope, string song, string text, string? mode, DateTimeOffset now)
    {
        string file = FileOf(projectRoot, scope, song);
        string how = string.IsNullOrWhiteSpace(mode) ? "append" : mode.Trim().ToLowerInvariant();
        if (how is not ("append" or "replace"))
        {
            throw new AmpleException("AMP5458", "mode must be append or replace.");
        }

        string clean = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (clean.Length > MaxWrite)
        {
            throw new AmpleException("AMP5458", $"One write may be at most {MaxWrite} characters; keep notes short and durable.");
        }

        string current = File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : string.Empty;
        string next;
        if (how == "replace")
        {
            next = clean.Length == 0 ? string.Empty : clean + "\n";
        }
        else
        {
            if (clean.Length == 0)
            {
                throw new AmpleException("AMP5458", "There is no text to add.");
            }

            string line = "- " + now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ": " + clean.Replace("\n", "\n  ", StringComparison.Ordinal);
            next = current.Length == 0 ? line + "\n" : current.TrimEnd('\n') + "\n" + line + "\n";
        }

        if (next.Length > MaxFile)
        {
            throw new AmpleException("AMP5458", $"This memory would become larger than {MaxFile} characters. Read it, tidy it up and write the shorter version with mode=replace.");
        }

        if (next.Length == 0)
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, next, new UTF8Encoding(false));
        }

        return new JsonObject { ["ok"] = true, ["scope"] = scope, ["mode"] = how, ["chars"] = next.Length, ["limit"] = MaxFile };
    }
}
