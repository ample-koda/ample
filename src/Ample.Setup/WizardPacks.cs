using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Ample.Setup
{
    // The page "Sound packs": its own screen to choose which sample packs this computer has. A ticked pack is wanted here: one that is not installed yet is
    // downloaded, an installed one that is unticked is removed. The licence is spelled out for every pack (installed or not), and a pack that asks for a credit
    // is marked clearly, because most people do not want that.
    internal sealed partial class Wizard
    {
        private sealed class PackInfo
        {
            internal string Id, Tier, Title, License, Author, Description;
            internal long Bytes, InstalledBytes;
            internal bool Installed, CreditRequired, Remove;
            internal CheckBox Box;
        }

        private Control packsPage;
        private readonly FlowLayoutPanel packRows = new FlowLayoutPanel();
        private readonly Label packsStatus = new Label();
        private readonly Label packsSummary = new Label();
        private readonly List<PackInfo> packs = new List<PackInfo>();
        private bool packsLoading, packsLoaded, packsFailed;
        private string packsProblem;
        private readonly ToolTip tips = new ToolTip { AutoPopDelay = 20000 };

        private Control PacksPage()
        {
            Panel p = new Panel();
            p.Controls.Add(new Label { Text = "Sound packs (optional)", Font = new Font("Segoe UI Semibold", 11f), AutoSize = true, ForeColor = Ink, Location = new Point(0, 0) });
            packsStatus.AutoSize = false; packsStatus.Size = new Size(528, 18); packsStatus.Location = new Point(0, 26); packsStatus.ForeColor = Ink;
            packsStatus.Text = "Looking at which packs are installed...";
            int x = 0;
            foreach (string label in new[] { "Only packs without a credit", "All", "None" })
            {
                string choice = label;
                Button b = new Button { Text = label, FlatStyle = FlatStyle.Flat, BackColor = Card, ForeColor = Ink, Height = 24, Width = choice.Length > 6 ? 176 : 56, Top = 48, Left = x };
                b.FlatAppearance.BorderColor = Accent;
                b.Click += (s, e) => { foreach (PackInfo pack in packs.Where(x => !x.Installed)) pack.Box.Checked = choice == "All" || (choice != "None" && !pack.CreditRequired); };
                p.Controls.Add(b);
                x += b.Width + 6;
            }

            packRows.Location = new Point(0, 78); packRows.Size = new Size(528, 140); packRows.AutoScroll = true; packRows.FlowDirection = FlowDirection.TopDown; packRows.WrapContents = false;
            packsSummary.AutoSize = false; packsSummary.Size = new Size(528, 36); packsSummary.Location = new Point(0, 222); packsSummary.ForeColor = Ink;
            p.Controls.AddRange(new Control[] { packsStatus, packRows, packsSummary });
            return p;
        }

        // the packs page needs room: the window grows while it is shown and goes back to its size on the other pages
        private void FitForPage(Control current)
        {
            int height = current == packsPage ? 520 : 380;
            if (ClientSize.Height != height) ClientSize = new Size(560, height);
            back.Top = next.Top = cancel.Top = height - 44;
            if (current == packsPage) packsPage.Height = height - 68 - 54;
            packRows.Height = Math.Max(100, packsPage.Height - 78 - 52);
            packsSummary.Top = 78 + packRows.Height + 6;
        }

        // --shot: waits until the page has what it shows, draws the window into a PNG and closes
        internal async void CaptureAndClose(string file, string pageName, int scroll)
        {
            Control wanted = pageName == "health" ? healthPage : packsPage;
            Show(pages.IndexOf(wanted));
            for (int i = 0; i < 400 && ((wanted == packsPage && !packsLoaded) || (wanted == healthPage && healthBusy)); i++) await Task.Delay(100);
            await Task.Delay(300);
            if (scroll > 0) { packRows.AutoScrollPosition = new Point(0, scroll); await Task.Delay(200); }
            using (Bitmap picture = new Bitmap(Width, Height))
            {
                DrawToBitmap(picture, new Rectangle(0, 0, Width, Height));
                picture.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }

            Close();
        }

        private async Task LoadPacksAsync()
        {
            packsLoading = true; next.Enabled = false; packsProblem = null;
            List<PackInfo> found = await Task.Run(() => ReadPacks());
            packRows.SuspendLayout();
            packRows.Controls.Clear();
            packs.Clear();
            foreach (PackInfo info in found) { packs.Add(info); packRows.Controls.Add(PackRow(info)); }
            packRows.ResumeLayout();
            packsLoaded = packsProblem == null; packsFailed = packsProblem != null; packsLoading = false;   // a failed list waits for the next visit of the page: Show(page) below must not start it again at once
            packsStatus.Text = packs.Count == 0 ? (packsProblem != null ? "The list of packs could not be read: " + packsProblem : "This version of AMPLE has no sound packs to offer.") : "Tick the packs to download. Installed packs stay as they are; use Remove on a pack to take it away.";
            UpdatePacksSummary();
            Show(page);
        }

        private List<PackInfo> ReadPacks()
        {
            List<PackInfo> list = new List<PackInfo>();
            try
            {
                ProcessStartInfo start = new ProcessStartInfo(ample, "packs available --json --home \"" + home + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
                };
                start.EnvironmentVariables["AMPLE_HOME"] = home;
                string json, errors; int exit;
                string timeout = ProcessRunner.Capture(start, 60000, out json, out errors, out exit);
                if (timeout != null) { packsProblem = "listing the packs: " + timeout + ". Go back and forward to try again."; return list; }
                if (json.Trim().Length == 0) { packsProblem = "no answer from ample (exit code " + exit + ")" + (errors.Trim().Length > 0 ? ": " + errors.Trim() : ""); return list; }

                foreach (object o in (object[])new JavaScriptSerializer().DeserializeObject(json))
                {
                    var d = (Dictionary<string, object>)o;
                    PackInfo info = new PackInfo
                    {
                        Id = Field(d, "id"), Tier = Field(d, "tier"), Title = Field(d, "title"), License = Field(d, "license"), Author = Field(d, "author"), Description = Field(d, "description"),
                        Bytes = LongField(d, "bytes"), InstalledBytes = LongField(d, "installedBytes"), Installed = Field(d, "installed") == "True", CreditRequired = Field(d, "creditRequired") == "True",
                    };
                    list.Add(info);
                }
            }
            catch (Exception ex) { packsProblem = ex.Message; }
            return list;
        }

        private static long LongField(Dictionary<string, object> d, string key)
        {
            long value;
            return long.TryParse(Field(d, key), out value) ? value : 0;
        }

        private static string Mb(long bytes) { return Math.Max(1, (int)Math.Round(bytes / 1048576.0)) + " MB"; }

        // one pack: the tick, its name, what the licence asks (always in full), what it holds, and what happens to it
        private Control PackRow(PackInfo info)
        {
            const int width = 488;
            Font small = new Font("Segoe UI", 8.5f);
            Panel row = new Panel { Width = width, BackColor = Card, Margin = new Padding(0, 0, 0, 6), Padding = new Padding(8, 6, 8, 6) };
            int y = 4;
            info.Box = new CheckBox { Text = info.Title, Checked = info.Installed, Enabled = !info.Installed, AutoSize = false, Size = new Size(width - 100, 22), Location = new Point(8, y), ForeColor = Ink, Font = new Font("Segoe UI Semibold", 9.5f) };
            info.Box.CheckedChanged += (s, e) => UpdatePacksSummary();
            row.Controls.Add(info.Box);
            if (info.Installed)
            {
                Button remove = new Button { Text = "Remove", FlatStyle = FlatStyle.Flat, BackColor = Card, ForeColor = Ink, Height = 22, Width = 80, Location = new Point(width - 90, y) };
                remove.FlatAppearance.BorderColor = Bad;
                remove.Click += (s, e) => { info.Remove = !info.Remove; remove.Text = info.Remove ? "Keep" : "Remove"; UpdatePacksSummary(); };
                row.Controls.Add(remove);
            }

            y += 24;

            string licence = info.CreditRequired
                ? "CREDIT REQUIRED. Creative Commons BY: if you publish music made with this pack you must credit " + (info.Author.Length > 0 ? info.Author : "its author") + ". Most people do not want that: leave it unticked" + (info.Installed ? ", or use Remove to take it away" : "") + ". (" + info.License + ")"
                : "No credit needed: " + info.License + ".";
            y = AddWrapped(row, licence, small, info.CreditRequired ? Warn : Good, y, width - 20);

            // what it holds: the first sentence (the whole text is in the tip), so that a few packs fit on the screen
            if (info.Description.Length > 0)
            {
                int stop = info.Description.IndexOf(". ", StringComparison.Ordinal);
                string firstSentence = stop > 0 && stop < 140 ? info.Description.Substring(0, stop + 1) : (info.Description.Length > 140 ? info.Description.Substring(0, 137) + "..." : info.Description);
                y = AddWrapped(row, firstSentence, small, Color.FromArgb(190, 186, 210), y, width - 20);
                tips.SetToolTip(row, info.Description);
            }

            string state = info.Installed ? "Installed on this computer (" + Mb(info.InstalledBytes) + " on disk). Use Remove to take it away." : "Not installed. Download about " + Mb(info.Bytes) + ".";
            y = AddWrapped(row, state, small, info.Installed ? Good : Ink, y, width - 20);
            row.Height = y + 6;
            return row;
        }

        private static int AddWrapped(Panel row, string text, Font font, Color colour, int y, int width)
        {
            int height = TextRenderer.MeasureText(text, font, new Size(width, 0), TextFormatFlags.WordBreak).Height + 2;
            row.Controls.Add(new Label { Text = text, Font = font, ForeColor = colour, AutoSize = false, Size = new Size(width, height), Location = new Point(8, y), UseMnemonic = false });
            return y + height + 2;
        }

        private void UpdatePacksSummary()
        {
            int down = packs.Count(p => p.Box.Checked && !p.Installed), remove = packs.Count(p => p.Remove);
            long downBytes = packs.Where(p => p.Box.Checked && !p.Installed).Sum(p => p.Bytes), removeBytes = packs.Where(p => p.Remove).Sum(p => p.InstalledBytes);
            List<string> credit = packs.Where(p => p.Box.Checked && p.CreditRequired).Select(p => p.Title).ToList();
            StringBuilder text = new StringBuilder();
            text.Append(down == 0 && remove == 0 ? "Nothing changes." : "Next: " + (down > 0 ? down + " to download (about " + Mb(downBytes) + ")" : "") + (down > 0 && remove > 0 ? ", " : "") + (remove > 0 ? remove + " to remove (" + Mb(removeBytes) + ")" : "") + ".");
            if (credit.Count > 0) text.Append(" Ticked and asking for a credit: " + string.Join(", ", credit.Take(3)) + (credit.Count > 3 ? " and " + (credit.Count - 3) + " more" : "") + ".");
            packsSummary.Text = text.ToString();
        }

        // the choices of this page as fixes: a download for a ticked pack that is not installed, a removal for an unticked one that is
        private List<Fix> PackFixes()
        {
            List<Fix> fixes = new List<Fix>();
            foreach (PackInfo p in packs)
            {
                if (p.Box.Checked && !p.Installed) fixes.Add(new Fix { Kind = "pack", Id = p.Id, Title = "Download " + p.Title + " (about " + Mb(p.Bytes) + ")", SizeMb = (int)Math.Max(1, p.Bytes / 1048576) });
                else if (p.Remove) fixes.Add(new Fix { Kind = "pack-remove", Id = p.Id, Title = "Remove " + p.Title + " (" + Mb(p.InstalledBytes) + ")", SizeMb = 0 });
            }

            return fixes;
        }

        private bool ConfirmPackRemoval()
        {
            List<PackInfo> remove = packs.Where(p => p.Remove).ToList();
            if (remove.Count == 0) return true;
            string names = string.Join("\r\n", remove.Take(12).Select(p => "  " + p.Title)) + (remove.Count > 12 ? "\r\n  and " + (remove.Count - 12) + " more" : "");
            return MessageBox.Show(this, "These sound packs will be removed from this computer:\r\n\r\n" + names + "\r\n\r\nYou can download them again here later. Remove them?", "AMPLE Tune-up", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }
    }
}
