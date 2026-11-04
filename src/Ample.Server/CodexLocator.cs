namespace Ample.Server;

/// <summary>Finds the Codex that this machine has, in a way that never needs a shell: the executable, or <c>node</c> plus the <c>codex.js</c> of the npm install.</summary>
internal static class CodexLocator
{
    private static readonly Dictionary<string, (string File, string[] Prefix)?> Cache = new(StringComparer.Ordinal);

    /// <summary>The file to start and the arguments that come before the ones of Codex, or null when Codex cannot be found.</summary>
    public static (string File, string[] Prefix)? Find(string? configured)
    {
        string key = configured ?? string.Empty;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out (string File, string[] Prefix)? known) && (known is null || File.Exists(known.Value.Prefix.Length > 0 ? known.Value.Prefix[0] : known.Value.File)))
            {
                return known;
            }

            (string File, string[] Prefix)? found = Search(configured ?? Environment.GetEnvironmentVariable("AMPLE_CODEX"));
            Cache[key] = found;
            return found;
        }
    }

    private static (string File, string[] Prefix)? Search(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!File.Exists(configured))
            {
                return null;
            }

            return configured.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? (Node(Path.GetDirectoryName(configured)), [configured]) : (configured, []);
        }

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    string exe = Path.Combine(dir, "codex.exe");
                    if (File.Exists(exe))
                    {
                        return (exe, []);
                    }

                    if (File.Exists(Path.Combine(dir, "codex.cmd")) && PackageScript(dir) is { } script)
                    {
                        return (Node(dir), [script]);
                    }
                }
                else
                {
                    string exe = Path.Combine(dir, "codex");
                    if (File.Exists(exe))
                    {
                        return PackageScript(Path.GetDirectoryName(exe)!) is { } script && exe.Contains("node", StringComparison.OrdinalIgnoreCase) ? (Node(dir), [script]) : (exe, []);
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // a path entry that cannot be read is skipped
            }
        }

        return null;
    }

    private static string? PackageScript(string dir)
    {
        foreach (string candidate in new[] { Path.Combine(dir, "node_modules", "@openai", "codex", "bin", "codex.js"), Path.Combine(dir, "..", "lib", "node_modules", "@openai", "codex", "bin", "codex.js") })
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    /// <summary>The node that goes with an npm install: the one next to the shim when there is one, else the one on the path.</summary>
    private static string Node(string? dir)
    {
        string name = OperatingSystem.IsWindows() ? "node.exe" : "node";
        return dir is not null && File.Exists(Path.Combine(dir, name)) ? Path.Combine(dir, name) : name;
    }
}
