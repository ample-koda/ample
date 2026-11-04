using System.CommandLine;
using System.Text.Json;
using Ample.Core.Abstractions;
using Ample.Docs;
using Ample.Plugins;

namespace Ample.Cli;

/// <summary>The <c>ample plugins</c> commands.</summary>
internal static class PluginCommands
{
    internal static IReadOnlyList<Command> Create(TextWriter output)
    {
        Option<string?> root = new("--root") { Description = "The AMPLE folder (default: found from the current folder upwards)." };
        Option<string?> folder = new("--folder") { Description = "An extra folder with plugins." };
        Argument<string> id = new("plugin") { Description = "A plugin id." };
        Argument<string> path = new("folder") { Description = "A plugin folder (with plugin.json)." };

        Command list = new("list", "Lists the plugins that are found.") { root, folder };
        list.SetAction(p =>
        {
            (IReadOnlyList<DiscoveredPlugin> plugins, IReadOnlyList<string> problems) = Find(p.GetValue(root), p.GetValue(folder));
            foreach (DiscoveredPlugin d in plugins)
            {
                output.WriteLine($"{d.Manifest.Id} {d.Manifest.Version}  {d.Manifest.Kind}  {d.Manifest.Determinism}  blocks: {string.Join(", ", d.Manifest.Blocks)}{(d.Manifest.Experimental ? "  (experimental)" : string.Empty)}");
            }

            foreach (string problem in problems)
            {
                output.WriteLine("problem: " + problem);
            }

            output.WriteLine($"{plugins.Count} plugin(s)");
            return problems.Count == 0 ? 0 : 1;
        });

        Command describe = new("describe", "Shows a plugin's manifest and settings schema.") { id, root, folder };
        describe.SetAction(p =>
        {
            DiscoveredPlugin? d = Find(p.GetValue(root), p.GetValue(folder)).Plugins.FirstOrDefault(x => x.Manifest.Id == p.GetValue(id));
            if (d is null)
            {
                output.WriteLine($"AMP5243: there is no plugin '{p.GetValue(id)}'.");
                return 1;
            }

            PluginManifest m = d.Manifest;
            output.WriteLine($"{m.Title} ({m.Id} {m.Version}), {m.Kind}, protocol {m.ProtocolVersion}, determinism {m.Determinism}");
            output.WriteLine($"program: {m.Command} {string.Join(' ', m.Args)}  (in {d.Folder})");
            output.WriteLine($"blocks: {string.Join(", ", m.Blocks)}; platforms: {string.Join(", ", m.Platforms)}");
            output.WriteLine($"needs: gpu {m.Needs.Gpu}, network {m.Needs.Network}, disk {m.Needs.DiskMb} MB, secrets [{string.Join(", ", m.Needs.Secrets)}]");
            output.WriteLine("settings schema: " + m.SettingsSchemaJson);
            return 0;
        });

        Command validate = new("validate", "Checks a plugin manifest; exit code 1 when it has problems.") { path };
        validate.SetAction(p =>
        {
            string file = Path.Combine(Path.GetFullPath(p.GetValue(path)!), "plugin.json");
            if (!File.Exists(file))
            {
                output.WriteLine($"{file} does not exist.");
                return 2;
            }

            IReadOnlyList<string> errors = PluginManifest.Validate(File.ReadAllText(file));
            foreach (string e in errors)
            {
                output.WriteLine("AMP5270: " + e);
            }

            output.WriteLine(errors.Count == 0 ? "plugin.json: valid" : $"plugin.json: {errors.Count} problem(s)");
            return errors.Count == 0 ? 0 : 1;
        });

        Command run = new("run", "Starts a plugin, does the handshake and asks it to describe itself (a self-test; no render).") { id, root, folder };
        run.SetAction(async (p, t) =>
        {
            DiscoveredPlugin? d = Find(p.GetValue(root), p.GetValue(folder)).Plugins.FirstOrDefault(x => x.Manifest.Id == p.GetValue(id));
            if (d is null)
            {
                output.WriteLine($"AMP5243: there is no plugin '{p.GetValue(id)}'.");
                return 1;
            }

            try
            {
                using PluginHost host = new(VaultSecretProvider.FromEnvironment(Ample.Storage.Library.Library.DefaultHome()));
                JsonElement description = await host.DescribeAsync(d, t);
                output.WriteLine($"{d.Manifest.Id}: handshake ok; {description.GetRawText()}");
                return 0;
            }
            catch (AmpleException ex)
            {
                output.WriteLine($"{ex.DiagnosticCode}: {ex.Message}");
                return 1;
            }
        });

        return [new Command("plugins", "Plugins.") { list, describe, validate, run }];
    }

    internal static (IReadOnlyList<DiscoveredPlugin> Plugins, IReadOnlyList<string> Problems) Find(string? rootFolder, string? extra)
    {
        List<string> folders = [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ample", "plugins")];
        try
        {
            folders.Add(Path.Combine((rootFolder is null ? SpecRoot.Find(Directory.GetCurrentDirectory()) : SpecRoot.At(rootFolder)).Path, "plugins"));
        }
        catch (DirectoryNotFoundException)
        {
            // Not inside an AMPLE folder: only the user folder and --folder are searched.
        }

        if (extra is not null)
        {
            folders.Add(Path.GetFullPath(extra));
        }

        return PluginHost.Discover(folders);
    }
}
