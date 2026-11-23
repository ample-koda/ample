using System.Diagnostics;

namespace Ample.Audio.IO;

/// <summary>
/// Makes an NTFS junction (a folder that shows another folder; it needs no special rights) with <c>mklink /J</c>, which is a command of cmd.exe and not a program of
/// its own. cmd reads a few characters as commands (<c>&amp;</c>, <c>|</c>, <c>^</c>, <c>&lt;</c>, <c>&gt;</c>) when they are not inside quotes, and a folder of a project
/// someone else made can be called <c>a&amp;calc</c>: the names are put between quotes here, and a name that cannot be quoted safely is refused (the callers have a fallback).
/// </summary>
public static class Junction
{
    /// <summary>Whether a path can be put between quotes on a cmd.exe command line without cmd reading anything of it (no quote, no % expansion, no control character).</summary>
    public static bool CanQuote(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        foreach (char c in path)
        {
            if (c == '"' || c == '%' || c < ' ') return false;
        }

        return true;
    }

    /// <summary>The argument line for cmd.exe that makes the junction (both paths quoted, the whole command quoted for /s).</summary>
    public static string Arguments(string link, string target) => "/d /s /c \"mklink /J \"" + link + "\" \"" + target + "\"\"";

    /// <summary>Makes the junction <paramref name="link"/> that shows <paramref name="target"/>; false when it could not be made (also when a name cannot be quoted safely).</summary>
    public static bool TryCreate(string link, string target)
    {
        if (!OperatingSystem.IsWindows() || !CanQuote(link) || !CanQuote(target)) return false;
        try
        {
            ProcessStartInfo info = new("cmd.exe", Arguments(link, target)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using Process process = Process.Start(info) ?? throw new InvalidOperationException("cmd.exe did not start.");
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(15000) && process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}
