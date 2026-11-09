using System.Reflection;

namespace Ample.Language;

/// <summary>
/// The components that ship with AMPLE (content/components, embedded in this assembly). Every song sees them by name, without an import line:
/// when the text of a song names one, the registry adds its file as an implicit import. Their versions are fixed: a better sound is a new version.
/// </summary>
public static class BuiltInComponents
{
    /// <summary>The target that stands for a built-in file in the registry (never a path in a project).</summary>
    public const string Prefix = "builtin:";

    /// <summary>The id prefix of every built-in component (no note about being unsealed for these).</summary>
    public const string IdPrefix = "ample.builtin.";

    private static readonly Lazy<IReadOnlyList<(string File, string Text, IReadOnlyList<string> Names)>> All = new(Load);

    /// <summary>The file names of the built-in components (for example voice-clean.amplec).</summary>
    public static IReadOnlyList<string> Files => [.. All.Value.Select(f => f.File)];

    /// <summary>The text of a built-in file (the target is builtin:file or just the file name), or null.</summary>
    public static string? Read(string target)
    {
        string file = target.StartsWith(Prefix, StringComparison.Ordinal) ? target[Prefix.Length..] : target;
        foreach ((string name, string text, _) in All.Value) if (string.Equals(name, file, StringComparison.OrdinalIgnoreCase)) return text;
        return null;
    }

    /// <summary>The built-in files whose components a song text names (a plain search of the dotted name: the registry checks the rest).</summary>
    public static IReadOnlyList<string> Mentioned(string songText) =>
        [.. All.Value.Where(f => f.Names.Any(n => songText.Contains(n, StringComparison.Ordinal))).Select(f => f.File)];

    /// <summary>True when a song text names a built-in component, so the song may have components even without an import or a declaration.</summary>
    public static bool MentionedIn(string songText) => Mentioned(songText).Count > 0;

    private static List<(string File, string Text, IReadOnlyList<string> Names)> Load()
    {
        List<(string File, string Text, IReadOnlyList<string> Names)> files = [];
        Assembly assembly = typeof(BuiltInComponents).Assembly;
        foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith("components/", StringComparison.Ordinal) && n.EndsWith(".amplec", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using Stream? stream = assembly.GetManifestResourceStream(resource);
            if (stream is null) continue;
            using StreamReader reader = new(stream);
            string text = reader.ReadToEnd();
            List<string> names = [];
            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.TrimStart();
                if (!trimmed.StartsWith("component ", StringComparison.Ordinal)) continue;
                string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1) names.Add(parts[1]);
            }

            files.Add((resource["components/".Length..], text, names));
        }

        return files;
    }
}
