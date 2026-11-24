using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Ample.Setup
{
    /// <summary>
    /// What the installer did with the optional extras (a sound pack, the Koda browser, Audiveris), kept in a small text file of the person who installed: one line per extra,
    /// <c>pack TAB state TAB message</c>, with the state installed, failed or skipped. The installer writes it (install/msi/CustomActions/AmpleCa.cs); the Tune-up reads it, says what failed
    /// and offers to try again, so a program that installed without its download is not mistaken for a complete one (issue 318).
    /// </summary>
    internal static class ExtrasResult
    {
        internal sealed class Entry
        {
            internal string Pack, State, Message;
        }

        /// <summary>The place of the file: Ample\install-extras.txt in the local application data of the person.</summary>
        internal static string PathIn(string localAppData) => System.IO.Path.Combine(localAppData, "Ample", "install-extras.txt");

        /// <summary>The entries of the file text; a line that is not of the form is left out.</summary>
        internal static List<Entry> Parse(string text)
        {
            List<Entry> entries = new List<Entry>();
            foreach (string line in (text ?? string.Empty).Replace("\r", string.Empty).Split('\n'))
            {
                string[] parts = line.Split('\t');
                if (parts.Length < 2 || parts[0].Length == 0) continue;
                string state = parts[1].Trim().ToLowerInvariant();
                if (state != "installed" && state != "failed" && state != "skipped") continue;
                entries.Add(new Entry { Pack = parts[0].Trim(), State = state, Message = parts.Length > 2 ? parts[2].Trim() : string.Empty });
            }

            return entries;
        }

        /// <summary>The line of one result: tabs and new lines in the message are spaces.</summary>
        internal static string Line(string pack, string state, string message)
        {
            string clean = (message ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
            return pack + "\t" + state + "\t" + clean;
        }

        /// <summary>
        /// The sentence for the Tune-up: the extras of the last installation that failed and are still not there, each with its reason, and how to try again; null when nothing failed.
        /// </summary>
        /// <param name="entries">The result of the last installation.</param>
        /// <param name="isInstalled">Whether an extra is installed now (it may have been fetched since).</param>
        internal static string Describe(IEnumerable<Entry> entries, Func<string, bool> isInstalled) => Describe(entries, isInstalled, _ => true);

        /// <summary>
        /// The same, for the packs page: only a pack that is in its list can be ticked there. Something else that failed (audiveris, the koda browser) is not in the list, so
        /// it was said to be tickable and could never be ticked: it is named apart, with the command that fetches it.
        /// </summary>
        /// <param name="entries">The result of the last installation.</param>
        /// <param name="isInstalled">Whether an extra is installed now.</param>
        /// <param name="isInList">Whether the extra is a pack in the list of the page.</param>
        internal static string Describe(IEnumerable<Entry> entries, Func<string, bool> isInstalled, Func<string, bool> isInList)
        {
            List<Entry> failed = (entries ?? Enumerable.Empty<Entry>()).Where(e => e.State == "failed" && !isInstalled(e.Pack)).ToList();
            if (failed.Count == 0) return null;
            List<Entry> tickable = failed.Where(e => isInList(e.Pack)).ToList();
            List<Entry> other = failed.Where(e => !isInList(e.Pack)).ToList();
            StringBuilder text = new StringBuilder();
            if (tickable.Count > 0)
            {
                text.Append("The last installation could not fetch: ");
                text.Append(string.Join("; ", tickable.Select(Shown)));
                text.Append(". Tick it below and press Next to try again.");
            }

            if (other.Count > 0)
            {
                if (text.Length > 0) text.Append(' ');
                text.Append(tickable.Count > 0 ? "Not in the list (it is no sound pack): " : "The last installation could not fetch: ");
                text.Append(string.Join("; ", other.Select(Shown)));
                text.Append(". Fetch it with: ").Append(string.Join(", ", other.Select(e => "ample packs install " + e.Pack))).Append('.');
            }

            return text.ToString();
        }

        private static string Shown(Entry e) => e.Pack + (e.Message.Length > 0 ? " (" + e.Message + ")" : string.Empty);
    }
}
