using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ample.Cli;

/// <summary>
/// Connects a folder that Visual Studio already has files in to an AMPLE project (plan/visual-studio-integration.md, VS05 step 3).
/// Identical files are no conflict; a local file that differs from the project's file at the same path is moved to
/// <c>_conflicts/&lt;id&gt;/&lt;same path&gt;</c> in the local folder and the project's file takes its place; a local-only file joins the project.
/// Nothing is deleted. A manifest is written before the first move, so a crash can be resumed by running the same connect again.
/// </summary>
internal static class VsConnect
{
    private static readonly string[] Skipped = [".ample", ".git", ".vs", "bin", "obj", "_conflicts", "Rules"];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal sealed record Step(string Path, string Action);

    internal sealed record Result(string? ConflictId, IReadOnlyList<Step> Steps);

    /// <summary>Plans and runs the connect. <paramref name="id"/> is only given by a test or a resumed run.</summary>
    internal static Result Run(string local, string project, string? id = null, IReadOnlyCollection<string>? studioDrafts = null)
    {
        local = Path.GetFullPath(local);
        project = Path.GetFullPath(project);
        string? pending = id is null ? PendingManifest(local) : null;
        id ??= pending ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3)) + "-" + DateTime.UtcNow.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        string conflicts = Path.Combine(local, "_conflicts", id);
        List<Step> steps = [];
        foreach (string file in Files(local))
        {
            string relative = Path.GetRelativePath(local, file).Replace('\\', '/');
            string theirs = Path.Combine(project, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(theirs)) steps.Add(new Step(relative, "join"));
            else if (Hash(file) == Hash(theirs)) steps.Add(new Step(relative, "same"));
            else steps.Add(new Step(relative, "conflict"));
        }

        // files that only the project has come to the local folder, so Visual Studio shows the whole project
        foreach (string file in Files(project))
        {
            string relative = Path.GetRelativePath(project, file).Replace('\\', '/');
            if (!File.Exists(Path.Combine(local, relative.Replace('/', Path.DirectorySeparatorChar)))) steps.Add(new Step(relative, "fetch"));
        }

        // a file the Studio has unsaved changes in is not moved: they would be saved over the project's file or lost; say which and stop before any move
        string[] blocked = [.. steps.Where(s => s.Action == "conflict" && studioDrafts is not null && studioDrafts.Contains(s.Path, StringComparer.OrdinalIgnoreCase)).Select(s => s.Path)];
        if (blocked.Length > 0) throw new InvalidOperationException("The Studio has unsaved changes in " + string.Join(", ", blocked) + "; save or revert them there, then connect again.");
        bool anyConflict = steps.Any(s => s.Action == "conflict");
        if (anyConflict)
        {
            Directory.CreateDirectory(conflicts);
            File.WriteAllText(Path.Combine(conflicts, "manifest.json"), JsonSerializer.Serialize(new { id, steps }, Json));
            File.WriteAllText(Path.Combine(conflicts, "README.txt"), Readme(id, steps));
        }

        foreach (Step step in steps)
        {
            string mine = Path.Combine(local, step.Path.Replace('/', Path.DirectorySeparatorChar));
            string theirs = Path.Combine(project, step.Path.Replace('/', Path.DirectorySeparatorChar));
            switch (step.Action)
            {
                case "join":
                    Copy(mine, theirs);
                    break;
                case "fetch":
                    Copy(theirs, mine);
                    break;
                case "conflict":
                    Move(mine, Path.Combine(conflicts, step.Path.Replace('/', Path.DirectorySeparatorChar)));
                    Copy(theirs, mine);
                    break;
            }
        }

        if (anyConflict || pending is not null) File.WriteAllText(Path.Combine(conflicts, "done"), DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        return new Result(anyConflict || pending is not null ? id : null, steps);
    }

    /// <summary>The id of an earlier connect that started moving files and did not finish, or null.</summary>
    private static string? PendingManifest(string local)
    {
        string root = Path.Combine(local, "_conflicts");
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, "manifest.json")) && !File.Exists(Path.Combine(d, "done")))
            .Select(Path.GetFileName).FirstOrDefault();
    }

    private static IEnumerable<string> Files(string root)
    {
        Stack<string> folders = new([root]);
        while (folders.Count > 0)
        {
            string folder = folders.Pop();
            foreach (string sub in Directory.EnumerateDirectories(folder))
            {
                string name = Path.GetFileName(sub);
                if (Skipped.Contains(name, StringComparer.OrdinalIgnoreCase) && folder == root) continue;
                folders.Push(sub);
            }

            foreach (string file in Directory.EnumerateFiles(folder))
            {
                string name = Path.GetFileName(file);
                if (folder == root && (name.EndsWith(".ampleproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".ampleproj.user", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || name == "Ample.targets")) continue;
                yield return file;
            }
        }
    }

    private static string Hash(string file)
    {
        using FileStream stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, true);
    }

    /// <summary>Moves a file; when it was moved by an earlier, interrupted run the source is gone and nothing happens.</summary>
    private static void Move(string from, string to)
    {
        if (!File.Exists(from)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(from, to, true);
    }

    private static string Readme(string id, IReadOnlyList<Step> steps)
    {
        StringBuilder text = new();
        text.AppendLine("Connect " + id);
        text.AppendLine();
        text.AppendLine("These files in this folder differed from the AMPLE project's files at the same path. Your version was moved here;");
        text.AppendLine("the project's version is now at the normal place (it is the live one, Visual Studio and the Studio both use it).");
        text.AppendLine("Nothing was deleted. To bring one of yours back, copy it over the file at the normal place (and check it in the Studio).");
        text.AppendLine();
        foreach (Step step in steps.Where(s => s.Action == "conflict")) text.AppendLine("  " + step.Path);
        return text.ToString();
    }
}
