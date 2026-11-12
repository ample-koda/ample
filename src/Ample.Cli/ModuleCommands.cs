using System.CommandLine;
using Ample.Audio.IO;
using Ample.Core.Abstractions;
using Ample.Docs;
using Ample.Engine.Modules;
using Ample.Language;

namespace Ample.Cli;

/// <summary>The <c>ample modules</c> commands: list, add and expand.</summary>
internal static class ModuleCommands
{
    private static readonly SystemClock Clock = new();

    internal static IReadOnlyList<Command> Create(TextWriter output) => [Modules(output)];

    private static Option<string?> ProjectOption() => new("--project") { Description = "The project folder (default: the current folder)." };

    private static Option<string?> RootOption() => new("--root") { Description = "The AMPLE folder (default: found from the current folder upwards)." };

    private static async Task<int> Guard(TextWriter output, Func<Task<int>> body)
    {
        try
        {
            return await body();
        }
        catch (AmpleException ex)
        {
            output.WriteLine($"{ex.DiagnosticCode}: {ex.Message}");
            return 1;
        }
    }

    private static Command Modules(TextWriter output)
    {
        Command root = new("modules", "Reusable modules: groups of blocks with parameters, inputs and outputs, used with 'use' in a song.");

        Option<string?> listProject = ProjectOption();
        Command list = new("list", "Lists the modules of the project's modules/ folder.") { listProject };
        list.SetAction((parse, token) => Guard(output, () =>
        {
            _ = token;
            string project = Path.GetFullPath(parse.GetValue(listProject) ?? Directory.GetCurrentDirectory());
            using ModuleService service = new(project, (Ample.Storage.Library.Library?)null);
            IReadOnlyList<(string Name, string Path)> modules = service.ProjectModules();
            foreach ((string name, string path) in modules)
            {
                ModuleDefinition def = ModuleDefinition.Parse(File.ReadAllText(path));
                output.WriteLine(def.Problems.Count > 0
                    ? $"{name}: damaged ({def.Problems[0]})"
                    : $"{name} {def.Version}: params {(def.Params.Count == 0 ? "none" : string.Join(", ", def.Params.Select(p => p.Name)))}; inputs {(def.Inputs.Count == 0 ? "none" : string.Join(", ", def.Inputs))}; outputs {(def.Outputs.Count == 0 ? "none" : string.Join(", ", def.Outputs.Select(o => o.Name)))}");
            }

            if (modules.Count == 0)
            {
                output.WriteLine("No modules in modules/ yet.");
            }

            return Task.FromResult(0);
        }));

        Argument<string> addFile = new("file") { Description = "The module file to copy into the project." };
        Option<string?> addProject = ProjectOption();
        Command add = new("add", "Checks a module file and copies it into the project's modules/ folder.") { addFile, addProject };
        add.SetAction((parse, token) => Guard(output, () =>
        {
            _ = token;
            string project = Path.GetFullPath(parse.GetValue(addProject) ?? Directory.GetCurrentDirectory());
            string source = Path.GetFullPath(parse.GetValue(addFile)!);
            ModuleDefinition def = ModuleDefinition.Parse(File.ReadAllText(source));
            if (def.Problems.Count > 0)
            {
                throw new AmpleException("AMP5326", $"The module file is damaged: {def.Problems[0]}");
            }

            string target = Ample.Audio.IO.ProjectPath.Resolve(project, "modules/" + def.Name + ModuleService.Extension);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                throw new AmpleException("AMP5326", $"modules/{def.Name}{ModuleService.Extension} already exists; remove it first.");
            }

            File.Copy(source, target);
            output.WriteLine($"Added module {def.Name} {def.Version}. Use it with: use {def.Name}@{def.Version} as <name> {{ ... }}");
            return Task.FromResult(0);
        }));

        Argument<string> expandFile = new("file") { Description = "The .amplet file." };
        Option<string?> expandProject = ProjectOption();
        Option<string?> expandRoot = RootOption();
        Command expand = new("expand", "Prints the song with every use replaced by the blocks of its module, and the problems found.") { expandFile, expandProject, expandRoot };
        expand.SetAction((parse, token) => Guard(output, async () =>
        {
            string song = Path.GetFullPath(parse.GetValue(expandFile)!);
            string project = Path.GetFullPath(parse.GetValue(expandProject) ?? Path.GetDirectoryName(song)!);
            SpecData spec = (parse.GetValue(expandRoot) is { } r ? SpecRoot.At(r) : SpecRoot.Find(Directory.GetCurrentDirectory())).LoadSpecData();
            using ModuleService service = ModuleService.WithUserLibrary(project, Clock);
            ExpandedSong e = service.Expand(await File.ReadAllTextAsync(song, token), spec);
            output.WriteLine(e.Text);
            foreach (ModuleDiagnostic d in e.Diagnostics)
            {
                output.WriteLine($"{song}:{d.Line + 1}: error {d.Code} {d.Message}");
            }

            return e.Diagnostics.Count == 0 ? 0 : 1;
        }));

        foreach (Command c in new[] { list, add, expand })
        {
            root.Subcommands.Add(c);
        }

        return root;
    }
}
