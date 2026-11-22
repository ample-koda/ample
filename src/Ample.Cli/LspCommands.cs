using System.CommandLine;
using Ample.Audio.IO;
using Ample.Docs;
using Ample.Engine.Modules;
using Ample.Lsp;

namespace Ample.Cli;

/// <summary>The <c>ample lsp</c> command: the language server on stdio, for editors.</summary>
internal static class LspCommands
{
    internal static IReadOnlyList<Command> Create()
    {
        Option<string?> root = new("--root") { Description = "The AMPLE folder with spec/ (default: found from the current folder upwards)." };
        Option<string?> project = new("--project") { Description = "The AMPLE project whose modules and settings should be available to the editor." };
        Option<string?> helpBase = new("--help-base") { Description = "Where diagnostic codes link to (default /help)." };
        Command lsp = new("lsp", "Runs the language server (Language Server Protocol 3.17) on stdin and stdout. Editors start this; nothing else is printed.") { root, project, helpBase };
        lsp.SetAction(async (p, t) =>
        {
            SpecRoot spec = p.GetValue(root) is string r ? SpecRoot.At(r) : SpecRoot.Find(Directory.GetCurrentDirectory());
            Ample.Language.SpecData specData = spec.LoadSpecData();
            using StdioChannel channel = new(Console.OpenStandardInput(), Console.OpenStandardOutput());
            if (p.GetValue(project) is not string projectRoot) return await new LspServer(specData, p.GetValue(helpBase) ?? "/help").RunAsync(channel, t);
            using ModuleService modules = ModuleService.WithUserLibrary(Path.GetFullPath(projectRoot), new SystemClock());
            LspServer server = new(specData, p.GetValue(helpBase) ?? "/help",
                components: text => modules.ComponentsForEditors(text, specData), observationProject: () => modules.ProjectLayerInput,
                projectCapabilities: () => modules.ProjectCapabilities,
                projectFiles: ProjectFiles.For(Path.GetFullPath(projectRoot)));
            return await server.RunAsync(channel, t);
        });
        return [lsp];
    }
}
