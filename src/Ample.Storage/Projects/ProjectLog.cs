using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ample.Storage.Projects;

/// <summary>One line of the project log.</summary>
/// <param name="At">When it was written.</param>
/// <param name="Kind"><c>missing</c> (something a tool needed is not there), <c>hint</c> or <c>note</c>.</param>
/// <param name="What">What it is about.</param>
/// <param name="Hint">How to put it right.</param>
public sealed record ProjectLogEntry(DateTimeOffset At, string Kind, string What, string Hint);

/// <summary>
/// The project log (<c>.ample/logs/project-log.jsonl</c>): what is missing for the tools of the project and how to put it right. Written by the tools and the
/// environment check, shown on the Health page and read by the assistant, which tells the person and recommends the fix. The same entry is written once.
/// </summary>
public static class ProjectLog
{
    private static readonly object Gate = new();

    private static string FileOf(string projectRoot) => Path.Combine(projectRoot, ProjectLayout.AmpleFolder, "logs", "project-log.jsonl");

    /// <summary>Adds an entry unless the same one is already there.</summary>
    /// <returns>True when it was added.</returns>
    public static bool Append(string projectRoot, string kind, string what, string hint)
    {
        lock (Gate)
        {
            if (List(projectRoot, 500).Any(e => e.Kind == kind && e.What == what && e.Hint == hint))
            {
                return false;
            }

            string file = FileOf(projectRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            JsonObject line = new() { ["at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), ["kind"] = kind, ["what"] = what, ["hint"] = hint };
            File.AppendAllText(file, line.ToJsonString() + "\n");
            return true;
        }
    }

    /// <summary>Removes the entries about something (it was put right).</summary>
    public static int Resolve(string projectRoot, string what)
    {
        lock (Gate)
        {
            List<ProjectLogEntry> all = List(projectRoot, 5000);
            List<ProjectLogEntry> kept = [.. all.Where(e => e.What != what)];
            if (kept.Count == all.Count)
            {
                return 0;
            }

            File.WriteAllText(FileOf(projectRoot), string.Concat(kept.Select(e => new JsonObject { ["at"] = e.At.ToString("O", CultureInfo.InvariantCulture), ["kind"] = e.Kind, ["what"] = e.What, ["hint"] = e.Hint }.ToJsonString() + "\n")));
            return all.Count - kept.Count;
        }
    }

    /// <summary>Lists the newest entries first.</summary>
    public static List<ProjectLogEntry> List(string projectRoot, int limit = 100)
    {
        string file = FileOf(projectRoot);
        List<ProjectLogEntry> list = [];
        try
        {
            if (!File.Exists(file))
            {
                return list;
            }

            foreach (string line in File.ReadLines(file))
            {
                try
                {
                    if (JsonNode.Parse(line) is JsonObject o && (string?)o["what"] is { } what)
                    {
                        list.Add(new ProjectLogEntry(DateTimeOffset.TryParse((string?)o["at"], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset at) ? at : DateTimeOffset.MinValue, (string?)o["kind"] ?? "note", what, (string?)o["hint"] ?? string.Empty));
                    }
                }
                catch (JsonException)
                {
                    // a damaged line is skipped
                }
            }
        }
        catch (IOException)
        {
            // the log is a help only
        }

        list.Reverse();
        return [.. list.Take(limit)];
    }
}
