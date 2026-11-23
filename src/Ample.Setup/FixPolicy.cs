using System;
using System.IO;

namespace Ample.Setup
{
    /// <summary>
    /// What the elevated copy of the wizard (<c>--run-fix</c>, started with "runas") accepts. It runs with administrator rights on arguments that anyone who can
    /// start a program as this person can write, and the person only sees the Windows prompt, so it refuses anything but the few shapes the wizard itself makes:
    /// a known kind of fix with a plain id, the <c>ample.exe</c> that sits next to the wizard (never another program), and a progress file the wizard made in the
    /// temporary folder.
    /// </summary>
    internal static class FixPolicy
    {
        /// <summary>The reason a fix is refused, or null when its kind, id and scope are the shapes the wizard makes.</summary>
        internal static string CheckFix(string kind, string id, string scope)
        {
            if (kind != "pack" && kind != "pack-remove" && kind != "winget") return "Unknown kind of fix: " + kind;
            if (!IsPlainId(id)) return "The name of the fix is not a plain package name.";
            if (!string.IsNullOrEmpty(scope) && scope != "user" && scope != "machine") return "The scope of the fix is not user or machine.";
            return null;
        }

        /// <summary>A pack name or winget id: letters, digits, '.', '_' and '-', not starting with a symbol (never a space, a quote or an option such as "--x").</summary>
        internal static bool IsPlainId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 100 || !char.IsLetterOrDigit(id[0])) return false;
            foreach (char c in id)
            {
                bool letter = c < 128 && char.IsLetterOrDigit(c);
                if (!letter && c != '.' && c != '_' && c != '-') return false;
            }

            return true;
        }

        /// <summary>The ample.exe next to the wizard when it exists, else null; the path given on the command line is not used.</summary>
        internal static string TrustedAmple(string wizardFolder)
        {
            string path = Path.Combine(wizardFolder, "ample.exe");
            return File.Exists(path) ? path : null;
        }

        /// <summary>True when the file is one the wizard makes (<c>ample-setup-&lt;32 hex&gt;.progress</c>) directly in the temporary folder.</summary>
        internal static bool IsOwnProgressFile(string path, string tempFolder)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception) { return false; }
            string folder = Path.GetFullPath(tempFolder).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(full), folder, StringComparison.OrdinalIgnoreCase)) return false;
            string name = Path.GetFileName(full);
            const string Prefix = "ample-setup-", Suffix = ".progress";
            if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(Suffix, StringComparison.Ordinal)) return false;
            string middle = name.Substring(Prefix.Length, name.Length - Prefix.Length - Suffix.Length);
            if (middle.Length != 32) return false;
            foreach (char c in middle)
            {
                if (!Uri.IsHexDigit(c) || char.IsUpper(c)) return false;
            }

            return true;
        }

        /// <summary>True when the AMPLE data folder is an absolute path that holds no quote or control character (it is put between quotes on a command line).</summary>
        internal static bool IsPlainHome(string home)
        {
            if (string.IsNullOrEmpty(home) || !Path.IsPathRooted(home)) return false;
            foreach (char c in home)
            {
                if (c == '"' || c < ' ') return false;
            }

            return true;
        }
    }
}
