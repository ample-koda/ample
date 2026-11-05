using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ample.Audio.Random;

namespace Ample.Cli;

/// <summary>
/// <c>ample release manifest</c>: writes the one release manifest that the installers, <c>ample doctor</c> and <c>ample self-update</c> read
/// (installer plan 4.2). Signing is a separate step on the owner's machine: <c>ample sign --file &lt;manifest&gt;</c>; a person checks it with
/// <c>ample verify --file &lt;manifest&gt; --release</c>.
/// </summary>
internal static class ReleaseCommands
{
    internal static IEnumerable<Command> Create(TextWriter output)
    {
        Command root = new("release", "Tools for making a release (for the maintainer).");
        Option<string> version = new("--version") { Description = "The program version, for example 0.9.2 or 1.3.0-rc.1.", Required = true };
        Option<string?> channel = new("--channel") { Description = "stable or unstable (default: unstable when the version has a pre-release suffix, else stable)." };
        Option<string?> from = new("--min-update-from") { Description = "The earliest version that can update straight to this one (default: none stated)." };
        Option<string?> spec = new("--spec-version-file") { Description = "spec/version.json (default: spec/version.json from the current folder)." };
        Option<string?> outFile = new("--out") { Description = "Where to write the manifest (default: ample-<version>.manifest.json in the current folder)." };
        Argument<string[]> files = new("artefacts") { Description = "The release files to list (name, size and SHA-256).", Arity = ArgumentArity.ZeroOrMore };
        Command manifest = new("manifest", "Writes ample-<version>.manifest.json for the given artefacts.") { version, channel, from, spec, outFile, files };
        manifest.SetAction(parse =>
        {
            try
            {
                string path = Write(parse.GetValue(version)!, parse.GetValue(channel), parse.GetValue(from), parse.GetValue(spec), parse.GetValue(outFile), parse.GetValue(files) ?? [], DateTimeOffset.UtcNow);
                output.WriteLine("Wrote " + path);
                output.WriteLine("Sign it with: ample sign --file " + path);
                return 0;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                output.WriteLine(ex.Message);
                return 2;
            }
        });
        root.Subcommands.Add(manifest);
        yield return root;
    }

    internal static string Write(string version, string? channel, string? minUpdateFrom, string? specFile, string? outFile, string[] artefacts, DateTimeOffset now)
    {
        bool pre = version.Contains('-', StringComparison.Ordinal);
        string[] core = version.Split('-')[0].Split('.');
        if (core.Length != 3 || core.Any(p => !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            throw new ArgumentException($"'{version}' is not a version like 1.2.3 or 1.3.0-rc.1.");
        }

        string chan = channel ?? (pre ? "unstable" : "stable");
        if (chan is not ("stable" or "unstable") || (chan == "stable" && pre))
        {
            throw new ArgumentException("The channel must be stable or unstable, and a pre-release version belongs to unstable.");
        }

        string specPath = specFile ?? Path.Combine("spec", "version.json");
        if (!File.Exists(specPath))
        {
            throw new FileNotFoundException($"The language version file '{specPath}' was not found.");
        }

        string language = JsonNode.Parse(File.ReadAllText(specPath))?["specVersion"]?.GetValue<string>() ?? "unknown";
        JsonArray items = [];
        foreach (string a in artefacts.OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            using FileStream s = File.OpenRead(a);
            items.Add(new JsonObject { ["name"] = Path.GetFileName(a), ["size"] = new FileInfo(a).Length, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(s)) });
        }

        JsonObject doc = new()
        {
            ["schema"] = 1,
            ["product"] = "AMPLE",
            ["version"] = version,
            ["channel"] = chan,
            ["beta"] = true,
            ["date"] = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["language"] = new JsonObject { ["reads"] = language },
            ["renderer"] = new JsonObject { ["rngVersion"] = RngVersion.Current },
            ["packs"] = new JsonObject { ["soundfont"] = new JsonObject { ["name"] = "MuseScore_General.sf2", ["url"] = PackInstall.BaseUrl + "MuseScore_General.sf2", ["sha256"] = PackInstall.Sha256, ["licence"] = "MIT" } },
            ["minUpdateFrom"] = minUpdateFrom,
            ["artefacts"] = items,
        };
        string path = Path.GetFullPath(outFile ?? $"ample-{version}.manifest.json");
        File.WriteAllText(path, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return path;
    }
}
