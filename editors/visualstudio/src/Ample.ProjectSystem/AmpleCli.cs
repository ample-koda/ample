namespace Ample.VisualStudio;

/// <summary>Finds the AMPLE command line the extension starts, the same one the project's build uses.</summary>
internal static class AmpleCli
{
    /// <summary>The CLI for a project: its <c>AmpleExecutable</c> property, else <c>AMPLE_CLI</c>, else <c>ample.exe</c> on PATH.</summary>
    /// <param name="projectFile">The .ampleproj file, or null when no project is known (then only the environment and PATH count).</param>
    public static string For(string? projectFile)
    {
        // the machine-local .user file wins over the project file, as it does in MSBuild
        string? fromProject = projectFile is null ? null : ProjectProperty(projectFile + ".user", "AmpleExecutable") ?? ProjectProperty(projectFile, "AmpleExecutable");
        if (!string.IsNullOrWhiteSpace(fromProject)) return fromProject!.Trim();
        string? fromEnvironment = System.Environment.GetEnvironmentVariable("AMPLE_CLI");
        return string.IsNullOrWhiteSpace(fromEnvironment) ? "ample.exe" : fromEnvironment!.Trim();
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
