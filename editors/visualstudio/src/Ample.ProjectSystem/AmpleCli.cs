namespace Ample.VisualStudio;

/// <summary>Finds the AMPLE command line the extension starts, the same one the project's build uses.</summary>
internal static class AmpleCli
{
    /// <summary>
    /// Asks the person whether a project may start a program of its own (project file, program): true to allow. The default says no, so a project
    /// can never start a program before the package has set a real question; the package sets one that shows a message box.
    /// </summary>
    internal static System.Func<string, string, bool> Ask { get; set; } = (_, _) => false;

    /// <summary>The file with the programs the person allowed (one line per project, program and content), one per user.</summary>
    internal static string TrustFile { get; set; } = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "AMPLE", "trusted-programs.txt");

    private static readonly object TrustGate = new();
    private static readonly System.Collections.Generic.Dictionary<string, bool> Decided = new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The CLI for a project: its <c>AmpleExecutable</c> property (only once the person allowed that program for that project; a project of someone else could
    /// otherwise start any program as soon as it is opened), else <c>AMPLE_CLI</c>, else <c>ample.exe</c> on PATH.
    /// </summary>
    /// <param name="projectFile">The .ampleproj file, or null when no project is known (then only the environment and PATH count).</param>
    public static string For(string? projectFile)
    {
        // the machine-local .user file wins over the project file, as it does in MSBuild
        string? fromProject = projectFile is null ? null : ProjectProperty(projectFile + ".user", "AmpleExecutable") ?? ProjectProperty(projectFile, "AmpleExecutable");
        if (!string.IsNullOrWhiteSpace(fromProject) && projectFile is not null && Trusted(projectFile, fromProject!.Trim())) return fromProject.Trim();
        string? fromEnvironment = System.Environment.GetEnvironmentVariable("AMPLE_CLI");
        return string.IsNullOrWhiteSpace(fromEnvironment) ? "ample.exe" : fromEnvironment!.Trim();
    }

    /// <summary>Whether the person allowed this program for this project: remembered in the trust file, asked once otherwise (and once per session when the answer is no).</summary>
    private static bool Trusted(string projectFile, string program)
    {
        string key = System.IO.Path.GetFullPath(projectFile) + "|" + program + "|" + ContentOf(projectFile, program);
        lock (TrustGate)
        {
            if (Decided.TryGetValue(key, out bool known)) return known;
            bool allowed = Remembered(key);
            if (!allowed && Ask(projectFile, program))
            {
                allowed = true;
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(TrustFile)!);
                    System.IO.File.AppendAllText(TrustFile, key + System.Environment.NewLine);
                }
                catch (System.Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException)
                {
                    System.Diagnostics.Trace.WriteLine("AMPLE: the allowed program could not be remembered: " + ex.Message);
                }
            }

            Decided[key] = allowed;
            return allowed;
        }
    }

    private static bool Remembered(string key)
    {
        try
        {
            return System.IO.File.Exists(TrustFile) && System.Linq.Enumerable.Contains(System.IO.File.ReadAllLines(TrustFile), key, System.StringComparer.OrdinalIgnoreCase);
        }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The SHA-256 of the program file when the property names a file (a path), so a program that was changed after it was allowed asks again; "path" for a bare name found on PATH.</summary>
    private static string ContentOf(string projectFile, string program)
    {
        if (program.IndexOfAny(['/', '\\']) < 0) return "path";
        try
        {
            string file = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(projectFile))!, program));
            if (!System.IO.File.Exists(file)) return "missing";
            using System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create();
            using System.IO.FileStream stream = System.IO.File.OpenRead(file);
#if NET5_0_OR_GREATER
            return System.Convert.ToHexString(sha.ComputeHash(stream));
#else
            return System.BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
#endif
        }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException or System.ArgumentException or System.NotSupportedException)
        {
            return "unreadable";
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Probed = new();

    /// <summary>
    /// Whether the command line answers <c>--help</c> of a command with a text that contains <paramref name="expected"/>. An older AMPLE on PATH
    /// (the installed one) has no <c>dap</c> and no <c>lsp --project</c>: asked for it, it prints its main help or an error and the editor sees a lost connection.
    /// </summary>
    public static bool Supports(string cli, string command, string expected) =>
        Probed.GetOrAdd(cli + "|" + command + "|" + expected, _ =>
        {
            try
            {
                System.Diagnostics.ProcessStartInfo start = new(cli, command + " --help")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                };
                using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
                // a stuck command line is killed after 15 s (reading ends then)
                using System.Threading.Timer limit = new(_ => { try { process.Kill(); } catch (System.InvalidOperationException) { } }, null, 15000, System.Threading.Timeout.Infinite);
                string text = process.StandardOutput.ReadToEnd();
                return text.Contains(expected);
            }
            catch (System.Exception ex) when (ex is System.ComponentModel.Win32Exception or System.InvalidOperationException or System.IO.IOException)
            {
                return false;
            }
        });

    /// <summary>The .ampleproj in a folder (the project root), or null when there is none or more than one.</summary>
    public static string? ProjectFileIn(string? folder)
    {
        if (folder is null || !System.IO.Directory.Exists(folder)) return null;
        string[] projects = System.IO.Directory.GetFiles(folder, "*.ampleproj");
        return projects.Length == 1 ? projects[0] : null;
    }

    private static string? ProjectProperty(string projectFile, string name)
    {
        try
        {
            System.Xml.Linq.XElement project = System.Xml.Linq.XElement.Load(projectFile);
            return project.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
        }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.Xml.XmlException or System.UnauthorizedAccessException)
        {
            return null;
        }
    }
}
