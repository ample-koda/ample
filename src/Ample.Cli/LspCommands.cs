using System.CommandLine;
using Ample.Docs;
using Ample.Lsp;

namespace Ample.Cli;

/// <summary>The <c>ample lsp</c> command: the language server on stdio, for editors.</summary>
internal static class LspCommands
{
    internal static IReadOnlyList<Command> Create()
    {
        Option<string?> root = new("--root") { Description = "The AMPLE folder with spec/ (default: found from the current folder upwards)." };
        Option<string?> helpBase = new("--help-base") { Description = "Where diagnostic codes link to (default /help)." };
        Command lsp = new("lsp", "Runs the language server (Language Server Protocol 3.17) on stdin and stdout. Editors start this; nothing else is printed.") { root, helpBase };
        lsp.SetAction(async (p, t) =>
        {
            SpecRoot spec = p.GetValue(root) is string r ? SpecRoot.At(r) : SpecRoot.Find(Directory.GetCurrentDirectory());
            using StdioChannel channel = new(Console.OpenStandardInput(), Console.OpenStandardOutput());
            return await new LspServer(spec.LoadSpecData(), p.GetValue(helpBase) ?? "/help").RunAsync(channel, t);
        });
        return [lsp];
    }
}
