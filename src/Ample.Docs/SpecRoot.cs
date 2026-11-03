using System.Text.Json;
using Ample.Language;

namespace Ample.Docs;

/// <summary>Locates and reads the AMPLE spec data (<c>spec/</c>) and docs (<c>docs/</c>).</summary>
public sealed class SpecRoot
{
    private SpecRoot(string path) => Path = path;

    /// <summary>Gets the absolute path of the AMPLE folder (the one containing <c>spec</c> and <c>docs</c>).</summary>
    public string Path { get; }

    /// <summary>Finds the AMPLE folder starting at <paramref name="start"/> and walking up, or throws when none is found.</summary>
    public static SpecRoot Find(string start)
    {
        // An installed copy keeps spec/, docs/, web/ and plugins/ next to the program: look there too when the folder we are in is not an AMPLE folder.
        return Walk(start) ?? Walk(AppContext.BaseDirectory) ?? throw new DirectoryNotFoundException($"No AMPLE folder with spec/ and docs/ found at or above '{start}' or next to the program.");
    }

    private static SpecRoot? Walk(string start)
    {
        for (DirectoryInfo? dir = new(System.IO.Path.GetFullPath(start)); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(System.IO.Path.Combine(dir.FullName, "spec")) && Directory.Exists(System.IO.Path.Combine(dir.FullName, "docs")))
            {
                return new SpecRoot(dir.FullName);
            }

            string nested = System.IO.Path.Combine(dir.FullName, "ample");
            if (Directory.Exists(System.IO.Path.Combine(nested, "spec")) && Directory.Exists(System.IO.Path.Combine(nested, "docs")))
            {
                return new SpecRoot(nested);
            }
        }

        return null;
    }

    /// <summary>Creates a root for a known folder.</summary>
    public static SpecRoot At(string path) => new(System.IO.Path.GetFullPath(path));

    /// <summary>Combines segments with the AMPLE folder.</summary>
    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Reads a JSON file under the AMPLE folder.</summary>
    public JsonDocument ReadJson(params string[] parts)
    {
        using FileStream stream = File.OpenRead(Combine(parts));
        return JsonDocument.Parse(stream);
    }

    /// <summary>Reads all JSON files under <c>spec/</c> into the language library's data.</summary>
    public SpecData LoadSpecData()
    {
        string spec = Combine("spec");
        Dictionary<string, string> files = Directory.GetFiles(spec, "*.json", SearchOption.AllDirectories)
            .ToDictionary(f => System.IO.Path.GetRelativePath(spec, f).Replace('\\', '/'), File.ReadAllText);
        return SpecData.FromFiles(files);
    }

    /// <summary>Gets the spec version from <c>spec/version.json</c>.</summary>
    public string SpecVersion
    {
        get
        {
            using JsonDocument doc = ReadJson("spec", "version.json");
            return doc.RootElement.GetProperty("specVersion").GetString() ?? "unknown";
        }
    }
}
