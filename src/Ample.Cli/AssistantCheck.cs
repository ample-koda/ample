using System.Diagnostics;

namespace Ample.Cli;

/// <summary>
/// <c>ample doctor --assistant</c>: is Codex usable for Koda? Optional, never a failure (installer plan, decision 24).
/// It only reads local state: it starts <c>codex --version</c> and looks whether a Codex login file exists (it never reads it).
/// </summary>
internal static class AssistantCheck
{
    internal static (string Level, string Text, string Fix) Run(Func<string, string?>? versionOf = null, Func<bool>? hasLogin = null)
    {
        versionOf ??= Version;
        hasLogin ??= LoginExists;
        string? version = versionOf("codex") ?? (OperatingSystem.IsWindows() ? versionOf("codex.cmd") : null);
        if (version is null)
        {
            return ("note", "Codex is not installed: Koda works in docs-only mode (answers from the documentation, cannot ask follow-up questions).", "Optional. Install the Codex CLI (npm install -g @openai/codex) and sign in to get the full assistant.");
        }

        return hasLogin()
            ? ("ok", $"Codex {version} is installed and signed in: Koda works as the full assistant.", string.Empty)
            : ("note", $"Codex {version} is installed but not signed in: Koda works in docs-only mode.", "Optional. Run: codex login");
    }

    /// <summary>
    /// The optional browser of the assistant (installer plan, decision 26): is the koda-browser pack installed? Never a failure.
    /// <paramref name="home"/> is the AMPLE user folder; the pack lives in its koda-browser folder.
    /// </summary>
    internal static (string Level, string Text, string Fix) Browser(string home)
    {
        string folder = Path.Combine(home, "koda-browser");
        return Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()
            ? ("ok", $"koda-browser is installed ({folder}): Koda can use a web browser when a task needs one.", string.Empty)
            : ("note", "koda-browser is not installed: Koda cannot use a web browser (everything else works).", "Optional. Run: ample packs install koda-browser");
    }
    private static string? Version(string program)
    {
        try
        {
            using Process? p = Process.Start(new ProcessStartInfo(program, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (p is null) return null;
            string text = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Split('\n')[0].Trim();
            if (!p.WaitForExit(8000)) { p.Kill(); return null; }
            return p.ExitCode == 0 && text.Length > 0 ? text : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static bool LoginExists()
    {
        string home = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h ? h : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        return File.Exists(Path.Combine(home, "auth.json"));
    }
}
