using System.CommandLine;
using Ample.Audio.IO;
using Ample.Storage.Projects;
using Ample.Core.Abstractions;

namespace Ample.Cli;

/// <summary>The <c>ample add</c> command: creates a file or folder of a kind in the project (the same kinds as the Add menu of the project panel).</summary>
internal static class AddCommands
{
    internal static IReadOnlyList<Command> Create(TextWriter output)
    {
        Argument<string> kind = new("kind") { Description = "What to create: " + string.Join(", ", NewFileKinds.All.Select(k => k.Id)) + "." };
        Argument<string> name = new("name") { Description = "The name (letters, digits, spaces, _ and -); the extension is added." };
        Option<string?> folder = new("--folder") { Description = "A project folder instead of the default one of the kind." };
        Option<string?> project = new("--project") { Description = "The project folder (default: the current folder)." };
        Option<bool> list = new("--list") { Description = "Lists the kinds and their default folders." };
        Command add = new("add", "Creates a song, lyrics, chords, MIDI clip, module, sample folder, note or folder with a valid skeleton.") { kind, name, folder, project, list };
        add.SetAction((parse, token) =>
        {
            _ = token;
            try
            {
                if (parse.GetValue(list))
                {
                    foreach (NewFileKind k in NewFileKinds.All) output.WriteLine($"{k.Id,-8} {k.Label,-22} {(k.Folder.Length == 0 ? "(project root)" : k.Folder + "/")}{(k.IsFolder ? "" : "*" + k.Extension)}");
                    return Task.FromResult(0);
                }

                string root = Path.GetFullPath(parse.GetValue(project) ?? Directory.GetCurrentDirectory());
                if (!Directory.Exists(Path.Combine(root, ".ample"))) throw new AmpleException("AMP5243", $"'{root}' is not an AMPLE project (there is no .ample folder). Use --project.");
                NewFile item = NewFileKinds.Build(parse.GetValue(kind)!, parse.GetValue(name)!, parse.GetValue(folder));
                string full = ProjectPath.Resolve(root, item.Path);
                if (File.Exists(full) || Directory.Exists(full)) throw new AmpleException("AMP5243", $"There is already something called {item.Path}.");
                if (item.IsFolder)
                {
                    Directory.CreateDirectory(full);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllBytes(full, item.Bytes!);
                }

                output.WriteLine($"Created {item.Path}");
                return Task.FromResult(0);
            }
            catch (AmpleException ex)
            {
                output.WriteLine($"{ex.DiagnosticCode}: {ex.Message}");
                return Task.FromResult(1);
            }
        });
        return [add, Move(output)];
    }

    private static Command Move(TextWriter output)
    {
        Argument<string> from = new("from") { Description = "The project file or folder to move or rename (project-relative)." };
        Argument<string> to = new("to") { Description = "Its new project-relative path." };
        Option<bool> noRefactor = new("--no-refactor") { Description = "Move only the file; paths in songs and modules that point at it are not changed (they may break)." };
        Option<bool> dryRun = new("--dry-run") { Description = "Print the changes without making them." };
        Option<string?> project = new("--project") { Description = "The project folder (default: the current folder)." };
        Command mv = new("mv", "Moves or renames a project file or folder and, by default, updates every path in the songs and modules that points at it.") { from, to, noRefactor, dryRun, project };
        mv.SetAction((parse, token) =>
        {
            _ = token;
            try
            {
                string root = Path.GetFullPath(parse.GetValue(project) ?? Directory.GetCurrentDirectory());
                string source = parse.GetValue(from)!.Replace('\\', '/').Trim('/'), destination = parse.GetValue(to)!.Replace('\\', '/').Trim('/');
                string fullFrom = ProjectPath.Resolve(root, source), fullTo = ProjectPath.Resolve(root, destination);
                bool directory = Directory.Exists(fullFrom);
                if (!directory && !File.Exists(fullFrom)) throw new AmpleException("AMP5243", $"There is no {source} in the project.");
                if (File.Exists(fullTo) || Directory.Exists(fullTo)) throw new AmpleException("AMP5243", $"{destination} already exists.");
                RefactorPlan plan = new([], new Dictionary<string, string>());
                if (!parse.GetValue(noRefactor))
                {
                    List<(string Path, string Text)> files = [];
                    foreach (string file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + ".ample" + Path.DirectorySeparatorChar, StringComparison.Ordinal) && (f.EndsWith(".amplet", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".module", StringComparison.OrdinalIgnoreCase))))
                    {
                        files.Add((Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllText(file)));
                    }

                    plan = ProjectRefactor.Plan(files, source, destination, directory);
                }

                foreach (PathEdit e in plan.Edits) output.WriteLine($"{e.File}:{e.Line}: {e.Old.Trim()}  ->  {e.New.Trim()}");
                if (parse.GetValue(dryRun)) { output.WriteLine($"{plan.Edits.Count} change(s); nothing was moved."); return Task.FromResult(0); }
                Directory.CreateDirectory(Path.GetDirectoryName(fullTo)!);
                if (directory) Directory.Move(fullFrom, fullTo); else File.Move(fullFrom, fullTo);
                foreach ((string path, string text) in plan.Texts) File.WriteAllText(ProjectPath.Resolve(root, ProjectRefactor.PathAfterMove(path, source, destination, directory)), text);
                output.WriteLine($"Moved {source} to {destination}; {plan.Edits.Count} reference(s) updated.");
                return Task.FromResult(0);
            }
            catch (AmpleException ex)
            {
                output.WriteLine($"{ex.DiagnosticCode}: {ex.Message}");
                return Task.FromResult(1);
            }
        });
        return mv;
    }
}
