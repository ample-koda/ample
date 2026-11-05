using Ample.Audio.IO;
using System.CommandLine;
using System.Diagnostics;

namespace Ample.Cli;

/// <summary>The <c>ample packs</c> commands for inspecting and linking installed audio packs.</summary>
internal static class PackCommands
{
    internal static Command Create(TextWriter output)
    {
        Command root = new("packs", "Lists and links the user-level installed audio packs.");
        Option<string?> home = new("--home") { Description = "The AMPLE user folder (default: AMPLE_HOME, else the local application data of the user)." };
        Command list = new("list", "Lists the pack folders in lookup order.") { home };
        list.SetAction(parse =>
        {
            string value = parse.GetValue(home) ?? AmpleHome.DefaultPath();
            foreach (string folder in AmpleHome.PackFolders(value, null, Directory.GetCurrentDirectory())) output.WriteLine($"{(Directory.Exists(folder) ? "available" : "missing")}  {folder}");
            return 0;
        });

        Argument<string?> asset = new("asset") { Description = "A path below an installed pack (default: the MuseScore General SoundFont).", Arity = ArgumentArity.ZeroOrOne };
        Option<string?> whereHome = new("--home") { Description = "The AMPLE user folder (default: AMPLE_HOME, else the local application data of the user)." };
        Command where = new("where", "Shows where an installed pack file is found.") { asset, whereHome };
        where.SetAction(parse =>
        {
            string value = parse.GetValue(whereHome) ?? AmpleHome.DefaultPath();
            string relative = parse.GetValue(asset) ?? "licensed/musescore-general/MuseScore_General.sf2";
            try
            {
                PackLookup result = AmpleHome.LookupPackFile(value, null, Directory.GetCurrentDirectory(), relative);
                if (result.Path is not null)
                {
                    output.WriteLine(result.Path);
                    return 0;
                }

                output.WriteLine("Not found. Searched:");
                foreach (string path in result.Searched) output.WriteLine(path);
                return 1;
            }
            catch (ArgumentException exception)
            {
                output.WriteLine(exception.Message);
                return 2;
            }
        });

        Argument<string> folder = new("folder") { Description = "Existing audio-packs folder to link into the user-level AMPLE home." };
        Option<string?> linkHome = new("--home") { Description = "The AMPLE user folder (default: AMPLE_HOME, else the local application data of the user)." };
        Command link = new("link", "Creates a junction to an existing audio-packs folder; it never copies packs.") { folder, linkHome };
        link.SetAction(parse => Link(output, parse.GetValue(folder)!, parse.GetValue(linkHome) ?? AmpleHome.DefaultPath()));
        Argument<string> what = new("what") { Description = "What to install: soundfont (the General MIDI SoundFont, pinned by SHA-256) or koda-browser (the optional browser of the assistant)." };
        Option<string?> installHome = new("--home") { Description = "The AMPLE user folder (default: AMPLE_HOME, else the local application data of the user)." };
        Option<string?> from = new("--from") { Description = "Fetch from this base URL instead of the pinned one (a mirror; the SHA-256 is still checked). For koda-browser: the folder that holds the release manifest and the pack." };
        Option<bool> mirror = new("--mirror") { Description = "soundfont: fetch from the AMPLE mirror (get.ample-koda.dev) instead of the original host. The SHA-256 is still checked. Cannot be combined with --from." };
        Option<bool> driverOnly = new("--driver-only") { Description = "koda-browser: install the driver but do not let Playwright download Chromium." };
        Option<bool> progressOption = new("--progress") { Description = "Also print lines 'progress <percent or -1> <text>' that say what is happening (for an installer that shows them)." };
        Command install = new("install", "Fetches a pack and verifies it; an existing file is verified and kept.") { what, installHome, from, mirror, driverOnly, progressOption };
        install.SetAction(async (parse, token) =>
        {
            if (parse.GetValue(mirror) && parse.GetValue(from) is not null)
            {
                output.WriteLine("Use either --mirror or --from, not both.");
                return 2;
            }

            Action<int, string>? progress = parse.GetValue(progressOption) ? (percent, text) => output.WriteLine($"progress {percent} {text}") : null;
            if (parse.GetValue(mirror) && string.Equals(parse.GetValue(what), "koda-browser", StringComparison.OrdinalIgnoreCase))
            {
                output.WriteLine("There is no AMPLE mirror of koda-browser; it comes from the release itself (use --from for your own folder).");
                return 2;
            }

            if (string.Equals(parse.GetValue(what), "koda-browser", StringComparison.OrdinalIgnoreCase))
            {
                string version = (typeof(PackCommands).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0").Split('+')[0];
                string baseUrl = parse.GetValue(from) ?? $"https://github.com/ample-koda/ample/releases/download/ample-v{version}";
                return await KodaBrowserInstall.RunAsync(output, parse.GetValue(installHome) ?? AmpleHome.DefaultPath(), version, baseUrl, parse.GetValue(driverOnly), null, token, progress);
            }

            if (!string.Equals(parse.GetValue(what), "soundfont", StringComparison.OrdinalIgnoreCase))
            {
                output.WriteLine("Only 'soundfont' and 'koda-browser' can be installed so far.");
                return 2;
            }

            return await PackInstall.SoundFontAsync(output, parse.GetValue(installHome) ?? AmpleHome.DefaultPath(), parse.GetValue(mirror) ? PackInstall.MirrorUrl : parse.GetValue(from), token, progress);
        });
        root.Subcommands.Add(install);
        root.Subcommands.Add(list);
        root.Subcommands.Add(where);
        root.Subcommands.Add(link);
        return root;
    }

    private static int Link(TextWriter output, string folder, string home)
    {
        string source = Path.GetFullPath(folder);
        if (!Directory.Exists(source))
        {
            output.WriteLine($"The pack folder '{source}' does not exist.");
            return 2;
        }

        string destination = Path.Combine(Path.GetFullPath(home), "audio-packs");
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            output.WriteLine($"The user pack folder already exists: {destination}");
            return 1;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using Process junction = Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "/d", "/c", "mklink", "/J", destination, source } })!;
                junction.WaitForExit();
                if (junction.ExitCode != 0) throw new IOException("Windows could not create the pack junction.");
            }
            else
            {
                Directory.CreateSymbolicLink(destination, source);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            output.WriteLine($"Could not link the pack folder: {exception.Message}");
            return 1;
        }

        output.WriteLine($"Linked {destination} to {source}");
        return 0;
    }
}
