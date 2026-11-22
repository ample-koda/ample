namespace Ample.Lsp;

/// <summary>The files of a project for the editor's suggestions (file and sample-set values): relative paths with '/', without .ample, bin, obj and
/// hidden folders, at most 5,000, read again when the last list is older than three seconds.</summary>
public static class ProjectFiles
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<IReadOnlyList<string>>> Readers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The files of a project, with one shared three-second cache per folder (for the server, which asks on every completion).</summary>
    public static IReadOnlyList<string> Cached(string root) => Readers.GetOrAdd(root, For)();

    /// <summary>A function that gives the files of the project at <paramref name="root"/>, cached for three seconds.</summary>
    public static Func<IReadOnlyList<string>> For(string root)
    {
        IReadOnlyList<string> last = [];
        DateTime read = DateTime.MinValue;
        object gate = new();
        return () =>
        {
            lock (gate)
            {
                if (DateTime.UtcNow - read < TimeSpan.FromSeconds(3)) return last;
                last = List(root);
                read = DateTime.UtcNow;
                return last;
            }
        };
    }

    private static List<string> List(string root)
    {
        List<string> files = [];
        Stack<string> folders = new([root]);
        while (folders.Count > 0 && files.Count < 5000)
        {
            string folder = folders.Pop();
            try
            {
                foreach (string sub in Directory.EnumerateDirectories(folder))
                {
                    string name = Path.GetFileName(sub);
                    if (name.StartsWith('.') || name is "bin" or "obj" or "node_modules") continue;
                    folders.Push(sub);
                }
                foreach (string file in Directory.EnumerateFiles(folder))
                {
                    files.Add(Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'));
                    if (files.Count >= 5000) break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return files;
    }
}
